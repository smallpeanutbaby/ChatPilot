using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChatPilot.Server.Devices;
using ChatPilot.Server.Options;
using ChatPilot.Server.Security;
using ChatPilot.Server.State;
using ChatPilot.Shared.Net;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Chat;

/// <summary>执行聊天指令：找设备、找 agent、发给节点、整理回复。只服务一个主人。</summary>
public sealed class ChatDispatcher(
    DeviceHub hub,
    StateStore store,
    PairingService pairing,
    ServerIdentity identity,
    ConversationState conversation,
    TaskHistory history,
    MediaStore media,
    UserClock clock,
    IOptions<ServerOptions> options,
    ILogger<ChatDispatcher> logger)
{
    /// <summary>管家 agent 的固定名字。</summary>
    public const string ButlerName = "butler";

    /// <summary>最多同时有几个定时任务。</summary>
    internal const int MaxScheduledJobs = 20;

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

    /// <summary><c>/close</c> 的确认有效期。</summary>
    private static readonly TimeSpan CloseConfirmWindow = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Lock _listingLock = new();
    private List<ListedAgent> _lastListing = [];
    private PendingClose? _pendingClose;

    private sealed record ListedAgent(DeviceRecord Device, AgentSummary Agent);

    /// <summary>发过确认提示、等主人确认的 <c>/close</c>。</summary>
    private sealed record PendingClose(string DeviceId, string PaneId, DateTimeOffset Expires);

    private sealed record Resolved(DeviceRecord Device, AgentSummary Agent)
    {
        /// <summary>回复里显示的名字，例如 <c>[mac] tideq</c>。</summary>
        public string Label => $"[{Device.Name}] {Agent.DisplayName}";

        /// <summary>
        /// 发给节点的目标：优先用 agent 名字。名字在 agent 退出时会被清除，
        /// 如果那个 pane 已经换成了别的程序，用名字会找不到而不是发错对象。
        /// </summary>
        public string Target => Agent.Name ?? Agent.PaneId;

        /// <summary>写给用户看、能直接复制的目标，例如 <c>mac/tideq</c>。</summary>
        public string Address => $"{Device.Name}/{Target}";
    }

    private TimeSpan CommandTimeout => TimeSpan.FromSeconds(options.Value.CommandTimeoutSeconds);

    /// <summary>
    /// 处理一条主人的消息，返回回复文字。不会抛异常（取消除外）。
    /// 所有消息严格排队、一条一条处理：先发的 <c>/use</c> 一定在后发的指令之前生效，<c>@序号</c> 用的一定是之前那次 <c>/ls</c> 的结果。
    /// </summary>
    public Task<string> HandleAsync(string text, CancellationToken cancellationToken) =>
        SerialAsync(() => HandleCoreAsync(text, scheduled: false, cancellationToken), cancellationToken);

    /// <summary>执行到点的定时任务。和主人发消息一样排队处理，但不会带走主人暂存的图片。</summary>
    public Task<string> HandleScheduledAsync(string text, CancellationToken cancellationToken) =>
        SerialAsync(() => HandleCoreAsync(text, scheduled: true, cancellationToken), cancellationToken);

    /// <summary>主人发来一张图片：暂存起来，等下一条派活的指令一起发出去。</summary>
    public Task<string> HandleImageAsync(byte[] data, CancellationToken cancellationToken) =>
        SerialAsync(() =>
        {
            var (count, error) = media.Stage(data);
            return Task.FromResult(error is not null
                ? "❓ " + error
                : $"🖼 收到图片（共 {count} 张待发）。接下来用 @目标 内容 派活时会一起带上，{MediaStore.Lifetime.TotalMinutes:0} 分钟内有效；不要了发 /drop");
        }, cancellationToken);

    private async Task<string> SerialAsync(Func<Task<string>> work, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<string> HandleCoreAsync(string text, bool scheduled, CancellationToken cancellationToken)
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
                ChatPending => await PendingAsync(cancellationToken).ConfigureAwait(false),
                ChatRead read => await ReadAsync(read, cancellationToken).ConfigureAwait(false),
                ChatLast last => await LastReplyAsync(last, cancellationToken).ConfigureAwait(false),
                ChatPrompt prompt => await PromptAsync(prompt.Target, prompt.Text, withImages: !scheduled, cancellationToken).ConfigureAwait(false),
                ChatReply reply => await ReplyAsync(reply.Text, cancellationToken).ConfigureAwait(false),
                ChatButlerPrompt butler => await ButlerPromptAsync(butler.Text, withImages: !scheduled, cancellationToken).ConfigureAwait(false),
                ChatNew create => await NewAsync(create, cancellationToken).ConfigureAwait(false),
                ChatRespond respond => await RespondAsync(respond, cancellationToken).ConfigureAwait(false),
                ChatKeys keys => await KeysAsync(keys, cancellationToken).ConfigureAwait(false),
                ChatRename rename => await RenameAsync(rename, cancellationToken).ConfigureAwait(false),
                ChatDiff diff => await DiffAsync(diff, cancellationToken).ConfigureAwait(false),
                ChatStop stop => await StopAsync(stop, cancellationToken).ConfigureAwait(false),
                ChatClose close => await CloseAsync(close, cancellationToken).ConfigureAwait(false),
                ChatWatchList => await WatchListAsync(cancellationToken).ConfigureAwait(false),
                ChatWatch watch => await WatchAsync(watch, cancellationToken).ConfigureAwait(false),
                ChatWatchAll watchAll => await WatchAllAsync(watchAll, cancellationToken).ConfigureAwait(false),
                ChatHistory recent => History(recent.Count),
                ChatAtList => ScheduledJobs(),
                ChatAt at => await ScheduleAsync(at, cancellationToken).ConfigureAwait(false),
                ChatAtCancel cancel => CancelJob(cancel.Id),
                ChatWake wake => await WakeAsync(wake.Device, cancellationToken).ConfigureAwait(false),
                ChatDropImages => DropImages(),
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
                builder.Append(" · ").Append(session.HostName).Append(" · ").Append(session.Platform).Append(" · 节点 ").Append(session.NodeVersion);
            }
            else
            {
                builder.Append(" · 离线");
                if (device.LastSeenAt is { } seen)
                {
                    builder.Append("，最后在线 ").Append(clock.Format(seen));
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

        var results = await QueryAgentsAsync(devices, cancellationToken).ConfigureAwait(false);
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

                if (agent.Queued > 0)
                {
                    builder.Append(" · 排队 ").Append(agent.Queued);
                }

                if (agent.Watched || data.WatchAll)
                {
                    builder.Append(" · 👀");
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

    /// <summary>向每台在线设备要 agent 列表；离线的设备结果为 null。</summary>
    private async Task<(DeviceRecord Device, CommandOutcome? Outcome)[]> QueryAgentsAsync(IEnumerable<DeviceRecord> devices, CancellationToken cancellationToken) =>
        await Task.WhenAll(devices.Select(async d =>
            (Device: d, Outcome: hub.IsOnline(d.Id)
                ? await hub.SendAsync(d.Id, new ListAgentsCommand(), CommandTimeout, cancellationToken).ConfigureAwait(false)
                : null))).ConfigureAwait(false);

    /// <summary><c>/pending</c>：在等确认的、在干活的（多久了、排了几条）、定时任务和暂存的图片。</summary>
    private async Task<string> PendingAsync(CancellationToken cancellationToken)
    {
        var online = store.Devices().Where(d => hub.IsOnline(d.Id)).ToList();
        var results = await QueryAgentsAsync(online, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var blocked = new List<string>();
        var busy = new List<string>();
        var failed = new List<string>();
        foreach (var (device, outcome) in results)
        {
            if (outcome is not { Ok: true, Data: AgentListData data })
            {
                failed.Add($"{device.Name}（{outcome?.Error?.Message ?? "离线"}）");
                continue;
            }

            foreach (var agent in data.Agents)
            {
                var address = $"{device.Name}/{agent.Name ?? agent.PaneId}";
                var label = $"[{device.Name}] {agent.DisplayName}";
                if (agent.Status == "blocked")
                {
                    blocked.Add($"  {label} · /yes {address}");
                }
                else if (agent.Status == "working" || agent.Queued > 0 || agent.TaskSince is not null)
                {
                    var line = new StringBuilder("  ").Append(label).Append(" · ").Append(ReplyFormatter.StatusText(agent.Status));
                    if (agent.TaskSince is { } since)
                    {
                        line.Append(" · 已经 ").Append(ReplyFormatter.Duration(now - since));
                    }

                    if (agent.Queued > 0)
                    {
                        line.Append(" · 排队 ").Append(agent.Queued).Append(" 条");
                    }

                    busy.Add(line.ToString());
                }
            }
        }

        var builder = new StringBuilder("📋 待办");
        if (blocked.Count > 0)
        {
            builder.Append("\n\n⚠️ 在等你确认\n").AppendJoin('\n', blocked);
        }

        if (busy.Count > 0)
        {
            builder.Append("\n\n⏳ 在干活\n").AppendJoin('\n', busy);
        }

        var jobs = store.Read(s => s.ScheduledJobs.Count);
        if (jobs > 0)
        {
            builder.Append("\n\n⏰ 定时任务 ").Append(jobs).Append(" 个（/at 查看）");
        }

        if (media.PendingCount is var images and > 0)
        {
            builder.Append("\n\n🖼 暂存的图片 ").Append(images).Append(" 张，下一条派活时一起发出");
        }

        if (failed.Count > 0)
        {
            builder.Append("\n\n这些设备查询失败：").AppendJoin('、', failed);
        }

        if (blocked.Count == 0 && busy.Count == 0 && jobs == 0 && media.PendingCount == 0 && failed.Count == 0)
        {
            builder.Append(online.Count == 0 ? "\n\n没有在线的设备" : "\n\n👌 没有在等确认或在干活的 agent");
        }

        return builder.ToString();
    }

    private async Task<string> ReadAsync(ChatRead command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOrRecentAsync(command.Target, cancellationToken).ConfigureAwait(false);
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
        var (resolved, error) = await ResolveOrRecentAsync(command.Target, cancellationToken).ConfigureAwait(false);
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

    private async Task<string> PromptAsync(TargetRef target, string text, bool withImages, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        return resolved is null ? "❓ " + error : await SendPromptAsync(resolved, text, withImages, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>/re</c>：交给最近打过交道的 agent。</summary>
    private async Task<string> ReplyAsync(string text, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOrRecentAsync(null, cancellationToken).ConfigureAwait(false);
        return resolved is null ? "❓ " + error : await SendPromptAsync(resolved, text, withImages: true, cancellationToken).ConfigureAwait(false);
    }

    private Task<string> SendPromptAsync(Resolved resolved, string text, bool withImages, CancellationToken cancellationToken) =>
        SendTextAsync(resolved.Device, resolved.Target, text, withImages, resolved.Label, $"已发给 {resolved.Label}", notFound: null, cancellationToken);

    private async Task<string> ButlerPromptAsync(string text, bool withImages, CancellationToken cancellationToken)
    {
        var (device, error) = DefaultDevice();
        if (device is null)
        {
            return "❓ " + error + "\n（直接发文字会交给管家；要指定 agent 请用 @目标 内容）";
        }

        var label = $"[{device.Name}] 的管家";
        return await SendTextAsync(
            device, ButlerName, text, withImages, label, $"已交给 {label}",
            notFound: $"🤖 [{device.Name}] 还没有管家。发 /butler 创建一个，或者用 @目标 内容 直接指定 agent（/ls 查看）。",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把文字（和暂存的图片）发给 agent。有图片时用 <see cref="MediaPromptCommand"/>：旧节点不认识它会回"不支持"，图片留着不丢。
    /// 节点直接交出去时用固定说法回复，排队等其他情况转述节点的说明。
    /// </summary>
    /// <param name="device">设备。</param>
    /// <param name="target">发给节点的目标。</param>
    /// <param name="text">文字。</param>
    /// <param name="withImages">要不要带上暂存的图片（定时任务不带）。</param>
    /// <param name="label">回复里的名字。</param>
    /// <param name="sentPhrase">直接交出去时的说法，例如"已发给 [mac] tideq"。</param>
    /// <param name="notFound">找不到目标时的回复；为 null 时用节点的说明。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<string> SendTextAsync(
        DeviceRecord device, string target, string text, bool withImages, string label, string sentPhrase, string? notFound, CancellationToken cancellationToken)
    {
        var attachments = withImages ? media.Issue(device.Id) : null;
        NodeCommand command = attachments is null ? new PromptCommand(target, text) : new MediaPromptCommand(target, text, attachments);
        var outcome = await hub.SendAsync(device.Id, command, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            var failure = notFound is not null && outcome.Error?.Code == ErrorCodes.NotFound ? notFound : ReplyFormatter.Failure(outcome);
            return attachments is null ? failure : failure + "\n\n图片还暂存着，处理好再派活会一起带上；不要了发 /drop";
        }

        if (attachments is not null)
        {
            media.ClearPending();
        }

        var withImageNote = attachments is null ? "" : $"（带 {attachments.Count} 张图片）";
        return outcome.Data is AckData { Message: var message } && message != AckData.Sent
            ? $"📨 {label}{withImageNote}：{message}"
            : $"📨 {sentPhrase}{withImageNote}，做完会通知你";
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
        conversation.Touch(AgentRef.From(device.Id, device.Name, agent));
        var target = $"{device.Name}/{agent.Name ?? agent.PaneId}";
        var header = $"🚀 已在 [{device.Name}] 启动 {agent.DisplayName}（{agent.Kind ?? "?"}）\n目录：{agent.Cwd}";
        return started.Ready
            ? $"{header}\n\n发 @{target} 内容 给它派活"
            : $"{header}\n\n⚠️ 它在等你确认：\n\n{started.Screen}\n\n回复 /yes {target} 同意，/no {target} 拒绝";
    }

    private async Task<string> RespondAsync(ChatRespond command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOrRecentAsync(command.Target, cancellationToken, preferBlocked: true).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new RespondCommand(resolved.Target, command.Approve), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (outcome is { Ok: true, Data: AckData ack })
        {
            conversation.Responded(resolved.Device.Id, resolved.Agent.PaneId);
            return $"👌 {resolved.Label}：{ack.Message}";
        }

        return ReplyFormatter.Failure(outcome);
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
        if (outcome is { Ok: true, Data: AckData ack })
        {
            // 以后用新名字找它。
            conversation.Touch(AgentRef.From(resolved.Device.Id, resolved.Device.Name, resolved.Agent with { Name = command.Name }));
            return $"👌 {resolved.Label}：{ack.Message}";
        }

        return ReplyFormatter.Failure(outcome);
    }

    /// <summary><c>/diff</c>：agent 工作目录里的 git 改动。</summary>
    private async Task<string> DiffAsync(ChatDiff command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOrRecentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new DiffCommand(resolved.Target, command.Path), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (outcome is not { Ok: true, Data: TextData data })
        {
            return ReplyFormatter.Failure(outcome);
        }

        var title = command.Path is null ? $"🔀 {resolved.Label} 的改动：" : $"🔀 {resolved.Label} 的 {command.Path}：";
        return $"{title}\n\n{data.Text}";
    }

    /// <summary><c>/stop</c>：打断 agent，清空它排队的指令。</summary>
    private async Task<string> StopAsync(ChatStop command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOrRecentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new StopAgentCommand(resolved.Target), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome is { Ok: true, Data: AckData ack } ? $"✋ {resolved.Label}：{ack.Message}" : ReplyFormatter.Failure(outcome);
    }

    /// <summary>
    /// <c>/close</c>：第一次只给出要关闭的是哪个 agent，并记下来；2 分钟内带"确认"再发一次、并且还是同一个 pane，才真正关闭。
    /// </summary>
    private async Task<string> CloseAsync(ChatClose command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var now = clock.UtcNow;
        var pending = _pendingClose;
        if (command.Confirmed && pending is not null && pending.Expires > now
            && pending.DeviceId == resolved.Device.Id && pending.PaneId == resolved.Agent.PaneId)
        {
            _pendingClose = null;
            var outcome = await hub.SendAsync(resolved.Device.Id, new CloseAgentCommand(resolved.Target), CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (outcome is { Ok: true, Data: AckData ack })
            {
                conversation.ForgetAgent(resolved.Device.Id, resolved.Agent.PaneId);
                lock (_listingLock)
                {
                    _lastListing.RemoveAll(l => l.Device.Id == resolved.Device.Id && l.Agent.PaneId == resolved.Agent.PaneId);
                }

                return $"🗑 {resolved.Label}：{ack.Message}";
            }

            return ReplyFormatter.Failure(outcome);
        }

        _pendingClose = new PendingClose(resolved.Device.Id, resolved.Agent.PaneId, now + CloseConfirmWindow);
        var agent = resolved.Agent;
        var expired = command.Confirmed ? "确认已经过期或者对不上，请重新确认。\n\n" : "";
        return $"""
            {expired}⚠️ 要关闭 {resolved.Label} 吗？
            {agent.Kind ?? "?"} · {ReplyFormatter.StatusText(agent.Status)} · {agent.Cwd}
            终端和 agent 进程都会结束，正在做的事会中断。

            确认请在 {CloseConfirmWindow.TotalMinutes:0} 分钟内回复：/close {resolved.Address} 确认
            """;
    }

    /// <summary><c>/watch</c>：每台在线设备在盯着哪些 agent。</summary>
    private async Task<string> WatchListAsync(CancellationToken cancellationToken)
    {
        var online = store.Devices().Where(d => hub.IsOnline(d.Id)).ToList();
        if (online.Count == 0)
        {
            return "没有在线的设备";
        }

        var results = await QueryAgentsAsync(online, cancellationToken).ConfigureAwait(false);
        var lines = new List<string>();
        foreach (var (device, outcome) in results)
        {
            if (outcome is not { Ok: true, Data: AgentListData data })
            {
                lines.Add($"📱 {device.Name}：查询失败（{outcome?.Error?.Message}）");
            }
            else if (data.WatchAll)
            {
                lines.Add($"📱 {device.Name}：盯着全部 agent");
            }
            else if (data.Agents.Where(a => a.Watched).Select(a => a.DisplayName).ToList() is { Count: > 0 } watched)
            {
                lines.Add($"📱 {device.Name}：盯着 {string.Join("、", watched)}");
            }
            else
            {
                lines.Add($"📱 {device.Name}：没有盯着任何 agent");
            }
        }

        return "👀 盯梢\n\n" + string.Join('\n', lines)
            + "\n\n盯着的 agent 在电脑上做完或卡在确认框时也会通知你。/watch 目标 开始盯，/watch all 盯全部，/unwatch 停止";
    }

    private async Task<string> WatchAsync(ChatWatch command, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return "❓ " + error;
        }

        var outcome = await hub.SendAsync(resolved.Device.Id, new WatchCommand(resolved.Target, command.Enable), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome is { Ok: true, Data: AckData ack } ? $"👀 [{resolved.Device.Name}] {ack.Message}" : ReplyFormatter.Failure(outcome);
    }

    /// <summary><c>/watch all [设备]</c>：没写设备时对所有在线设备生效。</summary>
    private async Task<string> WatchAllAsync(ChatWatchAll command, CancellationToken cancellationToken)
    {
        List<DeviceRecord> devices;
        if (command.Device is not null)
        {
            var (device, error) = OnlineDevice(command.Device);
            if (device is null)
            {
                return "❓ " + error;
            }

            devices = [device];
        }
        else
        {
            devices = store.Devices().Where(d => hub.IsOnline(d.Id)).ToList();
            if (devices.Count == 0)
            {
                return "❓ 没有在线的设备";
            }
        }

        var results = await Task.WhenAll(devices.Select(async d =>
            (Device: d, Outcome: await hub.SendAsync(d.Id, new WatchCommand(null, command.Enable), CommandTimeout, cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);
        return string.Join('\n', results.Select(r => r.Outcome is { Ok: true, Data: AckData ack }
            ? $"👀 [{r.Device.Name}] {ack.Message}"
            : $"❌ [{r.Device.Name}] {r.Outcome.Error?.Message ?? "失败了"}"));
    }

    /// <summary><c>/history</c>：今天的统计和最近几条任务。</summary>
    private string History(int count)
    {
        var entries = history.Snapshot();
        if (entries.Count == 0)
        {
            return "📜 还没有任务记录。通过 ChatPilot 派的活做完后会记在这里";
        }

        var today = clock.Now.Date;
        var todays = entries.Where(e => clock.ToLocal(e.At).Date == today).ToList();
        var worked = todays.Where(e => e.StartedAt is not null).Aggregate(TimeSpan.Zero, (sum, e) => sum + (e.At - e.StartedAt!.Value));
        var builder = new StringBuilder("📜 最近的任务");
        builder.Append("\n今天：做完 ").Append(todays.Count(e => e.Succeeded)).Append(" 个，出错 ").Append(todays.Count(e => !e.Succeeded)).Append(" 个");
        if (worked > TimeSpan.Zero)
        {
            builder.Append("，一共干了 ").Append(ReplyFormatter.Duration(worked));
        }

        foreach (var entry in entries.TakeLast(count).Reverse())
        {
            builder.Append("\n\n").Append(clock.Format(entry.At)).Append(entry.Succeeded ? " ✅ " : " ❌ ")
                .Append('[').Append(entry.Device).Append("] ").Append(entry.Agent);
            if (entry.StartedAt is { } started)
            {
                builder.Append(" · ").Append(ReplyFormatter.Duration(entry.At - started));
            }

            if (entry.Prompt is not null)
            {
                builder.Append("\n  任务：").Append(entry.Prompt);
            }

            if (entry.Result is not null)
            {
                builder.Append("\n  结果：").Append(entry.Result);
            }
        }

        return builder.ToString();
    }

    private string ScheduledJobs()
    {
        var jobs = store.Read(s => s.ScheduledJobs.OrderBy(j => j.DueAt).ToList());
        if (jobs.Count == 0)
        {
            return "⏰ 没有定时任务。用 /at 时间 @目标 内容 新建，例如 /at 23:00 @api 跑全量测试、/at 30m @2 看看测试结果";
        }

        var builder = new StringBuilder("⏰ 定时任务（").Append(clock.OffsetLabel(clock.UtcNow)).Append('）');
        foreach (var job in jobs)
        {
            builder.Append("\n\n#").Append(job.Id).Append(' ').Append(clock.Format(job.DueAt))
                .Append("\n  ").Append(TextChunker.Truncate(job.Text.ReplaceLineEndings(" "), 80));
        }

        return builder.Append("\n\n取消用 /at del 编号").ToString();
    }

    /// <summary>
    /// <c>/at</c>：算出执行时间（按主人的时区），存进状态文件，由 <see cref="Scheduler"/> 到点执行。
    /// <c>@序号</c> 在这里就换成 <c>@设备/名字</c>：到点时的 <c>/ls</c> 序号可能已经变了。
    /// </summary>
    private async Task<string> ScheduleAsync(ChatAt command, CancellationToken cancellationToken)
    {
        if (store.Read(s => s.ScheduledJobs.Count) >= MaxScheduledJobs)
        {
            return $"❓ 定时任务最多 {MaxScheduledJobs} 个，先用 /at del 编号 取消一些";
        }

        var text = command.Text;
        if (ChatCommandParser.Parse(text) is ChatPrompt { Target.IsIndex: true } prompt)
        {
            var (resolved, error) = await ResolveAsync(prompt.Target, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return "❓ " + error;
            }

            text = $"@{resolved.Address} {prompt.Text}";
        }

        var due = DueTime(command.When);
        var id = 0;
        store.Update(state =>
        {
            id = state.NextJobId++;
            state.ScheduledJobs.Add(new ScheduledJob(id, due, text, clock.UtcNow));
        });
        return $"⏰ 好的，定时任务 #{id} 会在 {clock.Format(due)}（{clock.OffsetLabel(due)}，{ReplyFormatter.Duration(due - clock.UtcNow)}后）执行：\n{TextChunker.Truncate(text, 200)}\n\n取消用 /at del {id}";
    }

    /// <summary>执行时间（UTC）：相对时长直接加；时刻按主人的时区算，今天已经过了（或者写了"明天"）就是明天。</summary>
    internal DateTimeOffset DueTime(ScheduleTime when)
    {
        ArgumentNullException.ThrowIfNull(when);
        var now = clock.UtcNow;
        if (when.Delay is { } delay)
        {
            return now + delay;
        }

        var local = clock.ToLocal(now);
        var date = local.Date.AddDays(when.Tomorrow ? 1 : 0);
        var candidate = date.AddHours(when.Hour).AddMinutes(when.Minute);
        if (!when.Tomorrow && candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        // 夏令时切换时不存在的时刻往后挪到存在的时刻。
        while (clock.Zone.IsInvalidTime(candidate))
        {
            candidate = candidate.AddMinutes(30);
        }

        return new DateTimeOffset(candidate, clock.Zone.GetUtcOffset(candidate)).ToUniversalTime();
    }

    private string CancelJob(int id)
    {
        var removed = false;
        store.Update(state => removed = state.ScheduledJobs.RemoveAll(j => j.Id == id) > 0);
        return removed ? $"🗑 已取消定时任务 #{id}" : $"❓ 没有定时任务 #{id}，发 /at 查看";
    }

    /// <summary>
    /// <c>/wake</c>：让同一局域网里的其他在线设备广播唤醒包。按上线时记下的网段挑同一网段的设备；
    /// 一台都对不上时让所有在线设备都试一次（网段判断不一定准，多发几个包没有坏处）。
    /// </summary>
    private async Task<string> WakeAsync(string name, CancellationToken cancellationToken)
    {
        if (store.FindDevice(name) is not { } target)
        {
            return "❓ " + UnknownDevice(name);
        }

        if (hub.IsOnline(target.Id))
        {
            return $"🟢 {target.Name} 在线，不用唤醒";
        }

        if (target.Networks is not { Count: > 0 } networks)
        {
            return $"❓ 还不知道 {target.Name} 的网卡地址：它的节点升级到新版、上线一次之后才能唤醒";
        }

        var relays = store.Devices().Where(d => d.Id != target.Id && hub.IsOnline(d.Id)).ToList();
        if (relays.Count == 0)
        {
            return $"❓ 没有其他在线的设备能帮忙：唤醒包只能从 {target.Name} 所在的局域网里发出";
        }

        var sameLan = relays.Where(r => SharesSubnet(r.Networks, networks)).ToList();
        var chosen = sameLan.Count > 0 ? sameLan : relays;
        var macs = networks.Select(n => n.Mac).Distinct(StringComparer.Ordinal).ToList();
        var results = await Task.WhenAll(chosen.Select(async r =>
            (Device: r, Outcome: await hub.SendAsync(r.Id, new WakeCommand(macs), CommandTimeout, cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);
        var sent = results.Where(r => r.Outcome.Ok).Select(r => r.Device.Name).ToList();
        if (sent.Count == 0)
        {
            return "❌ 唤醒包没有发出去：" + string.Join("；", results.Select(r => $"{r.Device.Name}（{r.Outcome.Error?.Message}）"));
        }

        var note = sameLan.Count > 0 ? "" : "\n（没找到和它在同一网段的在线设备，所有在线设备都试了一次）";
        return $"⏰ 已让 {string.Join("、", sent)} 在局域网里广播唤醒包。{target.Name} 醒来、节点连上后用 /devices 查看。" +
            $"\n电脑要在 BIOS / 网卡设置里打开 Wake-on-LAN，并且接着电源、用网线上网才唤得醒。{note}";
    }

    /// <summary>两台设备有没有在同一个 IPv4 网段的网卡（按目标网卡的前缀长度比较）。</summary>
    internal static bool SharesSubnet(IReadOnlyList<NetworkInterfaceInfo>? relay, IReadOnlyList<NetworkInterfaceInfo> target)
    {
        if (relay is null)
        {
            return false;
        }

        foreach (var t in target)
        {
            if (!TryParseIpv4(t.Address, out var targetIp) || t.PrefixLength is <= 0 or > 32)
            {
                continue;
            }

            if (relay.Any(r => TryParseIpv4(r.Address, out var relayIp)
                && Ipv4.Network(targetIp, t.PrefixLength) == Ipv4.Network(relayIp, t.PrefixLength)))
            {
                return true;
            }
        }

        return false;

        static bool TryParseIpv4(string? text, out IPAddress address) =>
            IPAddress.TryParse(text, out address!) && address.AddressFamily == AddressFamily.InterNetwork;
    }

    private string DropImages()
    {
        var dropped = media.ClearPending();
        return dropped > 0 ? $"🗑 丢掉了 {dropped} 张暂存的图片" : "没有暂存的图片";
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

        conversation.ForgetDevice(device.Id);
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
    /// 没写目标时用默认目标：<paramref name="preferBlocked"/> 为 true（<c>/yes</c>、<c>/no</c>）时先看在等确认的 agent，
    /// 只有一个就用它、有多个就让主人指定；否则用最近打过交道的 agent。
    /// </summary>
    private async Task<(Resolved? Resolved, string? Error)> ResolveOrRecentAsync(TargetRef? target, CancellationToken cancellationToken, bool preferBlocked = false)
    {
        if (target is not null)
        {
            return await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (preferBlocked)
        {
            var blocked = await StillBlockedAsync(cancellationToken).ConfigureAwait(false);
            if (blocked.Count > 1)
            {
                return (null, "有好几个 agent 在等确认，请写上目标：" + string.Join("、", blocked.Select(b => b.Address)));
            }

            if (blocked.Count == 1)
            {
                conversation.Touch(AgentRef.From(blocked[0].Device.Id, blocked[0].Device.Name, blocked[0].Agent));
                return (blocked[0], null);
            }
        }

        return conversation.Recent is { } recent
            ? await ResolveAsync(recent.Target, cancellationToken).ConfigureAwait(false)
            : (null, "还没有打过交道的 agent，请写上目标（发 /ls 查看）");
    }

    /// <summary>
    /// 发过"在等你确认"的 agent 里，现在确实还在等的（按 pane 对上，用它现在的名字）。已经不等了的（在电脑上处理掉了、
    /// 用 /keys 处理了、关掉了）顺手从记录里划掉；所在设备离线或查询失败的不算候选，但先不划掉。
    /// </summary>
    private async Task<IReadOnlyList<Resolved>> StillBlockedAsync(CancellationToken cancellationToken)
    {
        var remembered = conversation.Blocked();
        if (remembered.Count == 0)
        {
            return [];
        }

        var devices = remembered.Select(b => b.DeviceId).Distinct(StringComparer.Ordinal)
            .Select(id => store.FindDevice(id)).OfType<DeviceRecord>().ToList();
        var lists = (await QueryAgentsAsync(devices, cancellationToken).ConfigureAwait(false))
            .Where(r => r.Outcome is { Ok: true, Data: AgentListData })
            .ToDictionary(r => r.Device.Id, r => (r.Device, Agents: ((AgentListData)r.Outcome!.Data!).Agents), StringComparer.Ordinal);
        var still = new List<Resolved>();
        foreach (var entry in remembered)
        {
            if (!lists.TryGetValue(entry.DeviceId, out var list))
            {
                continue;
            }

            if (list.Agents.FirstOrDefault(a => a.PaneId == entry.PaneId) is { Status: "blocked" } agent)
            {
                still.Add(new Resolved(list.Device, agent));
            }
            else
            {
                conversation.Responded(entry.DeviceId, entry.PaneId);
            }
        }

        return still;
    }

    /// <summary>
    /// 找 agent：序号用上次 /ls 的结果；其他按 名字 / pane ID 精确匹配，找不到再按工作区名匹配（都不区分大小写）。
    /// 没指定设备时在所有在线设备里找，多处匹配时优先默认设备，仍有歧义就让用户指定。
    /// 找到的 agent 记为"最近打过交道的"。
    /// </summary>
    private async Task<(Resolved? Resolved, string? Error)> ResolveAsync(TargetRef target, CancellationToken cancellationToken)
    {
        var (resolved, error) = await FindAsync(target, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            conversation.Touch(AgentRef.From(resolved.Device.Id, resolved.Device.Name, resolved.Agent));
        }

        return (resolved, error);
    }

    private async Task<(Resolved? Resolved, string? Error)> FindAsync(TargetRef target, CancellationToken cancellationToken)
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
