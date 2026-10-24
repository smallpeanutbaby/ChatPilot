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
/// <param name="hub">在线设备，给节点发指令。</param>
/// <param name="store">状态文件：设备、默认设备、定时任务。</param>
/// <param name="pairing">生成配对码。</param>
/// <param name="identity">配对提示里要用的服务器地址和证书指纹。</param>
/// <param name="conversation">对话上下文：不写目标时用的默认 agent、在等确认的 agent。</param>
/// <param name="history">任务记录，给 <c>/history</c> 看。</param>
/// <param name="media">主人暂存的图片。</param>
/// <param name="clock">主人时区的时间。</param>
/// <param name="options">服务器配置，提供超时等设置。</param>
/// <param name="logger">日志。</param>
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

    /// <summary>让所有消息一条一条处理（见 <see cref="HandleAsync"/>）。</summary>
    private readonly SemaphoreSlim _serial = new(1, 1);
    /// <summary>保护 <c>_lastListing</c>。</summary>
    private readonly Lock _listingLock = new();
    /// <summary>上一次 <c>/ls</c> 列出的 agent，按序号排列，<c>@序号</c> 用它解析。</summary>
    private List<ListedAgent> _lastListing = [];
    /// <summary>等主人确认的 <c>/close</c>；同一时间只记一个，新的替换旧的。</summary>
    private PendingClose? _pendingClose;

    /// <summary><c>/ls</c> 列表里的一项。</summary>
    /// <param name="Device">agent 所在的设备。</param>
    /// <param name="Agent">列出时的 agent 概要。</param>
    private sealed record ListedAgent(DeviceRecord Device, AgentSummary Agent);

    /// <summary>发过确认提示、等主人确认的 <c>/close</c>。</summary>
    /// <param name="DeviceId">agent 所在设备的 ID。</param>
    /// <param name="PaneId">agent 所在的 pane。</param>
    /// <param name="Label">回复里显示的名字，例如 <c>[mac] tideq</c>。</param>
    /// <param name="Address">确认时要写的目标，例如 <c>mac/tideq</c>。</param>
    /// <param name="Expires">确认的截止时间。</param>
    private sealed record PendingClose(string DeviceId, string PaneId, string Label, string Address, DateTimeOffset Expires);

    /// <summary>
    /// 有 <c>/close</c> 在等确认时，单独发来会被当成"确认"的词（去掉首尾空白和句末标点，不区分大小写）。
    /// 这些词不交给管家：管家可能把它当成同意了它自己之前提的什么事。
    /// </summary>
    private static readonly HashSet<string> BareConfirmWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "确认", "确认关闭", "确定", "关闭", "关掉", "是", "是的", "对", "好", "好的", "可以", "嗯", "ok", "okay", "yes", "y", "confirm",
    };

    /// <summary>解析好的目标：在线设备上的一个 agent。</summary>
    /// <param name="Device">agent 所在的设备。</param>
    /// <param name="Agent">节点刚报告的 agent 概要。</param>
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

    /// <summary>等节点返回普通指令结果的时间。</summary>
    private TimeSpan CommandTimeout => TimeSpan.FromSeconds(options.Value.CommandTimeoutSeconds);

    /// <summary>
    /// 处理一条主人的消息，返回回复文字。不会抛异常（取消除外）。
    /// 所有消息严格排队、一条一条处理：先发的 <c>/use</c> 一定在后发的指令之前生效，<c>@序号</c> 用的一定是之前那次 <c>/ls</c> 的结果。
    /// </summary>
    /// <param name="text">主人发来的原文。</param>
    /// <param name="cancellationToken">取消处理；取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>要回复主人的文字；出错时是错误说明。</returns>
    public Task<string> HandleAsync(string text, CancellationToken cancellationToken) =>
        SerialAsync(() => HandleCoreAsync(text, cancellationToken), cancellationToken);

    /// <summary>
    /// 取走一个到点的定时任务，需要时执行它。从状态文件里删掉和执行在同一个排队区段里完成，和 <c>/at del</c> 互斥：
    /// 回复过"已取消"的任务不会再执行，已经执行的任务 <c>/at del</c> 也找不到了。
    /// 执行时只发给创建时定下的设备和 agent，不带主人暂存的图片，也不改变不写目标时的默认 agent。
    /// </summary>
    /// <param name="jobId">任务编号。</param>
    /// <param name="execute">删掉之后要不要执行；错过太久的任务只删不执行。</param>
    /// <param name="cancellationToken">取消排队或执行；取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>
    /// <c>Job</c> 是取走的任务，已经被 <c>/at del</c> 取消（不在状态文件里）时为 null；
    /// <c>Reply</c> 是执行结果的文字，没有执行时为 null。
    /// </returns>
    /// <exception cref="IOException">状态文件写不进去（任务还在，稍后再试）。</exception>
    internal Task<(ScheduledJob? Job, string? Reply)> TakeScheduledJobAsync(int jobId, bool execute, CancellationToken cancellationToken) =>
        SerialAsync<(ScheduledJob?, string?)>(async () =>
        {
            ScheduledJob? job = null;
            store.Update(state =>
            {
                var index = state.ScheduledJobs.FindIndex(j => j.Id == jobId);
                if (index >= 0)
                {
                    job = state.ScheduledJobs[index];
                    state.ScheduledJobs.RemoveAt(index);
                }
            });
            if (job is null || !execute)
            {
                return (job, null);
            }

            try
            {
                return (job, await RunJobAsync(job, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "执行定时任务 #{Id} 时出错", job.Id);
                return (job, "❌ 服务器内部错误，详情见服务器日志");
            }
        }, cancellationToken);

    /// <summary>主人发来一张图片：暂存起来，等下一条派活的指令一起发出去。</summary>
    /// <param name="data">图片的原始字节。</param>
    /// <param name="cancellationToken">取消排队；取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>回复主人的文字：暂存了几张、多久内有效；不是图片、太大或已满时是原因。</returns>
    public Task<string> HandleImageAsync(byte[] data, CancellationToken cancellationToken) =>
        SerialAsync(() =>
        {
            var (count, error) = media.Stage(data);
            return Task.FromResult(error is not null
                ? "❓ " + error
                : $"🖼 收到图片（共 {count} 张待发）。接下来用 @目标 内容 派活时会一起带上，{MediaStore.Lifetime.TotalMinutes:0} 分钟内有效；不要了发 /drop");
        }, cancellationToken);

    /// <summary>排队执行：等前一条消息处理完再开始。</summary>
    /// <typeparam name="T">处理结果的类型。</typeparam>
    /// <param name="work">要执行的处理。</param>
    /// <param name="cancellationToken">取消排队。</param>
    /// <returns><paramref name="work"/> 的结果。</returns>
    private async Task<T> SerialAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
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

    /// <summary>解析并执行一条消息。意外异常记日志并回复“服务器内部错误”，只有取消会抛出去。</summary>
    /// <param name="text">消息原文。</param>
    /// <param name="cancellationToken">取消处理。</param>
    /// <returns>要回复的文字。</returns>
    private async Task<string> HandleCoreAsync(string text, CancellationToken cancellationToken)
    {
        var command = ChatCommandParser.Parse(text);
        if (CloseReminder(command) is { } reminder)
        {
            return reminder;
        }

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
                ChatPrompt prompt => await PromptAsync(prompt.Target, prompt.Text, cancellationToken).ConfigureAwait(false),
                ChatReply reply => await ReplyAsync(reply.Text, cancellationToken).ConfigureAwait(false),
                ChatButlerPrompt butler => await ButlerPromptAsync(butler.Text, cancellationToken).ConfigureAwait(false),
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

    /// <summary>
    /// 有 <c>/close</c> 在等确认时，主人单独回了"确认""好的"之类的词，或者发了不写目标的 <c>/yes</c>、<c>/no</c>：
    /// 多半是在回答"要关闭吗？"。这时不交给管家、也不去猜是哪个确认框，只告诉主人确认关闭的完整写法。
    /// </summary>
    /// <param name="command">解析好的指令。</param>
    /// <returns>提醒的文字；没有在等确认的 <c>/close</c>、已经过期，或者不是这几种消息时为 null。</returns>
    private string? CloseReminder(ChatCommand command)
    {
        if (_pendingClose is not { } pending || pending.Expires <= clock.UtcNow)
        {
            return null;
        }

        var howToClose = $"确认关闭请回复：/close {pending.Address} 确认";
        return command switch
        {
            ChatButlerPrompt butler when BareConfirmWords.Contains(butler.Text.Trim().TrimEnd('。', '！', '!', '.', '~', '～')) =>
                $"⚠️ 还没有关闭 {pending.Label}。{howToClose}\n（这条消息没有交给管家；要和管家说话，请换个说法再发一次）",
            ChatRespond { Target: null } respond =>
                $"⚠️ 你刚才要关闭 {pending.Label}，这条 /{(respond.Approve ? "yes" : "no")} 没有按任何键。{howToClose}\n" +
                $"要回应确认框的话请写上目标：/{(respond.Approve ? "yes" : "no")} 设备/名字",
            _ => null,
        };
    }

    /// <summary><c>/devices</c>：在线状态、当前默认设备，在线的显示电脑名、系统和节点版本，离线的显示最后在线时间。</summary>
    /// <returns>设备列表；还没有设备时是配对提示。</returns>
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

    /// <summary><c>/use 设备</c>：切换默认设备（离线设备也可以）。</summary>
    /// <param name="name">设备名或设备 ID。</param>
    /// <returns>切换结果；设备不存在时是提示。</returns>
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

    /// <summary><c>/ls [设备]</c>：列出 agent 并编上序号，记下这次的列表供 <c>@序号</c> 使用。</summary>
    /// <param name="deviceName">只列这台设备；为 null 时列出所有设备。</param>
    /// <param name="cancellationToken">取消查询。</param>
    /// <returns>按设备分组的 agent 列表；设备不存在或还没有设备时是提示。</returns>
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
    /// <param name="devices">要查询的设备。</param>
    /// <param name="cancellationToken">取消查询。</param>
    /// <returns>每台设备一项，顺序和 <paramref name="devices"/> 相同；各设备并行查询。</returns>
    private async Task<(DeviceRecord Device, CommandOutcome? Outcome)[]> QueryAgentsAsync(IEnumerable<DeviceRecord> devices, CancellationToken cancellationToken) =>
        await Task.WhenAll(devices.Select(async d =>
            (Device: d, Outcome: hub.IsOnline(d.Id)
                ? await hub.SendAsync(d.Id, new ListAgentsCommand(), CommandTimeout, cancellationToken).ConfigureAwait(false)
                : null))).ConfigureAwait(false);

    /// <summary><c>/pending</c>：在等确认的、在干活的（多久了、排了几条）、定时任务和暂存的图片。</summary>
    /// <param name="cancellationToken">取消查询。</param>
    /// <returns>按类别分段的待办；查询失败的设备单独列出；什么都没有时说明没有待办。</returns>
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

    /// <summary><c>/read [目标] [行数]</c>：看 agent 最近的终端输出。</summary>
    /// <param name="command">解析好的指令；没写目标时用最近打过交道的 agent。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>终端输出；找不到目标或节点返回失败时是原因。</returns>
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

    /// <summary><c>/last [目标]</c>：补看 agent 最近一条回复。</summary>
    /// <param name="command">解析好的指令；没写目标时用最近打过交道的 agent。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>agent 最近的回复；找不到目标或节点返回失败时是原因。</returns>
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

    /// <summary><c>@目标 内容</c>：把任务交给指定的 agent。</summary>
    /// <param name="target">主人写的目标。</param>
    /// <param name="text">任务内容。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>发送结果；找不到目标时是原因。</returns>
    private async Task<string> PromptAsync(TargetRef target, string text, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        return resolved is null ? "❓ " + error : await SendPromptAsync(resolved, text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>/re</c>：交给最近打过交道的 agent。</summary>
    /// <param name="text">任务内容。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>发送结果；还没有打过交道的 agent 或找不到它时是原因。</returns>
    private async Task<string> ReplyAsync(string text, CancellationToken cancellationToken)
    {
        var (resolved, error) = await ResolveOrRecentAsync(null, cancellationToken).ConfigureAwait(false);
        return resolved is null ? "❓ " + error : await SendPromptAsync(resolved, text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把主人的任务（带上暂存的图片）发给解析好的 agent，见 <see cref="SendTextAsync"/>。交出去（或排上队）了就记为主人派的活；
    /// 节点说它在等确认时记下来，紧接着的 <c>/yes</c> 就是回应它。
    /// </summary>
    /// <param name="resolved">目标 agent。</param>
    /// <param name="text">任务内容。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>发送结果。</returns>
    private async Task<string> SendPromptAsync(Resolved resolved, string text, CancellationToken cancellationToken)
    {
        CommandOutcome? outcome = null;
        var reply = await SendTextAsync(
            resolved.Device, resolved.Target, text, withImages: true, resolved.Label, $"已发给 {resolved.Label}", notFound: null,
            o => outcome = o, cancellationToken).ConfigureAwait(false);
        if (outcome is { Ok: true })
        {
            conversation.Dispatched(resolved.Device.Id, resolved.Agent.PaneId, resolved.Agent.Name);
        }
        else if (outcome?.Error?.Code == ErrorCodes.AgentBlocked)
        {
            conversation.NoteBlocked(AgentRef.From(resolved.Device.Id, resolved.Device.Name, resolved.Agent));
        }

        return reply;
    }

    /// <summary>
    /// 不带 <c>/</c> 或 <c>@</c> 的文字：交给默认设备上名为 <see cref="ButlerName"/> 的管家。交出去了就记为主人派的活；
    /// 管家在等确认时把它记为最近打过交道的、在等确认的，紧接着的 <c>/yes</c> 就是回应它。
    /// </summary>
    /// <param name="text">原文；暂存的图片一起带上。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>发送结果；没有可用的默认设备或还没有管家时是提示。</returns>
    private async Task<string> ButlerPromptAsync(string text, CancellationToken cancellationToken)
    {
        var (device, error) = DefaultDevice();
        if (device is null)
        {
            return "❓ " + error + "\n（直接发文字会交给管家；要指定 agent 请用 @目标 内容）";
        }

        var label = $"[{device.Name}] 的管家";
        CommandOutcome? outcome = null;
        var reply = await SendTextAsync(
            device, ButlerName, text, withImages: true, label, $"已交给 {label}",
            notFound: $"🤖 [{device.Name}] 还没有管家。发 /butler 创建一个，或者用 @目标 内容 直接指定 agent（/ls 查看）。",
            o => outcome = o, cancellationToken).ConfigureAwait(false);
        if (outcome is { Ok: true })
        {
            conversation.Dispatched(device.Id, paneId: null, ButlerName);
        }
        else if (outcome?.Error?.Code == ErrorCodes.AgentBlocked
            && (await FindAsync(new TargetRef(device.Name, ButlerName), cancellationToken).ConfigureAwait(false)).Resolved is { } butler)
        {
            var agent = AgentRef.From(butler.Device.Id, butler.Device.Name, butler.Agent);
            conversation.Touch(agent);
            conversation.NoteBlocked(agent);
        }

        return reply;
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
    /// <param name="observe">拿到节点的结果后调用，让调用方据此更新对话上下文；为 null 时不调用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回复主人的文字：已发出、节点的说明（例如排队了），或失败原因（带图片时提醒图片还暂存着）。</returns>
    private async Task<string> SendTextAsync(
        DeviceRecord device, string target, string text, bool withImages, string label, string sentPhrase, string? notFound,
        Action<CommandOutcome>? observe, CancellationToken cancellationToken)
    {
        var attachments = withImages ? media.Issue(device.Id) : null;
        NodeCommand command = attachments is null ? new PromptCommand(target, text) : new MediaPromptCommand(target, text, attachments);
        var outcome = await hub.SendAsync(device.Id, command, CommandTimeout, cancellationToken).ConfigureAwait(false);
        observe?.Invoke(outcome);
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

    /// <summary><c>/new [设备] 目录 [类型] [名字]</c>：新开工作区并启动 agent。</summary>
    /// <param name="command">解析好的指令；没写设备时用默认设备。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>启动结果；设备不可用时是原因。</returns>
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

    /// <summary><c>/butler [设备]</c>：在节点的第一个允许目录启动名为 <see cref="ButlerName"/> 的 agent，并发给它 <see cref="ButlerInstructions"/>。</summary>
    /// <param name="deviceName">设备名；为 null 时用默认设备。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>启动结果；设备不可用时是原因。</returns>
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

    /// <summary>
    /// 让节点启动 agent（超时用 <see cref="ServerOptions.StartAgentTimeoutSeconds"/>），成功后记为最近打过交道的 agent；
    /// 卡在确认框时记为在等确认，带初始说明时（管家）记为主人派的活。
    /// </summary>
    /// <param name="device">在哪台设备上启动。</param>
    /// <param name="command">启动参数。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>启动结果和派活方式；卡在确认框时附上屏幕摘要和 <c>/yes</c>、<c>/no</c> 的写法；失败时是原因。</returns>
    private async Task<string> StartAgentAsync(DeviceRecord device, StartAgentCommand command, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.StartAgentTimeoutSeconds);
        var outcome = await hub.SendAsync(device.Id, command, timeout, cancellationToken).ConfigureAwait(false);
        if (outcome is not { Ok: true, Data: AgentStartedData started })
        {
            return ReplyFormatter.Failure(outcome);
        }

        var agent = started.Agent;
        var agentRef = AgentRef.From(device.Id, device.Name, agent);
        conversation.Touch(agentRef);
        if (!started.Ready)
        {
            conversation.NoteBlocked(agentRef);
        }

        if (command.InitialPrompt is not null)
        {
            conversation.Dispatched(device.Id, agent.PaneId, agent.Name);
        }

        var target = $"{device.Name}/{agent.Name ?? agent.PaneId}";
        var header = $"🚀 已在 [{device.Name}] 启动 {agent.DisplayName}（{agent.Kind ?? "?"}）\n目录：{agent.Cwd}";
        return started.Ready
            ? $"{header}\n\n发 @{target} 内容 给它派活"
            : $"{header}\n\n⚠️ 它在等你确认：\n\n{started.Screen}\n\n回复 /yes {target} 同意，/no {target} 拒绝";
    }

    /// <summary>
    /// <c>/yes</c>、<c>/no</c>：回应确认框，成功后不再把它记为在等确认。同意之后节点会接着跟踪到它做完，
    /// 所以记为主人派的活；节点说确认框变了、要再看一眼时仍然记为在等确认。
    /// </summary>
    /// <param name="command">解析好的指令；没写目标时的选法见 <see cref="PickBlockedAsync"/>。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>节点的说明；找不到目标或节点返回失败时是原因。</returns>
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
            if (command.Approve)
            {
                conversation.Dispatched(resolved.Device.Id, resolved.Agent.PaneId, resolved.Agent.Name);
            }

            return $"👌 {resolved.Label}：{ack.Message}";
        }

        if (outcome.Error?.Code == ErrorCodes.AgentBlocked)
        {
            conversation.NoteBlocked(AgentRef.From(resolved.Device.Id, resolved.Device.Name, resolved.Agent));
        }

        return ReplyFormatter.Failure(outcome);
    }

    /// <summary><c>/keys 目标 按键…</c>：给 agent 发送按键。</summary>
    /// <param name="command">解析好的指令（按键已通过白名单检查）。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>节点的说明；找不到目标或节点返回失败时是原因。</returns>
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

    /// <summary><c>/name 目标 新名字</c>：改名成功后用新名字记为最近打过交道的 agent。</summary>
    /// <param name="command">解析好的指令。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>节点的说明；找不到目标或节点返回失败时是原因。</returns>
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
    /// <param name="command">解析好的指令；没写目标时用最近打过交道的 agent，没写文件时看改动概要。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>改动内容；找不到目标或节点返回失败时是原因。</returns>
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
    /// <param name="command">解析好的指令；没写目标时用最近打过交道的 agent。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>节点的说明；找不到目标或节点返回失败时是原因。</returns>
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
    /// <param name="command">解析好的指令。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>
    /// 确认提示（确认过期或对不上时也重新给出提示）；关闭成功时是节点的说明，并从对话上下文和 <c>/ls</c> 列表里去掉它；
    /// 找不到目标或节点返回失败时是原因。
    /// </returns>
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

        _pendingClose = new PendingClose(resolved.Device.Id, resolved.Agent.PaneId, resolved.Label, resolved.Address, now + CloseConfirmWindow);
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
    /// <param name="cancellationToken">取消查询。</param>
    /// <returns>每台在线设备一行，附上用法说明；没有在线设备时是提示。</returns>
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

    /// <summary><c>/watch 目标</c>、<c>/unwatch 目标</c>：开始或停止盯一个 agent。</summary>
    /// <param name="command">解析好的指令。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>节点的说明；找不到目标或节点返回失败时是原因。</returns>
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
    /// <param name="command">解析好的指令。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>每台设备一行结果；指定的设备不可用或没有在线设备时是原因。</returns>
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
    /// <param name="count">显示最近几条。</param>
    /// <returns>今天（按主人的时区）做完、出错的个数和总耗时，以及最近的任务（新的在前）；没有记录时是提示。</returns>
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

    /// <summary><c>/at</c>：按执行时间列出定时任务，时间按主人的时区显示。</summary>
    /// <returns>定时任务列表；没有时是新建的用法。</returns>
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
    /// <c>/at</c>：算出执行时间（按主人的时区），现在就定下发给哪台设备的哪个 agent，存进状态文件，由 <see cref="Scheduler"/> 到点执行。
    /// <c>@目标</c> 现在就解析成设备和 agent 名字（到点时 <c>/ls</c> 序号可能变了、别的电脑上可能有同名的）；
    /// 交给管家的文字现在就定下是哪台设备的管家（到点时默认设备可能换了）。没名字的 agent 不能定时：
    /// pane 编号在 agent 退出后可能换给别的程序，名字会随 agent 退出清掉，用名字找不到也不会发错。
    /// </summary>
    /// <param name="command">解析好的指令。</param>
    /// <param name="cancellationToken">取消等待（解析目标时要问节点）。</param>
    /// <returns>
    /// 编号、执行时间、发给谁和取消方法；已经有 <see cref="MaxScheduledJobs"/> 个、目标现在找不到、agent 没有名字、
    /// 没有可用的默认设备时是原因。
    /// </returns>
    private async Task<string> ScheduleAsync(ChatAt command, CancellationToken cancellationToken)
    {
        if (store.Read(s => s.ScheduledJobs.Count) >= MaxScheduledJobs)
        {
            return $"❓ 定时任务最多 {MaxScheduledJobs} 个，先用 /at del 编号 取消一些";
        }

        DeviceRecord device;
        string target, prompt, text;
        var butler = false;
        switch (ChatCommandParser.Parse(command.Text))
        {
            case ChatPrompt agentPrompt:
            {
                var (resolved, error) = await FindAsync(agentPrompt.Target, cancellationToken).ConfigureAwait(false);
                if (resolved is null)
                {
                    return $"❓ {error}\n（定时任务在创建时就要找到目标）";
                }

                if (resolved.Agent.Name is not { } name)
                {
                    return $"❓ {resolved.Label} 没有名字，不能定时派活：它的 pane 编号以后可能换给别的程序。" +
                        $"先用 /name {resolved.Address} 新名字 给它起个名字，再用名字定时";
                }

                (device, target, prompt) = (resolved.Device, name, agentPrompt.Text);
                text = $"@{device.Name}/{name} {prompt}";
                break;
            }

            case ChatButlerPrompt butlerPrompt:
            {
                var (selected, error) = DefaultDevice();
                if (selected is null)
                {
                    return $"❓ {error}\n（定时交给管家时，现在就要定下是哪台设备的管家）";
                }

                (device, target, prompt, butler) = (selected, ButlerName, butlerPrompt.Text, true);
                text = $"[{device.Name}] 的管家：{prompt}";
                break;
            }

            default:
                return "❓ 定时任务的内容只能是 @目标 内容，或者交给管家的文字";
        }

        var due = DueTime(command.When);
        var id = 0;
        store.Update(state =>
        {
            id = state.NextJobId++;
            state.ScheduledJobs.Add(new ScheduledJob(id, due, text, clock.UtcNow, device.Id, target, prompt, butler));
        });
        return $"⏰ 好的，定时任务 #{id} 会在 {clock.Format(due)}（{clock.OffsetLabel(due)}，{ReplyFormatter.Duration(due - clock.UtcNow)}后）执行：\n{TextChunker.Truncate(text, 200)}\n\n取消用 /at del {id}";
    }

    /// <summary>
    /// 执行一个定时任务：发给创建时定下的设备和 agent，不带暂存的图片，不改变不写目标时的默认 agent，
    /// 它的结果也不会（见 <see cref="ConversationState"/>）。
    /// </summary>
    /// <param name="job">取走的任务。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>执行结果；升级前创建的任务、设备已撤销或离线、找不到 agent 时是没有执行的原因。</returns>
    private async Task<string> RunJobAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        if (job.DeviceId is null || job.Target is null || job.Prompt is null)
        {
            return "❓ 这个定时任务是升级前创建的，没有记下要发给哪台设备的哪个 agent，为了不发错地方没有执行。需要的话请重新创建";
        }

        if (store.FindDevice(job.DeviceId) is not { } device)
        {
            return "❌ 它要发去的设备已经撤销了，没有执行";
        }

        if (!hub.IsOnline(device.Id))
        {
            return $"❌ 设备 {device.Name} 离线，没有执行";
        }

        var label = job.Butler ? $"[{device.Name}] 的管家" : $"[{device.Name}] {job.Target}";
        var notFound = job.Butler
            ? $"🤖 [{device.Name}] 还没有管家，没有执行。发 /butler 创建一个"
            : $"❌ [{device.Name}] 上找不到 {job.Target}（可能已经关掉或改名了），没有执行";
        return await SendTextAsync(
            device, job.Target, job.Prompt, withImages: false, label, (job.Butler ? "已交给 " : "已发给 ") + label, notFound,
            observe: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行时间（UTC）：相对时长直接加；时刻按主人的时区算，今天已经过了（或者写了"明天"）就是明天。</summary>
    /// <param name="when">解析好的时间。</param>
    /// <returns>执行时间（UTC）；夏令时切换时不存在的时刻往后挪到存在的时刻。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="when"/> 为 null。</exception>
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

    /// <summary>
    /// <c>/at del 编号</c>：取消定时任务。和到点执行在同一个排队区段里（见 <see cref="TakeScheduledJobAsync"/>），
    /// 回复"已取消"的任务一定不会再执行。
    /// </summary>
    /// <param name="id">任务编号。</param>
    /// <returns>取消结果；没有这个编号（可能刚刚已经到点执行了）时是提示。</returns>
    private string CancelJob(int id)
    {
        var removed = false;
        store.Update(state => removed = state.ScheduledJobs.RemoveAll(j => j.Id == id) > 0);
        return removed ? $"🗑 已取消定时任务 #{id}" : $"❓ 没有定时任务 #{id}（可能已经到点执行了），发 /at 查看";
    }

    /// <summary>
    /// <c>/wake</c>：让同一局域网里的其他在线设备广播唤醒包。按上线时记下的网段挑同一网段的设备；
    /// 一台都对不上时让所有在线设备都试一次（网段判断不一定准，多发几个包没有坏处）。
    /// </summary>
    /// <param name="name">要唤醒的设备名或设备 ID。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>哪些设备发出了唤醒包，以及唤醒需要的条件；设备不存在、已经在线、不知道网卡、没有其他在线设备或都发送失败时是原因。</returns>
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
    /// <param name="relay">帮忙发唤醒包的设备的网卡；旧版节点没报告时为 null。</param>
    /// <param name="target">要唤醒的设备的网卡。</param>
    /// <returns>有一对网卡在同一网段时为 <c>true</c>；没有地址、不是 IPv4 或前缀长度为 0 的网卡不参与比较。</returns>
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

    /// <summary><c>/drop</c>：丢掉暂存着、还没发出去的图片。</summary>
    /// <returns>丢掉了几张；没有暂存的图片时是提示。</returns>
    private string DropImages()
    {
        var dropped = media.ClearPending();
        return dropped > 0 ? $"🗑 丢掉了 {dropped} 张暂存的图片" : "没有暂存的图片";
    }

    /// <summary><c>/pair [设备名]</c>：生成配对码，给出在电脑上运行的完整配对命令（开了 TLS 时带上证书指纹）。</summary>
    /// <param name="deviceName">预先指定的设备名；为 null 时由节点决定。</param>
    /// <returns>配对码、有效期和配对命令；没配置 <see cref="ServerOptions.PublicUrl"/> 时提醒把服务器地址换成实际地址。</returns>
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

    /// <summary><c>/unpair 设备</c>：删除设备、断开连接，并从 <c>/ls</c> 列表和对话上下文里去掉它的 agent。</summary>
    /// <param name="name">设备名或设备 ID。</param>
    /// <returns>撤销结果；写盘失败时说明重启后会恢复；设备不存在时是提示。</returns>
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
    /// <returns>可用的默认设备；已选的设备离线、没有在线设备、没选且有多台在线时设备为 null，并给出原因。</returns>
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

    /// <summary>按名字找一台在线的设备。</summary>
    /// <param name="name">设备名或设备 ID。</param>
    /// <returns>找到并且在线时返回设备；设备不存在或离线时设备为 null，并给出原因。</returns>
    private (DeviceRecord? Device, string? Error) OnlineDevice(string name)
    {
        if (store.FindDevice(name) is not { } device)
        {
            return (null, UnknownDevice(name));
        }

        return hub.IsOnline(device.Id) ? (device, null) : (null, $"设备 {device.Name} 离线");
    }

    /// <summary>
    /// 没写目标时用默认目标：<paramref name="preferBlocked"/> 为 true（<c>/yes</c>、<c>/no</c>）时先按 <see cref="PickBlockedAsync"/>
    /// 找在等确认的 agent；否则（或者没有候选时）用最近打过交道的 agent。
    /// </summary>
    /// <param name="target">主人写的目标；为 null 时用默认目标。</param>
    /// <param name="cancellationToken">取消查询。</param>
    /// <param name="preferBlocked">没写目标时是否先找在等确认的 agent。</param>
    /// <returns>找到的 agent；找不到、或者分不清是哪个在等确认时为 null，并给出原因。</returns>
    private async Task<(Resolved? Resolved, string? Error)> ResolveOrRecentAsync(TargetRef? target, CancellationToken cancellationToken, bool preferBlocked = false)
    {
        if (target is not null)
        {
            return await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (preferBlocked)
        {
            var (blocked, error) = await PickBlockedAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return (null, error);
            }

            if (blocked is not null)
            {
                conversation.Touch(AgentRef.From(blocked.Device.Id, blocked.Device.Name, blocked.Agent));
                return (blocked, null);
            }
        }

        return conversation.Recent is { } recent
            ? await ResolveAsync(recent.Target, cancellationToken).ConfigureAwait(false)
            : (null, "还没有打过交道的 agent，请写上目标（发 /ls 查看）");
    }

    /// <summary>
    /// 不写目标的 <c>/yes</c>、<c>/no</c> 回应哪个确认框。向记着的候选（最近打过交道的 agent、记为在等确认的 agent）
    /// 所在的设备要一次列表，按 pane 对上现在的状态：
    /// <list type="number">
    /// <item>最近打过交道的 agent 自己在等确认（名字也没变）：就是它，哪怕别的 agent 也在等。</item>
    /// <item>有候选所在的设备离线或没有回应、又可能挑错时：不猜，让主人写上目标。</item>
    /// <item>其余在等确认的只有一个就是它；有好几个让主人写上目标。</item>
    /// </list>
    /// 已经不等了的（在电脑上处理掉了、用 /keys 处理了、关掉了）顺手从记录里划掉；查不到的先不划掉。
    /// </summary>
    /// <param name="cancellationToken">取消查询。</param>
    /// <returns>
    /// 挑中的 agent（用它现在的名字）；没有在等确认的候选时两项都为 null（由调用方退回最近打过交道的 agent）；
    /// 分不清时 agent 为 null，并给出让主人写目标的说明。
    /// </returns>
    private async Task<(Resolved? Blocked, string? Error)> PickBlockedAsync(CancellationToken cancellationToken)
    {
        var recent = conversation.Recent;
        var remembered = conversation.Blocked();
        if (recent is null && remembered.Count == 0)
        {
            return (null, null);
        }

        var deviceIds = remembered.Select(b => b.DeviceId).Append(recent?.DeviceId).OfType<string>().Distinct(StringComparer.Ordinal);
        var results = await QueryAgentsAsync(deviceIds.Select(id => store.FindDevice(id)).OfType<DeviceRecord>(), cancellationToken).ConfigureAwait(false);
        var lists = results
            .Where(r => r.Outcome is { Ok: true, Data: AgentListData })
            .ToDictionary(r => r.Device.Id, r => (r.Device, Agents: ((AgentListData)r.Outcome!.Data!).Agents), StringComparer.Ordinal);

        if (recent is not null
            && lists.TryGetValue(recent.DeviceId, out var recentList)
            && recentList.Agents.FirstOrDefault(a => a.PaneId == recent.PaneId) is { Status: "blocked" } recentAgent
            && (recent.Name is null || string.Equals(recent.Name, recentAgent.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return (new Resolved(recentList.Device, recentAgent), null);
        }

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

        // 查不到的设备上可能有更该回应的确认框（包括最近打过交道的那个），这时挑哪个都可能挑错。
        var unknown = results.Where(r => !lists.ContainsKey(r.Device.Id)).ToList();
        if (unknown.Count > 0 && (still.Count > 0 || remembered.Any(b => unknown.Any(u => u.Device.Id == b.DeviceId))))
        {
            var reasons = string.Join("、", unknown.Select(u => $"{u.Device.Name}（{u.Outcome?.Error?.Message ?? "离线"}）"));
            var known = still.Count > 0 ? "。能查到在等确认的：" + string.Join("、", still.Select(b => b.Address)) : "";
            return (null, $"{reasons} 查不到 agent 的状态，分不清你要回应哪个确认框，请写上目标，例如 /yes 设备/名字{known}");
        }

        return still.Count switch
        {
            0 => (null, null),
            1 => (still[0], null),
            _ => (null, "有好几个 agent 在等确认，请写上目标：" + string.Join("、", still.Select(b => b.Address))),
        };
    }

    /// <summary>
    /// 找 agent：序号用上次 /ls 的结果；其他按 名字 / pane ID 精确匹配，找不到再按工作区名匹配（都不区分大小写）。
    /// 没指定设备时在所有在线设备里找，多处匹配时优先默认设备，仍有歧义就让用户指定。
    /// 找到的 agent 记为"最近打过交道的"。
    /// </summary>
    /// <param name="target">主人写的目标。</param>
    /// <param name="cancellationToken">取消查询。</param>
    /// <returns>找到的 agent；找不到、设备离线或有歧义时为 null，并给出原因。</returns>
    private async Task<(Resolved? Resolved, string? Error)> ResolveAsync(TargetRef target, CancellationToken cancellationToken)
    {
        var (resolved, error) = await FindAsync(target, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            conversation.Touch(AgentRef.From(resolved.Device.Id, resolved.Device.Name, resolved.Agent));
        }

        return (resolved, error);
    }

    /// <summary>按 <see cref="ResolveAsync"/> 的规则找 agent，但不更新对话上下文。</summary>
    /// <param name="target">主人写的目标。</param>
    /// <param name="cancellationToken">取消查询。</param>
    /// <returns>
    /// 找到的 agent；序号不存在或不在指定的设备上、设备离线、找不到（顺带列出查询失败的设备）、
    /// 多处匹配且默认设备上不是正好一个时为 null，并给出原因。
    /// </returns>
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

    /// <summary>设备不存在时的说明，顺便列出现有的设备。</summary>
    /// <param name="name">主人写的设备名。</param>
    /// <returns>说明文字（不带前面的图标）。</returns>
    private string UnknownDevice(string name)
    {
        var names = store.Devices().Select(d => d.Name).ToList();
        return names.Count == 0
            ? $"没有叫 {name} 的设备，目前还没有配对任何设备"
            : $"没有叫 {name} 的设备。现有设备：{string.Join("、", names)}";
    }
}
