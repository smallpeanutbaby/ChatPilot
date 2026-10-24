using ChatPilot.Server.State;
using ChatPilot.Shared.Text;

namespace ChatPilot.Server.Chat;

/// <summary>
/// 执行 <c>/at</c> 定时任务：每隔几秒看一次状态文件里有没有到点的任务，到点就像主人刚发了这条消息一样执行，结果作为通知推送。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>先从状态文件里删掉再执行：执行到一半服务器崩溃，重启后不会再执行一次（宁可漏，不重复派活）。</item>
/// <item>服务器停机错过的任务：晚了不到 <see cref="MissedGrace"/> 照常执行，更晚的不执行，只通知主人。</item>
/// </list>
/// </remarks>
public sealed class Scheduler(
    StateStore store,
    ChatDispatcher dispatcher,
    Notifier notifier,
    UserClock clock,
    TimeProvider time,
    ILogger<Scheduler> logger) : BackgroundService
{
    /// <summary>错过执行时间多久以内还照常执行。</summary>
    internal static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(PollInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 定时任务出问题不能拖垮服务器；写盘失败等情况下稍后再试。
                logger.LogError(ex, "执行定时任务出错，稍后重试");
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>执行所有到点的任务（按时间先后）。</summary>
    internal async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = store.Read(s => s.ScheduledJobs.Where(j => j.DueAt <= now).OrderBy(j => j.DueAt).ToList());
        foreach (var job in due)
        {
            var removed = false;
            store.Update(state => removed = state.ScheduledJobs.RemoveAll(j => j.Id == job.Id) > 0);
            if (!removed)
            {
                // 刚被 /at del 取消了。
                continue;
            }

            var summary = TextChunker.Truncate(job.Text.ReplaceLineEndings(" "), 80);
            if (now - job.DueAt > MissedGrace)
            {
                logger.LogWarning("定时任务 #{Id} 错过了执行时间，不再执行", job.Id);
                notifier.Enqueue($"⏰ 定时任务 #{job.Id} 原定 {clock.Format(job.DueAt)} 执行，那时服务器没在运行，已经错过，没有执行：\n{summary}");
                continue;
            }

            logger.LogInformation("执行定时任务 #{Id}", job.Id);
            var reply = await dispatcher.HandleScheduledAsync(job.Text, cancellationToken).ConfigureAwait(false);
            notifier.Enqueue($"⏰ 定时任务 #{job.Id} 到点了：{summary}\n\n{reply}");
        }
    }
}
