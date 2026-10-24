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
/// <param name="store">状态文件，定时任务存在这里，服务器重启后还在。</param>
/// <param name="dispatcher">执行到点任务的入口，和主人发消息走同一套逻辑。</param>
/// <param name="notifier">把执行结果或“已错过”的说明推送给主人。</param>
/// <param name="clock">按主人的时区显示原定时间。</param>
/// <param name="time">时间来源，测试里可以换成假时钟。</param>
/// <param name="logger">日志。</param>
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

    /// <summary>多久检查一次有没有到点的任务，也就是任务到点后最多晚这么久执行。</summary>
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
    /// <param name="cancellationToken">服务器停止时取消。</param>
    /// <returns>这一批到点的任务都处理完后完成的任务。</returns>
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
