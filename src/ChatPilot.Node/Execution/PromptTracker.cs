using System.Collections.Concurrent;
using ChatPilot.Node.Config;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>接收节点上报的 agent 事件（由连接层负责送到服务器）。</summary>
public interface IAgentEventSink
{
    /// <summary>上报一个事件；不能阻塞，也不能抛异常。</summary>
    void Publish(AgentEvent agentEvent);
}

/// <summary>
/// 跟踪交给 agent 的任务：在后台等它停下，然后把回复（或确认框、错误）作为事件上报。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>每个 agent 同时最多一个后台跟踪任务（按 pane ID 去重），总数不超过 <see cref="MaxWatchers"/>，防止后台 herdr 进程无限堆积。</item>
/// <item>任务一旦停下就一定会上报一个事件：整理回复失败时也会上报，内容改为提示用 /read 查看。</item>
/// </list>
/// </remarks>
public sealed class PromptTracker : IAsyncDisposable
{
    /// <summary>同时跟踪的 agent 数上限。</summary>
    internal const int MaxWatchers = 32;


    /// <summary>初始说明最多发送几次。</summary>
    internal const int InitialPromptAttempts = 3;

    /// <summary>判断指令有没有被吞时读多少行终端输出。</summary>
    private const int DropCheckLines = 200;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly string[] SettledStates = [AgentStatus.Idle, AgentStatus.Done, AgentStatus.Blocked];
    private static readonly string[] ReadyStates = [AgentStatus.Idle, AgentStatus.Done];

