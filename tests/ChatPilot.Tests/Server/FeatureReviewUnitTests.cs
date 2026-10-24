using System.Collections.Concurrent;
using ChatPilot.Server.Chat;
using ChatPilot.Server.Telegram;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChatPilot.Tests.Server;

/// <summary>新功能审查发现的问题的单元测试。</summary>
public class FeatureReviewUnitTests
{
    private static readonly AgentSummary Api = new("w1:p1", "api", "claude", "idle", null, null, null);
    private static readonly AgentSummary Web = new("w2:p1", "web", "claude", "idle", null, null, null);
    private static readonly AgentSummary Butler = new("w3:p1", "butler", "claude", "idle", null, null, null);

    [Fact]
    public void Only_events_of_work_the_owner_dispatched_change_the_recent_agent()
    {
        // 审查 #2：盯着的 agent、定时任务的事件不换默认目标；主人派的活有了结果才换。
        var state = new ConversationState(new FakeTimeProvider());
        state.Touch(AgentRef.From("d1", "mac", Api));

        Assert.False(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Web, "做完了")));
        Assert.Equal("api", state.Recent?.Name);

        // 卡在确认框的照样记为在等确认（/yes 的候选），只是不换默认目标。
        Assert.False(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Blocked, Web, "确认？")));
        Assert.Equal("api", state.Recent?.Name);
        Assert.Equal("web", Assert.Single(state.Blocked()).Name);

        // 派过活：等确认、做完都换过去；做完（没有排队的）之后这次派活就结束了。
        state.Dispatched("d1", Web.PaneId, Web.Name);
        Assert.True(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Blocked, Web, "确认？")));
        Assert.Equal("web", state.Recent?.Name);
        state.Touch(AgentRef.From("d1", "mac", Api));
        Assert.True(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Web, "一", Queued: 1)));
        state.Touch(AgentRef.From("d1", "mac", Api));
        Assert.True(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Web, "二")));
        state.Touch(AgentRef.From("d1", "mac", Api));
        Assert.False(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Web, "电脑上派的")));
        Assert.Equal("api", state.Recent?.Name);

        // 交给管家的文字只知道名字；别的设备上同名的 agent 不算。
        state.Dispatched("d1", paneId: null, "butler");
        Assert.False(state.OnEvent("d2", "win", new AgentEvent(AgentEventKind.Completed, Butler, "好")));
        Assert.True(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Butler, "好")));
        Assert.Equal("butler", state.Recent?.Name);
    }

    [Fact]
    public void Late_messages_use_the_same_ten_minutes_as_missed_jobs_and_the_notice_lists_them()
    {
        // 审查 #3。
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(Scheduler.MissedGrace, StaleCommands.MaxDelay);
        Assert.False(StaleCommands.IsStale(now.AddMinutes(-10), now));
        Assert.True(StaleCommands.IsStale(now.AddMinutes(-11), now));
        Assert.False(StaleCommands.IsStale(now.AddMinutes(5), now));
        Assert.Equal(now, StaleCommands.FromUnixMilliseconds(now.ToUnixTimeMilliseconds()));
        Assert.Null(StaleCommands.FromUnixMilliseconds(null));
        Assert.Null(StaleCommands.FromUnixMilliseconds(0));
        Assert.Null(StaleCommands.FromUnixMilliseconds(long.MaxValue));

        Assert.Equal("[图片]", StaleCommands.Summarize(null, attachment: true));
        Assert.Equal("[图片] @a 看看", StaleCommands.Summarize("@a\n看看", attachment: true));
        Assert.Equal("[消息]", StaleCommands.Summarize(" ", attachment: false));

        var dropped = Enumerable.Range(1, 7).Select(i => (now.AddHours(-i), $"/m{i}")).ToList();
        var notice = StaleCommands.Notice(dropped, now);
        Assert.StartsWith("⏰ 有 7 条消息晚到了（最早的是 7 小时前发的", notice, StringComparison.Ordinal);
        Assert.Contains("· /m5", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("· /m6", notice, StringComparison.Ordinal);
        Assert.Contains("· ……还有 2 条", notice, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => StaleCommands.Notice([], now));
    }

    [Fact]
    public async Task A_hanging_channel_neither_delays_nor_overflows_the_other_one()
    {
        // 审查 #8：一个通道卡住、队列排满之后，另一个通道的通知照常、一条不少地发出。
        // 一条一条地发、等正常的通道收到再发下一条：一次塞进 150 条的话，慢机器上正常通道的发送任务还没开始，
        // 它自己的队列也会排满，测的就不是"互不影响"了。
        var logs = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([logs]);
        var registry = new ChatChannelRegistry();
        var stuck = new TestChannel("Telegram") { Hang = true };
        var healthy = new TestChannel("微信");
        registry.Register(stuck);
        registry.Register(healthy);
        using var notifier = new Notifier(registry, factory.CreateLogger<Notifier>());

        for (var i = 0; i < 150; i++)
        {
            notifier.Enqueue($"通知 {i}");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (healthy.Received.Count <= i)
            {
                Assert.True(DateTime.UtcNow < deadline, $"正常的通道没有收到第 {i + 1} 条");
                await Task.Delay(1);
            }
        }

        Assert.Equal(Enumerable.Range(0, 150).Select(i => $"通知 {i}"), healthy.Received);
        Assert.Single(stuck.Received);
        Assert.Contains(logs.Lines, l => l.Contains("Telegram 的通知队列已满", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("微信 的通知队列已满", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_notifications_log_only_the_error_type_and_message()
    {
        // 审查 #9：Telegram 的异常里套着的异常可能带着含 bot token 的地址，日志里不能有（S-21）。
        var logs = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([logs]);
        var registry = new ChatChannelRegistry();
        var failing = new TestChannel("Telegram")
        {
            Error = new TelegramException(
                "sendMessage 请求失败（ConnectionError）",
                inner: new HttpRequestException("https://api.telegram.org/bot123456:SECRET-TOKEN/sendMessage")),
        };
        registry.Register(failing);
        using var notifier = new Notifier(registry, factory.CreateLogger<Notifier>());

        await notifier.BroadcastAsync("✅ 做完了", CancellationToken.None);
        notifier.Enqueue("✅ 又做完了");
        await DevChat.EventuallyAsync(() => Task.FromResult(failing.Received.Count == 2), "两条都应该尝试发送");
        await DevChat.EventuallyAsync(() => Task.FromResult(logs.Lines.Count(l => l.Contains("发送通知失败", StringComparison.Ordinal)) == 2), "两次失败都应该记日志");

        Assert.Contains("TelegramException sendMessage 请求失败（ConnectionError）", logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-TOKEN", logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("api.telegram.org", logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Blocked_agents_learned_from_command_results_are_candidates()
    {
        var time = new FakeTimeProvider();
        var state = new ConversationState(time);
        state.NoteBlocked(AgentRef.From("d1", "mac", Api));
        Assert.Equal("api", Assert.Single(state.Blocked()).Name);
        state.ForgetAgent("d1", Api.PaneId);
        Assert.Empty(state.Blocked());

        state.NoteBlocked(AgentRef.From("d1", "mac", Api));
        time.Advance(TimeSpan.FromHours(7));
        Assert.Empty(state.Blocked());
    }

    /// <summary>测试用的聊天通道：记下收到的通知；可以让它一直卡住，或者每次都抛出指定的异常。</summary>
    private sealed class TestChannel(string name) : IChatChannel
    {
        public ConcurrentQueue<string> Received { get; } = new();

        public bool Hang { get; init; }

        public Exception? Error { get; init; }

        public string Name => name;

        public bool IsReady => true;

        public async Task SendToOwnerAsync(string text, CancellationToken cancellationToken)
        {
            Received.Enqueue(text);
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Error is not null)
            {
                throw Error;
            }
        }
    }
}
