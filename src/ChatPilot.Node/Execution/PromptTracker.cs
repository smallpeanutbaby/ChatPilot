using System.Collections.Concurrent;
using ChatPilot.Node.Config;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>接收节点上报的 agent 事件（由连接层负责送到服务器）。</summary>
public interface IAgentEventSink
{
    void Publish(AgentEvent agentEvent);
}

/// <summary>
/// 跟踪交给 agent 的任务：在后台等它停下，然后把回复（或确认框、错误）作为事件上报。
/// </summary>
public sealed class PromptTracker : IAsyncDisposable
{
    /// <summary>提交后先等这么久：这段时间内就失败的（例如 agent 不存在），直接作为指令结果返回。</summary>
    internal static readonly TimeSpan QuickFailWindow = TimeSpan.FromSeconds(3);

    private const int MaxTracked = 32;
    private static readonly string[] SettledStates = [AgentStatus.Idle, AgentStatus.Done, AgentStatus.Blocked];
    private static readonly string[] ReadyStates = [AgentStatus.Idle, AgentStatus.Done];

    private readonly HerdrClient _herdr;
    private readonly ReplyService _replies;
    private readonly IAgentEventSink _sink;
    private readonly NodeConfig _config;
    private readonly ILogger<PromptTracker> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly SemaphoreSlim _slots = new(MaxTracked, MaxTracked);
    private readonly ConcurrentDictionary<string, string> _pendingInitialPrompts = new(StringComparer.Ordinal);

    public PromptTracker(HerdrClient herdr, ReplyService replies, IAgentEventSink sink, NodeConfig config, ILogger<PromptTracker> logger)
    {
        _herdr = herdr;
        _replies = replies;
        _sink = sink;
        _config = config;
        _logger = logger;
    }

    private TimeSpan WaitLimit => TimeSpan.FromHours(_config.PromptWaitHours);

    /// <summary>提交文字并在后台等结果。立即失败时返回失败结果，否则返回"已发送"。</summary>
    public async Task<CommandOutcome> SubmitAsync(HerdrAgent agent, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!_slots.Wait(0))
        {
            return CommandOutcome.Failure(ErrorCodes.Busy, $"同时在等的任务已经有 {MaxTracked} 个了，稍后再试");
        }

        var prompt = _herdr.PromptAsync(agent.PaneId, text, WaitLimit, _stopping.Token);
        var first = await Task.WhenAny(prompt, Task.Delay(QuickFailWindow, cancellationToken)).ConfigureAwait(false);
        if (first == prompt && prompt.IsFaulted)
        {
            _slots.Release();
            var error = prompt.Exception?.InnerException as HerdrException
                ?? new HerdrException("herdr_error", prompt.Exception?.InnerException?.Message ?? "提交失败");
            return CommandExecutor.MapHerdrError(error);
        }

        Track(async () =>
        {
            try
            {
                HerdrAgent? settled;
                try
                {
                    settled = await prompt.ConfigureAwait(false);
                }
                catch (HerdrException ex)
                {
                    Publish(AgentEventKind.Failed, agent, ex.Code == "timeout" ? $"等了 {_config.PromptWaitHours} 小时还没做完，已停止等待" : ex.Message);
                    return;
                }

                await ReportSettledAsync(settled ?? agent, _stopping.Token).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        });

        return CommandOutcome.Success(new AckData("已发送"));
    }

    /// <summary>
    /// 回应确认框之后继续跟踪：确认框消失后如果 agent 开始干活，就等它做完再上报；
    /// 如果直接回到空闲（比如只是信任了目录），就不上报。
    /// </summary>
    public void WatchAfterResponse(string target)
    {
        Track(async () =>
        {
            var token = _stopping.Token;
            await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);

            HerdrAgent? agent = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                agent = await _herdr.TryGetAgentAsync(target, token).ConfigureAwait(false);
                if (agent is null || agent.Status != AgentStatus.Blocked)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            }

            if (agent is null)
            {
                return;
            }

            if (agent.IsReady)
            {
                // 确认框关掉后直接回到空闲：如果有挂起的初始说明（例如管家），现在发送；否则不需要通知。
                await SendPendingInitialPromptAsync(agent, token).ConfigureAwait(false);
                return;
            }

            if (agent.Status == AgentStatus.Blocked)
            {
                // 又弹出了新的确认框。
                await ReportSettledAsync(agent, token).ConfigureAwait(false);
                return;
            }

            var settled = await _herdr.WaitAsync(target, SettledStates, WaitLimit, token).ConfigureAwait(false);
            await ReportSettledAsync(settled ?? agent, token).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// agent 就绪后发送初始指令（用于创建管家）。指令先挂起，两条路谁先到谁发：
    /// 后台等待 agent 变为空闲，或者用户回应确认框后 <see cref="WatchAfterResponse"/> 发现它已就绪。
    /// 用户可能隔很久才在手机上点同意，所以等待时间和普通任务一样长；真的超时会通知用户。
    /// </summary>
    public void QueueInitialPrompt(HerdrAgent agent, string text)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _pendingInitialPrompts[agent.PaneId] = text;
        Track(async () =>
        {
            var token = _stopping.Token;
            HerdrAgent? ready;
            try
            {
                ready = await _herdr.WaitAsync(agent.PaneId, ReadyStates, WaitLimit, token).ConfigureAwait(false);
            }
            catch (HerdrException ex) when (ex.Code == "timeout")
            {
                if (_pendingInitialPrompts.TryRemove(agent.PaneId, out _))
                {
                    Publish(AgentEventKind.Failed, agent, $"等了 {_config.PromptWaitHours} 小时它都没有就绪，初始说明没有发出。可以直接用 @ 把说明发给它，或者重新创建");
                }

                return;
            }

            if (ready is { IsReady: true })
            {
                await SendPendingInitialPromptAsync(ready, token).ConfigureAwait(false);
            }
        });
    }

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

    /// <summary>如果这个 agent 有挂起的初始指令，发送它并跟踪结果。只会发送一次。</summary>
    private async Task SendPendingInitialPromptAsync(HerdrAgent agent, CancellationToken cancellationToken)
    {
        if (!_pendingInitialPrompts.TryRemove(agent.PaneId, out var text))
        {
            return;
        }

        var settled = await _herdr.PromptAsync(agent.PaneId, text, WaitLimit, cancellationToken).ConfigureAwait(false);
        await ReportSettledAsync(settled ?? agent, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReportSettledAsync(HerdrAgent agent, CancellationToken cancellationToken)
    {
        if (agent.Status == AgentStatus.Blocked)
        {
            var screen = await _replies.GetScreenExcerptAsync(agent.PaneId, cancellationToken).ConfigureAwait(false);
            Publish(AgentEventKind.Blocked, agent, screen);
            return;
        }

        var reply = await _replies.GetLastReplyAsync(agent, cancellationToken).ConfigureAwait(false);
        Publish(AgentEventKind.Completed, agent, reply);
    }

    private void Publish(AgentEventKind kind, HerdrAgent agent, string? text) =>
        _sink.Publish(new AgentEvent(kind, agent.ToSummary(workspaceLabel: null), text));

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
            catch (HerdrException ex)
            {
                _logger.LogWarning("后台跟踪任务失败：{Code} {Message}", ex.Code, ex.Message);
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