    private readonly HerdrClient _herdr;
    private readonly ReplyService _replies;
    private readonly IAgentEventSink _sink;
    private readonly NodeConfig _config;
    private readonly ILogger<PromptTracker> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<string, byte> _watched = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaxWatchers, MaxWatchers);
    private readonly ConcurrentDictionary<string, string> _pendingInitialPrompts = new(StringComparer.Ordinal);
    private readonly TimeSpan _initialPromptGrace;
    private readonly TimeSpan _activityTimeout;

    /// <param name="herdr">herdr 客户端。</param>
    /// <param name="replies">取回复和屏幕摘要。</param>
    /// <param name="sink">事件上报给谁（连接层）。</param>
    /// <param name="config">节点配置（等待时长、回复长度上限）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="initialPromptGrace">
    /// agent 刚就绪后等多久再发初始说明（默认 3 秒），被吞的指令重发前也等这么久。刚确认完"信任此目录"时 herdr 已经报告空闲，
    /// 但 Claude 的界面还在初始化，这时输入的文字会被吞掉。
    /// </param>
    /// <param name="activityTimeout">提交后最多等多久看 agent 有没有开始干活（默认 10 秒）。</param>
    public PromptTracker(
        HerdrClient herdr,
        ReplyService replies,
        IAgentEventSink sink,
        NodeConfig config,
        ILogger<PromptTracker> logger,
        TimeSpan? initialPromptGrace = null,
        TimeSpan? activityTimeout = null)
    {
        _herdr = herdr;
        _replies = replies;
        _sink = sink;
        _config = config;
        _logger = logger;
        _initialPromptGrace = initialPromptGrace ?? TimeSpan.FromSeconds(3);
        _activityTimeout = activityTimeout ?? TimeSpan.FromSeconds(10);
    }

    private TimeSpan WaitLimit => TimeSpan.FromHours(_config.PromptWaitHours);

    /// <summary>
    /// 提交文字，然后在后台等结果。提交失败时直接返回失败结果；成功时返回"已发送"。
    /// </summary>
    /// <param name="agent">提交前的 agent 状态，用它的状态序号判断之后有没有开始干活。</param>
    /// <param name="text">要交给 agent 的文字。</param>
    /// <param name="cancellationToken">只用于提交这一步；后台跟踪不受它影响。</param>
    public async Task<CommandOutcome> SubmitAsync(HerdrAgent agent, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        try
        {
            await _herdr.PromptAsync(agent.PaneId, text, cancellationToken).ConfigureAwait(false);
        }
        catch (HerdrException ex)
        {
            return CommandExecutor.MapHerdrError(ex);
        }

        var watching = TryWatch(agent, token => FollowPromptAsync(agent, text, token));
        return CommandOutcome.Success(new AckData(watching switch
        {
            WatchResult.Started or WatchResult.AlreadyWatching => "已发送",
            _ => $"已发送（同时跟踪的任务已满 {MaxWatchers} 个，这次做完不会通知，可以用 /read 查看）",
        }));
    }

    /// <summary>
    /// 回应确认框之后继续跟踪：确认框消失后如果 agent 开始干活，就等它做完再上报；
    /// 如果直接回到空闲（比如只是信任了目录），就不上报，但会补发挂起的初始说明。
    /// </summary>
    public void WatchAfterResponse(HerdrAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        TryWatch(agent, async token =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);

            // 按键之后 herdr 的状态要过一会儿才更新，最多等 5 秒确认框消失。
            var current = await _herdr.TryGetAgentAsync(agent.PaneId, token).ConfigureAwait(false);
            for (var attempt = 0; attempt < 10 && current is { Status: AgentStatus.Blocked }; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                current = await _herdr.TryGetAgentAsync(agent.PaneId, token).ConfigureAwait(false);
            }

            if (current is null)
            {
                return;
            }

            if (current.IsReady)
            {
                await SendPendingInitialPromptAsync(current, token).ConfigureAwait(false);
                return;
            }

            if (current.Status == AgentStatus.Blocked)
            {
                // 又弹出了新的确认框。
                await ReportSettledAsync(current, prompt: null, token).ConfigureAwait(false);
                return;
            }

            var settled = await _herdr.WaitAsync(agent.PaneId, SettledStates, WaitLimit, token).ConfigureAwait(false);
            await ReportSettledAsync(settled ?? current, prompt: null, token).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// agent 就绪后发送初始指令（用于创建管家）。指令先挂起，两条路谁先到谁发：
    /// 后台等待 agent 变为空闲，或者用户回应确认框后 <see cref="WatchAfterResponse"/> 发现它已就绪。
    /// 用户可能隔很久才在手机上点同意，所以等待时间和普通任务一样长；没发出去时一定会通知用户。
    /// </summary>
    public void QueueInitialPrompt(HerdrAgent agent, string text)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _pendingInitialPrompts[agent.PaneId] = text;
        var result = TryWatch(agent, async token =>
        {
            try
            {
                var ready = await _herdr.WaitAsync(agent.PaneId, ReadyStates, WaitLimit, token).ConfigureAwait(false);
                if (ready is { IsReady: true })
                {
                    await SendPendingInitialPromptAsync(ready, token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!token.IsCancellationRequested && _pendingInitialPrompts.TryRemove(agent.PaneId, out _))
                {
                    Publish(AgentEventKind.Failed, agent, "它一直没有就绪，初始说明没有发出。可以直接用 @ 把说明发给它，或者重新创建");
                }
            }
        });

        if (result == WatchResult.Busy && _pendingInitialPrompts.TryRemove(agent.PaneId, out _))
        {
            Publish(AgentEventKind.Failed, agent, "节点同时跟踪的任务已满，初始说明没有发出。可以直接用 @ 把说明发给它");
        }
    }

    /// <summary>停止所有后台跟踪（最多等 5 秒）。</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_running.Values).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // 退出时不再等后台任务。
        }

        _stopping.Dispose();
        _slots.Dispose();
    }

    private enum WatchResult
    {
        Started,
        AlreadyWatching,
        Busy,
    }

    /// <summary>
    /// 为一个 agent 启动后台跟踪。已经在跟踪时不重复启动（现有的跟踪会负责上报）；
    /// herdr 出错时上报失败事件。
    /// </summary>
    private WatchResult TryWatch(HerdrAgent agent, Func<CancellationToken, Task> work)
    {
        if (!_watched.TryAdd(agent.PaneId, 0))
        {
            return WatchResult.AlreadyWatching;
        }

        if (!_slots.Wait(0))
        {
            _watched.TryRemove(agent.PaneId, out _);
            return WatchResult.Busy;
        }

        Track(async () =>
        {
            try
            {
                await work(_stopping.Token).ConfigureAwait(false);
            }
            catch (HerdrException ex) when (!_stopping.IsCancellationRequested)
            {
                Publish(AgentEventKind.Failed, agent, ex.Code == "timeout"
                    ? $"等了 {_config.PromptWaitHours} 小时还没做完，已停止等待"
                    : ex.Message);
            }
            finally
            {
                _watched.TryRemove(agent.PaneId, out _);
                _slots.Release();
            }
        });
        return WatchResult.Started;
    }

    /// <summary>
    /// 提交后：先确认 agent 开始干活，再等它停下并上报。
    /// 迟迟没有动静、并且能确定文字被吞了时（agent 刚启动、界面还没准备好会丢输入），自动重发一次。
    /// </summary>
    private async Task FollowPromptAsync(HerdrAgent before, string prompt, CancellationToken cancellationToken)
    {
        var started = await WaitForActivityAsync(before, cancellationToken).ConfigureAwait(false);
        var resent = false;
        if (started is null)
        {
            (started, resent) = await ResendIfDroppedAsync(before, prompt, cancellationToken).ConfigureAwait(false);
        }

        if (started is null)
        {
            Publish(AgentEventKind.Failed, before, resent
                ? "提交后没看到它开始工作，屏幕上也找不到这条指令，已自动重发一次，还是没有反应，可以用 /read 看看"
                : $"提交后 {_activityTimeout.TotalSeconds:0} 秒内没看到它开始工作，可以用 /read 看看");
            return;
        }

        await FollowStartedAsync(started, prompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 判断提交的文字是不是被吞了，是的话重发一次并再等它开始工作。
    /// 用户的指令不能被执行两次，所以只有这几条都满足才重发：不是斜杠命令（命令执行完不一定有状态变化）、
    /// agent 仍然空闲且状态从没变过、屏幕上（对话里和输入框里）都找不到这段文字。拿不准一律不重发。
    /// </summary>
    /// <returns>开始工作后的 agent（没开始为 null），以及有没有重发。</returns>
    private async Task<(HerdrAgent? Started, bool Resent)> ResendIfDroppedAsync(HerdrAgent before, string prompt, CancellationToken cancellationToken)
    {
        string screen;
        try
        {
            var current = await _herdr.TryGetAgentAsync(before.PaneId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return (null, false);
            }

            if (HasStarted(before, current))
            {
                // 超时之后刚好开始了。
                return (current, false);
            }

            if (!current.IsReady || prompt.TrimStart().StartsWith('/'))
            {
                return (null, false);
            }

            screen = await _herdr.ReadAsync(before.PaneId, "recent-unwrapped", DropCheckLines, cancellationToken).ConfigureAwait(false);
        }
        catch (HerdrException ex)
        {
            _logger.LogDebug("检查 {Agent} 有没有收到指令时出错：{Message}", before.Name ?? before.PaneId, ex.Message);
            return (null, false);
        }

        if (ReplyExtractor.MayShowPrompt(screen, prompt))
        {
            return (null, false);
        }

        // 等界面准备好再重发。等待期间 agent 可能已经把排队的输入吃进去开始干活了，所以重发前必须再确认一次，
        // 把"判断被吞"到"重发"之间的空窗缩到最小。
        await Task.Delay(_initialPromptGrace, cancellationToken).ConfigureAwait(false);
        try
        {
            var latest = await _herdr.TryGetAgentAsync(before.PaneId, cancellationToken).ConfigureAwait(false);
            if (latest is null || HasStarted(before, latest))
            {
                return (latest, false);
            }

            if (!latest.IsReady)
            {
                return (null, false);
            }

            screen = await _herdr.ReadAsync(before.PaneId, "recent-unwrapped", DropCheckLines, cancellationToken).ConfigureAwait(false);
        }
        catch (HerdrException ex)
        {
            _logger.LogDebug("重发前检查 {Agent} 时出错：{Message}", before.Name ?? before.PaneId, ex.Message);
            return (null, false);
        }

        if (ReplyExtractor.MayShowPrompt(screen, prompt))
        {
            return (null, false);
        }

        _logger.LogWarning("提交给 {Agent} 的指令没有被接收（屏幕上找不到），重发一次", before.Name ?? before.PaneId);
        await _herdr.PromptAsync(before.PaneId, prompt, cancellationToken).ConfigureAwait(false);
        return (await WaitForActivityAsync(before, cancellationToken).ConfigureAwait(false), true);
    }

    /// <summary>agent 已经开始干活（或已经做完）：等它停下并上报。</summary>
    private async Task FollowStartedAsync(HerdrAgent started, string prompt, CancellationToken cancellationToken)
    {
        var settled = started.Status is AgentStatus.Idle or AgentStatus.Done or AgentStatus.Blocked
            ? started
            : await _herdr.WaitAsync(started.PaneId, SettledStates, WaitLimit, cancellationToken).ConfigureAwait(false);
        await ReportSettledAsync(settled ?? started, prompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>和提交前相比有没有动静：状态序号变了（哪怕已经做完），或者状态是工作中 / 等确认。</summary>
    private static bool HasStarted(HerdrAgent before, HerdrAgent current) =>
        current.StateChangeSeq > before.StateChangeSeq || current.Status is AgentStatus.Working or AgentStatus.Blocked;

    /// <summary>轮询直到看到 agent 有动静（见 <see cref="HasStarted"/>）。超时或 agent 不见了返回 null。</summary>
    private async Task<HerdrAgent?> WaitForActivityAsync(HerdrAgent before, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _activityTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await _herdr.TryGetAgentAsync(before.PaneId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return null;
            }

            if (HasStarted(before, current))
            {
                return current;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// 如果这个 agent 有挂起的初始指令，发送它并跟踪到做完（只会取出一次）。
    /// 初始说明是程序生成的、重发无害，所以没看到 agent 开始工作时会重发，最多 <see cref="InitialPromptAttempts"/> 次；
    /// 用户自己发的指令只在确定被吞了时重发一次（见 <see cref="ResendIfDroppedAsync"/>），避免同一个任务被执行两次。
    /// </summary>
    private async Task SendPendingInitialPromptAsync(HerdrAgent agent, CancellationToken cancellationToken)
    {
        if (!_pendingInitialPrompts.TryRemove(agent.PaneId, out var text))
        {
            return;
        }

        await Task.Delay(_initialPromptGrace, cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; attempt <= InitialPromptAttempts; attempt++)
        {
            var before = await _herdr.TryGetAgentAsync(agent.PaneId, cancellationToken).ConfigureAwait(false);
            if (before is null)
            {
                return;
            }

            await _herdr.PromptAsync(agent.PaneId, text, cancellationToken).ConfigureAwait(false);
            if (await WaitForActivityAsync(before, cancellationToken).ConfigureAwait(false) is { } started)
            {
                await FollowStartedAsync(started, text, cancellationToken).ConfigureAwait(false);
                return;
            }

            _logger.LogWarning("第 {Attempt} 次发送初始说明后没看到 {Agent} 开始工作", attempt, agent.Name ?? agent.PaneId);
            await Task.Delay(_initialPromptGrace, cancellationToken).ConfigureAwait(false);
        }

        Publish(AgentEventKind.Failed, agent, $"初始说明发了 {InitialPromptAttempts} 次都没看到它开始工作，可以用 @ 把说明手动发给它");
    }

    /// <summary>上报 agent 停下的结果。读屏幕或提取回复失败时用提示文字代替，保证事件一定发出。</summary>
    /// <param name="agent">停下来的 agent。</param>
    /// <param name="prompt">这一轮交给它的文字；回应确认框后继续跟踪时不知道，为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ReportSettledAsync(HerdrAgent agent, string? prompt, CancellationToken cancellationToken)
    {
        var blocked = agent.Status == AgentStatus.Blocked;
        string text;
        try
        {
            text = blocked
                ? await _replies.ShowDialogAsync(agent, cancellationToken).ConfigureAwait(false)
                : await _replies.GetLastReplyAsync(agent, prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HerdrException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("整理 {Agent} 的结果失败：{Message}", agent.Name ?? agent.PaneId, ex.Message);
            text = blocked ? "（读取屏幕失败，请用 /read 查看）" : "（提取回复失败，请用 /read 查看）";
        }

        Publish(blocked ? AgentEventKind.Blocked : AgentEventKind.Completed, agent, text);
    }

    private void Publish(AgentEventKind kind, HerdrAgent agent, string? text) =>
        _sink.Publish(new AgentEvent(kind, agent.ToSummary(workspaceLabel: null), text));

    /// <summary>登记并运行后台任务；退出时等它们结束（最多 5 秒）。</summary>
    private void Track(Func<Task> work)
    {
        var id = Guid.NewGuid();
        var task = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // 节点正在退出。
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台跟踪任务异常");
            }
        });

        // 先登记再挂移除回调：任务即使已经完成，回调也会在登记之后执行，不会留下残留项。
        _running[id] = task;
        _ = task.ContinueWith(completed => _running.TryRemove(id, out var _), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
