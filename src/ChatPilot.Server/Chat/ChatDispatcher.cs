using System.Globalization;
using System.Text;
using ChatPilot.Server.Devices;
using ChatPilot.Server.Options;
using ChatPilot.Server.Security;
using ChatPilot.Server.State;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Chat;

/// <summary>执行聊天指令：找设备、找 agent、发给节点、整理回复。只服务一个主人。</summary>
public sealed class ChatDispatcher(
    DeviceHub hub,
    StateStore store,
    PairingService pairing,
    ServerIdentity identity,
    IOptions<ServerOptions> options,
    ILogger<ChatDispatcher> logger)
{
    /// <summary>管家 agent 的固定名字。</summary>
    public const string ButlerName = "butler";

    /// <summary>创建管家时发给它的说明。</summary>
    internal const string ButlerInstructions = """
        你是这台电脑的管家 agent，主人会通过手机把消息转给你。
        这台电脑用 herdr 管理多个编码 agent。先运行 `herdr --skill` 学会 herdr 的用法，
        之后用 `herdr agent list`、`herdr agent read`、`herdr agent prompt` 等命令查看和指挥其他 agent。
        规则：
        1. 主人的消息可能很简短，先弄清楚要哪个 agent 做什么，再分派任务；
        2. 没有主人明确要求时，不要关闭、删除任何 agent、工作区或文件；
        3. 每次回复都用简短的中文总结你做了什么、结果如何，因为主人在手机上看。
        现在只回复"管家已就绪"。
        """;

    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Lock _listingLock = new();
    private List<ListedAgent> _lastListing = [];

    private sealed record ListedAgent(DeviceRecord Device, AgentSummary Agent);

    private sealed record Resolved(DeviceRecord Device, AgentSummary Agent)
    {
        /// <summary>回复里显示的名字，例如 <c>[mac] tideq</c>。</summary>
        public string Label => $"[{Device.Name}] {Agent.DisplayName}";

        /// <summary>
        /// 发给节点的目标：优先用 agent 名字。名字在 agent 退出时会被清除，
        /// 如果那个 pane 已经换成了别的程序，用名字会找不到而不是发错对象。
        /// </summary>
        public string Target => Agent.Name ?? Agent.PaneId;
    }

    private TimeSpan CommandTimeout => TimeSpan.FromSeconds(options.Value.CommandTimeoutSeconds);

    /// <summary>
    /// 处理一条主人的消息，返回回复文字。不会抛异常（取消除外）。
    /// 所有消息严格排队、一条一条处理：先发的 <c>/use</c> 一定在后发的指令之前生效，<c>@序号</c> 用的一定是之前那次 <c>/ls</c> 的结果。
    /// </summary>
    public async Task<string> HandleAsync(string text, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HandleCoreAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<string> HandleCoreAsync(string text, CancellationToken cancellationToken)
    {
        var command = ChatCommandParser.Parse(text);
        try
        {
            return command switch
            {
                ChatHelp => ReplyFormatter.Help,
                ChatDevices => Devices(),
                ChatUse use => Use(use.Device),
                ChatList list => await ListAsync(list.Device, cancellationToken).ConfigureAwait(false),
                ChatRead read => await ReadAsync(read, cancellationToken).ConfigureAwait(false),
                ChatLast last => await LastReplyAsync(last, cancellationToken).ConfigureAwait(false),
                ChatPrompt prompt => await PromptAsync(prompt, cancellationToken).ConfigureAwait(false),
                ChatButlerPrompt butler => await ButlerPromptAsync(butler.Text, cancellationToken).ConfigureAwait(false),
                ChatNew create => await NewAsync(create, cancellationToken).ConfigureAwait(false),
                ChatRespond respond => await RespondAsync(respond, cancellationToken).ConfigureAwait(false),
                ChatKeys keys => await KeysAsync(keys, cancellationToken).ConfigureAwait(false),
                ChatRename rename => await RenameAsync(rename, cancellationToken).ConfigureAwait(false),
                ChatButler butler => await CreateButlerAsync(butler.Device, cancellationToken).ConfigureAwait(false),
                ChatPair pair => Pair(pair.DeviceName),
                ChatUnpair unpair => await UnpairAsync(unpair.Device).ConfigureAwait(false),
                ChatInvalid invalid => "❓ " + invalid.Message,
                _ => "❓ 不支持的指令",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理指令 {Command} 时出错", command.GetType().Name);
            return "❌ 服务器内部错误，详情见服务器日志";
        }
    }

    private string Devices()
    {
        var devices = store.Devices();
        if (devices.Count == 0)
        {
            return "还没有设备。发 /pair 生成配对码，然后在电脑上运行节点程序配对。";
        }

        var selected = store.Read(s => s.SelectedDeviceId);
        var builder = new StringBuilder("📱 设备列表\n");
        foreach (var device in devices)
        {
            builder.Append('\n').Append(hub.IsOnline(device.Id) ? "🟢 " : "⚪ ").Append(device.Name);
            if (device.Id == selected)
            {
                builder.Append("（当前）");
            }

            if (hub.Session(device.Id) is { } session)
            {
                builder.Append(" · ").Append(session.HostName).Append(" · ").Append(session.Platform);
            }
            else
            {
                builder.Append(" · 离线");
                if (device.LastSeenAt is { } seen)
                {
                    builder.Append("，最后在线 ").Append(seen.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
                }
            }
        }

        return builder.ToString();
    }

    private string Use(string name)
    {
        if (store.FindDevice(name) is not { } device)
        {
            return "❓ " + UnknownDevice(name);
        }

        store.Update(s => s.SelectedDeviceId = device.Id);
        return hub.IsOnline(device.Id)
            ? $"👌 默认设备切换为 {device.Name}"
            : $"👌 默认设备切换为 {device.Name}（目前离线）";
    }

    private async Task<string> ListAsync(string? deviceName, CancellationToken cancellationToken)
    {
        IReadOnlyList<DeviceRecord> devices;
        if (deviceName is not null)
        {
            if (store.FindDevice(deviceName) is not { } device)
            {
                return "❓ " + UnknownDevice(deviceName);
            }

            devices = [device];
        }
        else
        {
            devices = store.Devices();
        }

        if (devices.Count == 0)
        {
            return "还没有设备。发 /pair 生成配对码。";
        }

        var results = await Task.WhenAll(devices.Select(async d =>
            (Device: d, Outcome: hub.IsOnline(d.Id)
                ? await hub.SendAsync(d.Id, new ListAgentsCommand(), CommandTimeout, cancellationToken).ConfigureAwait(false)
                : null))).ConfigureAwait(false);

        var selected = store.Read(s => s.SelectedDeviceId);
        var listing = new List<ListedAgent>();
        var builder = new StringBuilder();
        foreach (var (device, outcome) in results)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append("📱 ").Append(device.Name).Append(device.Id == selected ? "（当前）" : "");
            if (outcome is null)
            {
                builder.Append(" · 离线");
                continue;
            }

            if (!outcome.Ok || outcome.Data is not AgentListData data)
            {
                builder.Append('\n').Append(ReplyFormatter.Failure(outcome));
                continue;
            }

            if (data.Agents.Count == 0)
            {
                builder.Append("\n  （没有 agent）");
                continue;
            }

            foreach (var agent in data.Agents)
            {
                listing.Add(new ListedAgent(device, agent));
                builder.Append('\n')
                    .Append("  ").Append(listing.Count.ToString(CultureInfo.InvariantCulture)).Append(". ")
                    .Append(ReplyFormatter.StatusIcon(agent.Status)).Append(' ')
                    .Append(agent.DisplayName)
                    .Append(" · ").Append(agent.Kind ?? "?")
                    .Append(" · ").Append(ReplyFormatter.StatusText(agent.Status));
                if (agent.Name is null && agent.WorkspaceLabel is not null)
                {
                    builder.Append(" · ").Append(agent.PaneId);
                }

                if (!string.IsNullOrWhiteSpace(agent.Title))
                {
                    builder.Append("\n     ").Append(agent.Title);
                }
            }
        }

        lock (_listingLock)
        {
            _lastListing = listing;
        }

        if (listing.Count > 0)
        {
            builder.Append("\n\n用 @序号 内容 派活，例如 @1 跑一下测试");
        }

        return builder.ToString();
    }

    private async Task<string> ReadAsync(ChatRead command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new ReadCommand(resolved.Target, command.Lines), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome is { Ok: true, Data: TextData data }
            ? $"🖥 {resolved.Label} 最近的输出：\n\n{data.Text}"
            : ReplyFormatter.Failure(outcome);
    }

    private async Task<string> LastReplyAsync(ChatLast command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new LastReplyCommand(resolved.Target), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome is { Ok: true, Data: TextData data }
            ? ReplyFormatter.LastReply(resolved.Label, data.Text)
            : ReplyFormatter.Failure(outcome);
    }

    private async Task<string> PromptAsync(ChatPrompt command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new PromptCommand(resolved.Target, command.Text), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Ok
            ? $"📨 已发给 {resolved.Label}，做完会通知你"
            : ReplyFormatter.Failure(outcome);
    }

    private async Task<string> ButlerPromptAsync(string text, CancellationToken cancellationToken)
    {
        var (device, error) = DefaultDevice();
        if (device is null)
        {
            return "❓ " + error + "\n（直接发文字会交给管家；要指定 agent 请用 @目标 内容）";
        }

        var outcome = await hub.SendAsync(device.Id, new PromptCommand(ButlerName, text), CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (outcome.Ok)
        {
            return $"📨 已交给 [{device.Name}] 的管家，做完会通知你";
        }

        return outcome.Error?.Code == ErrorCodes.NotFound
            ? $"🤖 [{device.Name}] 还没有管家。发 /butler 创建一个，或者用 @目标 内容 直接指定 agent（/ls 查看）。"
            : ReplyFormatter.Failure(outcome);
    }

    private async Task<string> NewAsync(ChatNew command, CancellationToken cancellationToken)
    {
        var (device, error) = command.Device is null ? DefaultDevice() : OnlineDevice(command.Device);
        if (device is null)
        {
            return "❓ " + error;
        }

        return await StartAgentAsync(device, new StartAgentCommand(command.Directory, command.Kind, command.Name, null), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> CreateButlerAsync(string? deviceName, CancellationToken cancellationToken)
    {
        var (device, error) = deviceName is null ? DefaultDevice() : OnlineDevice(deviceName);
        if (device is null)
        {
            return "❓ " + error;
        }

        return await StartAgentAsync(device, new StartAgentCommand(null, null, ButlerName, ButlerInstructions), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> StartAgentAsync(DeviceRecord device, StartAgentCommand command, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.StartAgentTimeoutSeconds);
        var outcome = await hub.SendAsync(device.Id, command, timeout, cancellationToken).ConfigureAwait(false);
        if (outcome is not { Ok: true, Data: AgentStartedData started })
        {
            return ReplyFormatter.Failure(outcome);
        }

        var agent = started.Agent;
        var target = $"{device.Name}/{agent.Name ?? agent.PaneId}";
        var header = $"🚀 已在 [{device.Name}] 启动 {agent.DisplayName}（{agent.Kind ?? "?"}）\n目录：{agent.Cwd}";
        return started.Ready
            ? $"{header}\n\n发 @{target} 内容 给它派活"
            : $"{header}\n\n⚠️ 它在等你确认：\n\n{started.Screen}\n\n回复 /yes {target} 同意，/no {target} 拒绝";
    }

    private async Task<string> RespondAsync(ChatRespond command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new RespondCommand(resolved.Target, command.Approve), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome is { Ok: true, Data: AckData ack }
            ? $"👌 {resolved.Label}：{ack.Message}"
            : ReplyFormatter.Failure(outcome);
    }

    private async Task<string> KeysAsync(ChatKeys command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new SendKeysCommand(resolved.Target, command.Keys), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome is { Ok: true, Data: AckData ack } ? $"👌 {resolved.Label}：{ack.Message}" : ReplyFormatter.Failure(outcome);
    }

    private async Task<string> RenameAsync(ChatRename command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new RenameAgentCommand(resolved.Target, command.Name), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome is { Ok: true, Data: AckData ack } ? $"👌 {resolved.Label}：{ack.Message}" : ReplyFormatter.Failure(outcome);
    }

    private string Pair(string? deviceName)
    {
        var code = pairing.Create(deviceName);
        var server = identity.PublicUrl ?? "<服务器地址>";
        var fingerprint = identity.Fingerprint is { } fp ? $" --fingerprint {fp}" : "";
        var name = deviceName is null ? "" : $" --name {deviceName}";
        var hint = identity.PublicUrl is null ? "\n\n（服务器没配置 PublicUrl，请把 <服务器地址> 换成实际地址，例如 https://你的域名:8443）" : "";
        return $"""
            🔑 配对码：{code.Display}
            {options.Value.PairingCodeMinutes} 分钟内有效，只能用一次。

            在要接入的电脑上运行：
            chatpilot-node pair --server {server} --code {code.Display}{fingerprint}{name}{hint}
            """;
    }

    private async Task<string> UnpairAsync(string name)
    {
        if (store.FindDevice(name) is not { } device)
        {
            return "❓ " + UnknownDevice(name);
        }

        // 撤销必须立即生效：哪怕写盘失败，也先从内存里删掉并断开连接。
        var saved = store.UpdateBestEffort(state =>
        {
            state.Devices.RemoveAll(d => d.Id == device.Id);
            if (state.SelectedDeviceId == device.Id)
            {
                state.SelectedDeviceId = null;
            }
        });
        await hub.DisconnectAsync(device.Id).ConfigureAwait(false);
        lock (_listingLock)
        {
            _lastListing.RemoveAll(l => l.Device.Id == device.Id);
        }

        logger.LogInformation("设备 {Device} 已撤销", device.Name);
        return saved
            ? $"🗑 已撤销设备 {device.Name}，它的令牌立即失效。"
            : $"⚠️ 已撤销设备 {device.Name} 并断开连接，但保存到磁盘失败：服务器重启后它会恢复。请检查服务器磁盘后再发一次 /unpair {device.Name}。";
    }

    /// <summary>默认设备：已选的设备；没选时如果只有一台在线就用它。</summary>
    private (DeviceRecord? Device, string? Error) DefaultDevice()
    {
        var selectedId = store.Read(s => s.SelectedDeviceId);
        if (selectedId is not null && store.FindDevice(selectedId) is { } selected)
        {
            return hub.IsOnline(selected.Id) ? (selected, null) : (null, $"默认设备 {selected.Name} 离线。可以 /use 换一台");
        }

        var online = store.Devices().Where(d => hub.IsOnline(d.Id)).ToList();
        return online.Count switch
        {
            1 => (online[0], null),
            0 => (null, "没有在线的设备"),
            _ => (null, "有多台设备在线，先 /use 选一台：" + string.Join("、", online.Select(d => d.Name))),
        };
    }

    private (DeviceRecord? Device, string? Error) OnlineDevice(string name)
    {
        if (store.FindDevice(name) is not { } device)
        {
            return (null, UnknownDevice(name));
        }

        return hub.IsOnline(device.Id) ? (device, null) : (null, $"设备 {device.Name} 离线");
    }

    /// <summary>
    /// 找 agent：序号用上次 /ls 的结果；其他按 名字 / pane ID 精确匹配，找不到再按工作区名匹配（都不区分大小写）。
    /// 没指定设备时在所有在线设备里找，多处匹配时优先默认设备，仍有歧义就让用户指定。
    /// </summary>
    private async Task<(Resolved? Resolved, string? Error)> ResolveAsync(TargetRef target, CancellationToken cancellationToken)
    {
        if (target.IsIndex)
        {
            var index = int.Parse(target.Value, CultureInfo.InvariantCulture);
            ListedAgent? listed;
            lock (_listingLock)
            {
                listed = index >= 1 && index <= _lastListing.Count ? _lastListing[index - 1] : null;
            }

            if (listed is null)
            {
                return (null, $"没有序号 {index}。先发 /ls 看看列表");
            }

            if (target.Device is not null && !string.Equals(target.Device, listed.Device.Name, StringComparison.OrdinalIgnoreCase))
            {
                return (null, $"序号 {index} 在设备 {listed.Device.Name} 上，不在 {target.Device}");
            }

            return hub.IsOnline(listed.Device.Id)
                ? (new Resolved(listed.Device, listed.Agent), null)
                : (null, $"设备 {listed.Device.Name} 离线");
        }

        List<DeviceRecord> candidates;
        if (target.Device is not null)
        {
            var (device, error) = OnlineDevice(target.Device);
            if (device is null)
            {
                return (null, error);
            }

            candidates = [device];
        }
        else
        {
            candidates = store.Devices().Where(d => hub.IsOnline(d.Id)).ToList();
            if (candidates.Count == 0)
            {
                return (null, "没有在线的设备");
            }
        }

        var lists = await Task.WhenAll(candidates.Select(async d =>
            (Device: d, Outcome: await hub.SendAsync(d.Id, new ListAgentsCommand(), CommandTimeout, cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);

        var all = lists
            .SelectMany(l => l.Outcome is { Ok: true, Data: AgentListData data }
                ? data.Agents.Select(a => new Resolved(l.Device, a))
                : [])
            .ToList();
        var matches = all.Where(r =>
                string.Equals(r.Agent.Name, target.Value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Agent.PaneId, target.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            matches = all.Where(r => string.Equals(r.Agent.WorkspaceLabel, target.Value, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count == 1)
        {
            return (matches[0], null);
        }

        if (matches.Count == 0)
        {
            var failed = lists.Where(l => !l.Outcome.Ok).Select(l => $"{l.Device.Name}（{l.Outcome.Error?.Message}）").ToList();
            var note = failed.Count > 0 ? "。另外这些设备查询失败：" + string.Join("、", failed) : "";
            return (null, $"找不到 {target}。发 /ls 查看有哪些 agent{note}");
        }

        var selectedId = store.Read(s => s.SelectedDeviceId);
        var onSelected = matches.Where(m => m.Device.Id == selectedId).ToList();
        if (onSelected.Count == 1)
        {
            return (onSelected[0], null);
        }

        return (null, $"{target} 有多个匹配，请写得更具体：" +
            string.Join("、", matches.Select(m => $"{m.Device.Name}/{m.Agent.Name ?? m.Agent.PaneId}")));
    }

    private string UnknownDevice(string name)
    {
        var names = store.Devices().Select(d => d.Name).ToList();
        return names.Count == 0
            ? $"没有叫 {name} 的设备，目前还没有配对任何设备"
            : $"没有叫 {name} 的设备。现有设备：{string.Join("、", names)}";
    }
}
