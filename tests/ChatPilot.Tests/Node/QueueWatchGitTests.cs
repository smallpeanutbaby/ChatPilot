using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ChatPilot.Node.Config;
using ChatPilot.Node.Execution;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatPilot.Tests.Node;

/// <summary>节点上的排队（F-43）、/stop、/close（F-44、F-45）和盯梢（F-46）。</summary>
public sealed class QueueAndWatchTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeHerdr _herdr = new();
    private readonly RecordingSink _events = new();
    private readonly NodeConfig _config;
    private readonly HerdrClient _client;
    private readonly ReplyService _replies;
    private readonly PromptTracker _tracker;
    private readonly AgentWatcher _watcher;
    private readonly CommandExecutor _executor;

    public QueueAndWatchTests()
    {
        _config = new NodeConfig { AllowedRoots = [_temp.Path] };
        _client = new HerdrClient(_herdr, "herdr", null, TimeSpan.FromSeconds(5));
        _replies = new ReplyService(_client, _config, NullLogger<ReplyService>.Instance);
        _tracker = new PromptTracker(_client, _replies, _events, _config, NullLogger<PromptTracker>.Instance,
            initialPromptGrace: TimeSpan.FromMilliseconds(50), activityTimeout: TimeSpan.FromSeconds(1),
            queue: new PromptQueue(_temp.Combine("node.queue.json"), NullLogger.Instance));
        _watcher = new AgentWatcher(_client, _replies, _events, _tracker, _temp.Combine("node.watch.json"), NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        _executor = new CommandExecutor(_client, new PathPolicy(_config.AllowedRoots), _tracker, _replies, _config, NullLogger<CommandExecutor>.Instance, _watcher);
    }

    public async ValueTask DisposeAsync()
    {
        await _watcher.DisposeAsync();
        await _tracker.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task Prompts_to_a_busy_agent_are_queued_and_sent_one_by_one_after_it_finishes()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        _herdr.PromptDelay = TimeSpan.FromMilliseconds(200);

        var first = await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "一"), CancellationToken.None);
        var second = await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "二"), CancellationToken.None);
        Assert.Contains("已排队（第 1 条）", Assert.IsType<AckData>(first.Data).Message, StringComparison.Ordinal);
        Assert.Contains("已排队（第 2 条）", Assert.IsType<AckData>(second.Data).Message, StringComparison.Ordinal);
        Assert.Empty(_herdr.CallsStartingWith("agent", "prompt"));
        var listed = Assert.IsType<AgentListData>((await _executor.ExecuteAsync(new ListAgentsCommand(), CancellationToken.None)).Data);
        Assert.Equal(2, listed.Agents[0].Queued);

        // 它在电脑上做完了：排队的指令按顺序发出，每条做完都通知，第一条的通知里说还有一条在排队。
        agent.Status = "idle";
        var done1 = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "一", 1), (done1.Kind, done1.Prompt, done1.Queued));
        var done2 = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "二", 0), (done2.Kind, done2.Prompt, done2.Queued));
        Assert.Equal(["一", "二"], _herdr.CallsStartingWith("agent", "prompt").Select(c => c[3]));
        Assert.NotNull(done1.StartedAt);
    }

    [Fact]
    public async Task A_second_prompt_while_one_is_tracked_waits_for_the_first_result()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.PromptDelay = TimeSpan.FromMilliseconds(400);
        Assert.Equal("已发送", Assert.IsType<AckData>((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "一"), CancellationToken.None)).Data).Message);
        Assert.Contains("已排队", Assert.IsType<AckData>((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "二"), CancellationToken.None)).Data).Message, StringComparison.Ordinal);
        Assert.Single(_herdr.CallsStartingWith("agent", "prompt"));

        Assert.Equal("一", (await _events.WaitAsync()).Prompt);
        Assert.Equal("二", (await _events.WaitAsync()).Prompt);
    }

    [Fact]
    public async Task A_prompt_arriving_while_the_queue_pump_is_mid_submit_is_queued_not_typed_in_at_the_same_time()
    {
        // 审查：排队泵已经把一条取出来、还卡在 herdr agent prompt 上（还没开始跟踪）时直接发来的指令，不能同时敲进去。
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        _herdr.PromptDelay = TimeSpan.FromMilliseconds(300);
        Assert.Contains("已排队", Assert.IsType<AckData>((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "一"), CancellationToken.None)).Data).Message, StringComparison.Ordinal);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _herdr.PromptGate = gate;
        agent.Status = "idle";
        await DevChat.EventuallyAsync(() => Task.FromResult(_herdr.CallsStartingWith("agent", "prompt").Count == 1), "排队泵应该开始提交第一条");
        Assert.Equal(0, _tracker.QueuedCount(agent.PaneId));

        var second = await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "二"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("已排队（第 1 条）", Assert.IsType<AckData>(second.Data).Message, StringComparison.Ordinal);
        Assert.Single(_herdr.CallsStartingWith("agent", "prompt"));

        _herdr.PromptGate = null;
        gate.SetResult();
        var done1 = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "一"), (done1.Kind, done1.Prompt));
        var done2 = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "二"), (done2.Kind, done2.Prompt));
        Assert.Equal(["一", "二"], _herdr.CallsStartingWith("agent", "prompt").Select(c => c[3]));
    }

    [Fact]
    public async Task Two_prompts_arriving_together_are_sent_one_after_the_other()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.PromptDelay = TimeSpan.FromMilliseconds(300);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _herdr.PromptGate = gate;
        var first = _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "一"), CancellationToken.None);
        await DevChat.EventuallyAsync(() => Task.FromResult(_herdr.CallsStartingWith("agent", "prompt").Count == 1), "第一条应该开始提交");

        var second = await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "二"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("已排队（第 1 条）", Assert.IsType<AckData>(second.Data).Message, StringComparison.Ordinal);

        _herdr.PromptGate = null;
        gate.SetResult();
        Assert.Equal(AckData.Sent, Assert.IsType<AckData>((await first).Data).Message);
        Assert.Equal("一", (await _events.WaitAsync()).Prompt);
        Assert.Equal("二", (await _events.WaitAsync()).Prompt);
        Assert.Equal(["一", "二"], _herdr.CallsStartingWith("agent", "prompt").Select(c => c[3]));
    }

    [Fact]
    public async Task Queue_survives_a_node_restart()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        var path = _temp.Combine("restart.queue.json");
        await using (var before = Tracker(new RecordingSink(), path))
        {
            var executor = new CommandExecutor(_client, new PathPolicy(_config.AllowedRoots), before, _replies, _config, NullLogger<CommandExecutor>.Instance);
            await executor.ExecuteAsync(new PromptCommand(agent.PaneId, "重启后再发"), CancellationToken.None);
        }

        Assert.Single(new PromptQueue(path, NullLogger.Instance).Panes());
        var sink = new RecordingSink();
        await using var restarted = Tracker(sink, path);
        restarted.ResumeTrackedTasks();
        agent.Status = "idle";
        var ev = await sink.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "重启后再发"), (ev.Kind, ev.Prompt));
        Assert.Empty(new PromptQueue(path, NullLogger.Instance).Panes());
    }

    private PromptTracker Tracker(RecordingSink sink, string queuePath) =>
        new(_client, _replies, sink, _config, NullLogger<PromptTracker>.Instance,
            initialPromptGrace: TimeSpan.FromMilliseconds(50), activityTimeout: TimeSpan.FromSeconds(1),
            queue: new PromptQueue(queuePath, NullLogger.Instance));

    [Fact]
    public async Task Queue_is_dropped_with_a_notice_when_the_agent_goes_away()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "一"), CancellationToken.None);
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "二"), CancellationToken.None);
        agent.Kind = "codex";
        agent.Status = "idle";
        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Failed, ev.Kind);
        Assert.Contains("排队的 2 条指令没有发出", ev.Text, StringComparison.Ordinal);
        Assert.Empty(_herdr.CallsStartingWith("agent", "prompt"));
    }

    [Fact]
    public async Task Only_prompts_queued_for_a_previous_terminal_are_dropped()
    {
        // 审查：herdr 重启后同一个 pane 换成了新终端，给旧终端排的丢掉，给新终端排的照常发。
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "旧"), CancellationToken.None);
        agent.TerminalId = "term_new";
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "新"), CancellationToken.None);
        agent.Status = "idle";

        var dropped = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Failed, "旧"), (dropped.Kind, dropped.Prompt));
        Assert.Contains("排队的 1 条指令没有发出", dropped.Text, StringComparison.Ordinal);
        var done = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "新"), (done.Kind, done.Prompt));
        Assert.Equal(["新"], _herdr.CallsStartingWith("agent", "prompt").Select(c => c[3]));
    }

    [Fact]
    public async Task Images_are_removed_again_when_the_prompt_cannot_be_queued()
    {
        // 审查：排队满了，刚下载的图片不能留在 agent 的目录里。
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 5];
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        agent.Cwd = _temp.CreateDirectory("proj");
        for (var i = 0; i < PromptQueue.MaxPerAgent; i++)
        {
            Assert.True((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, $"第 {i} 条"), CancellationToken.None)).Ok);
        }

        var downloader = new AttachmentDownloader(new HttpClient(new FixedHandler(png)), new Uri("https://example.com"), "tok", _temp.Combine("fallback"));
        var executor = new CommandExecutor(_client, new PathPolicy(_config.AllowedRoots), _tracker, _replies, _config, NullLogger<CommandExecutor>.Instance, _watcher, attachments: downloader);
        var media = new MediaRef("abc", png.Length, Convert.ToHexStringLower(SHA256.HashData(png)));
        var outcome = await executor.ExecuteAsync(new MediaPromptCommand(agent.PaneId, "看图", [media]), CancellationToken.None);

        Assert.Equal(ErrorCodes.Busy, outcome.Error?.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(agent.Cwd, ".chatpilot", "attachments")));
    }

    [Fact]
    public async Task A_slow_image_download_does_not_use_up_the_prompt_budget()
    {
        // 审查：下载图片和提交共用一个预算，服务器上传慢、图片又大时，提交就被掐断（文字可能已经敲进去了还报超时）。
        var agent = _herdr.AddAgent("tideq", "TideQ");
        agent.Cwd = _temp.CreateDirectory("proj");
        var executor = MediaExecutor(new SlowMediaHandler(Png, () => Task.Delay(1500)), commandBudget: TimeSpan.FromSeconds(1));

        var outcome = await executor.ExecuteAsync(new MediaPromptCommand(agent.PaneId, "看图", [PngRef]), CancellationToken.None);
        Assert.Equal(AckData.Sent, Assert.IsType<AckData>(outcome.Data).Message);
        Assert.Single(_herdr.CallsStartingWith("agent", "prompt"));
    }

    [Fact]
    public async Task The_agent_is_checked_again_after_the_images_are_downloaded()
    {
        // 审查：下载期间它弹出了确认框，不能拿下载前查到的"空闲"去提交；没交出去的图片要删掉。
        _herdr.Screen = SampleScreens.PermissionDialog;
        var agent = _herdr.AddAgent("tideq", "TideQ");
        agent.Cwd = _temp.CreateDirectory("proj");
        var executor = MediaExecutor(new SlowMediaHandler(Png, () =>
        {
            agent.Status = "blocked";
            return Task.CompletedTask;
        }));

        var outcome = await executor.ExecuteAsync(new MediaPromptCommand(agent.PaneId, "看图", [PngRef]), CancellationToken.None);
        Assert.Equal(ErrorCodes.AgentBlocked, outcome.Error?.Code);
        Assert.Empty(_herdr.CallsStartingWith("agent", "prompt"));
        Assert.Empty(Directory.GetFiles(Path.Combine(agent.Cwd, ".chatpilot", "attachments")));
    }

    [Fact]
    public async Task A_prompt_already_handed_to_herdr_is_not_aborted_when_the_command_runs_out_of_time()
    {
        // 审查：到时（或者连接断开）时 herdr agent prompt 可能已经把文字敲进去了，不能半路杀掉、当成没发出去，否则用户重发就做两遍。
        var agent = _herdr.AddAgent("tideq", "TideQ");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _herdr.PromptGate = gate;
        using var deadline = new CancellationTokenSource();
        var submit = _tracker.SubmitAsync(await _client.GetAgentAsync(agent.PaneId, CancellationToken.None), "一", null, deadline.Token);
        await DevChat.EventuallyAsync(() => Task.FromResult(_herdr.CallsStartingWith("agent", "prompt").Count == 1), "应该开始提交");

        await deadline.CancelAsync();
        await Task.Delay(100);
        _herdr.PromptGate = null;
        gate.SetResult();
        Assert.Equal(AckData.Sent, Assert.IsType<AckData>((await submit).Data).Message);
        var done = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "一"), (done.Kind, done.Prompt));
    }

    [Fact]
    public async Task Images_of_a_queued_prompt_are_deleted_when_the_queue_is_cleared()
    {
        // 审查：排队的指令被 /stop 清掉（或者过期、agent 不在了）时，它带的图片没人会看了，不能一直留在 agent 的目录里。
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        agent.Cwd = _temp.CreateDirectory("proj");
        var executor = MediaExecutor(new FixedHandler(Png));
        var queued = await executor.ExecuteAsync(new MediaPromptCommand(agent.PaneId, "看图", [PngRef]), CancellationToken.None);
        Assert.Contains("已排队", Assert.IsType<AckData>(queued.Data).Message, StringComparison.Ordinal);
        var folder = Path.Combine(agent.Cwd, ".chatpilot", "attachments");
        Assert.Single(Directory.GetFiles(folder));

        var stopped = await executor.ExecuteAsync(new StopAgentCommand("tideq"), CancellationToken.None);
        Assert.Equal("已按 Esc 打断它，清空了 1 条排队的指令", Assert.IsType<AckData>(stopped.Data).Message);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Stop_presses_esc_only_while_working_and_clears_the_queue()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "一"), CancellationToken.None);
        var stopped = await _executor.ExecuteAsync(new StopAgentCommand("tideq"), CancellationToken.None);
        Assert.Equal("已按 Esc 打断它，清空了 1 条排队的指令", Assert.IsType<AckData>(stopped.Data).Message);
        Assert.Contains(_herdr.CallsStartingWith("agent", "send-keys"), c => c[^1] == "esc");
        Assert.Equal(0, _tracker.QueuedCount(agent.PaneId));

        agent.Status = "idle";
        var idle = await _executor.ExecuteAsync(new StopAgentCommand("tideq"), CancellationToken.None);
        Assert.Equal("它现在没在干活，没有按键", Assert.IsType<AckData>(idle.Data).Message);
        agent.Status = "blocked";
        Assert.StartsWith("它在等你确认，没有按键", Assert.IsType<AckData>((await _executor.ExecuteAsync(new StopAgentCommand("tideq"), CancellationToken.None)).Data).Message, StringComparison.Ordinal);
        Assert.Single(_herdr.CallsStartingWith("agent", "send-keys"));
    }

    [Fact]
    public async Task Close_closes_the_pane_and_forgets_its_queue()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "一"), CancellationToken.None);
        var closed = await _executor.ExecuteAsync(new CloseAgentCommand("tideq"), CancellationToken.None);
        Assert.True(closed.Ok);
        Assert.Equal(["pane", "close", agent.PaneId], Assert.Single(_herdr.CallsStartingWith("pane", "close")));
        Assert.Null(_herdr.Find(agent.PaneId));
        Assert.Equal(0, _tracker.QueuedCount(agent.PaneId));
        Assert.Equal(ErrorCodes.NotFound, (await _executor.ExecuteAsync(new CloseAgentCommand("tideq"), CancellationToken.None)).Error?.Code);
    }

    [Fact]
    public async Task Closing_a_tracked_agent_does_not_report_a_bogus_failure()
    {
        // 审查：/close 正在跟踪的 agent，跟踪方发现它不见了，不能当成任务失败（或者做完了）通知。
        var fresh = _herdr.AddAgent("fresh", "Fresh");
        var busy = _herdr.AddAgent("busy", "Busy");
        _herdr.SwallowPrompts = 1;
        Assert.True((await _executor.ExecuteAsync(new PromptCommand(fresh.PaneId, "刚发出去"), CancellationToken.None)).Ok);
        _herdr.PromptDelay = TimeSpan.FromSeconds(30);
        Assert.True((await _executor.ExecuteAsync(new PromptCommand(busy.PaneId, "正在干"), CancellationToken.None)).Ok);
        await Task.Delay(200);

        Assert.True((await _executor.ExecuteAsync(new CloseAgentCommand("fresh"), CancellationToken.None)).Ok);
        Assert.True((await _executor.ExecuteAsync(new CloseAgentCommand("busy"), CancellationToken.None)).Ok);
        await DevChat.EventuallyAsync(
            () => Task.FromResult(!_tracker.IsTracking(fresh.PaneId) && !_tracker.IsTracking(busy.PaneId)), "关闭后应该停止跟踪");
        await Task.Delay(1500);
        Assert.Empty(_events.Received);
    }

    [Fact]
    public async Task Queued_prompts_wait_a_moment_when_herdr_cannot_find_the_agent()
    {
        // 审查：Claude Code 重启或自我更新时 herdr 一时查不到它，不能马上丢掉整个队列。
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        var path = _temp.Combine("unrecognized.queue.json");
        new PromptQueue(path, NullLogger.Instance).Enqueue(await _client.GetAgentAsync(agent.PaneId, CancellationToken.None), "一", DateTimeOffset.UtcNow);
        agent.Hidden = true;
        agent.Status = "idle";

        var sink = new RecordingSink();
        await using var tracker = Tracker(sink, path);
        tracker.ResumeTrackedTasks();
        await Task.Delay(700);
        Assert.Empty(sink.Received);
        Assert.Equal(1, tracker.QueuedCount(agent.PaneId));

        agent.Hidden = false;
        var ev = await sink.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "一"), (ev.Kind, ev.Prompt));
    }

    [Fact]
    public async Task Watcher_still_reports_after_herdr_restarts_and_reuses_the_pane_id()
    {
        // 审查：herdr 重启后 pane ID 从头编号、状态序号也重新计；旧终端上报过的大序号不能压住新终端的通知。
        var agent = _herdr.AddAgent("tideq", "TideQ");
        for (var i = 0; i < 10; i++)
        {
            agent.Status = i % 2 == 0 ? "working" : "idle";
        }

        Assert.True((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "只回复：收到"), CancellationToken.None)).Ok);
        Assert.Equal(AgentEventKind.Completed, (await _events.WaitAsync()).Kind);
        Assert.True(agent.Seq > 10);

        await _client.ClosePaneAsync(agent.PaneId, CancellationToken.None);
        await _client.StartAgentAsync("tideq", "claude", agent.PaneId, TimeSpan.FromSeconds(5), CancellationToken.None);
        var restarted = _herdr.Find(agent.PaneId)!;
        restarted.TerminalId = "term_after_herdr_restart";
        await _executor.ExecuteAsync(new WatchCommand(null, true), CancellationToken.None);
        _watcher.Start();
        await Task.Delay(200);

        restarted.Status = "working";
        await Task.Delay(200);
        restarted.Status = "idle";
        var done = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, agent.PaneId, (string?)null), (done.Kind, done.Agent.PaneId, done.Prompt));
    }

    [Fact]
    public async Task Watched_agent_reports_work_done_on_the_computer_and_new_confirmation_dialogs()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.Screen = SampleScreens.ClaudeReply;
        var ack = await _executor.ExecuteAsync(new WatchCommand("tideq", true), CancellationToken.None);
        Assert.StartsWith("开始盯着 tideq", Assert.IsType<AckData>(ack.Data).Message, StringComparison.Ordinal);
        _watcher.Start();

        agent.Status = "working";
        await Task.Delay(300);
        agent.Status = "idle";
        var done = await _events.WaitAsync();
        Assert.Equal((AgentEventKind.Completed, "收到"), (done.Kind, done.Text));

        _herdr.Screen = SampleScreens.PermissionDialog;
        agent.Status = "blocked";
        Assert.Equal(AgentEventKind.Blocked, (await _events.WaitAsync()).Kind);

        // 停止盯之后不再通知。
        await _executor.ExecuteAsync(new WatchCommand("tideq", false), CancellationToken.None);
        agent.Status = "working";
        await Task.Delay(200);
        agent.Status = "idle";
        await Task.Delay(300);
        Assert.Equal(2, _events.Received.Count);
    }

    [Fact]
    public async Task Work_dispatched_through_chatpilot_is_not_reported_twice_by_the_watcher()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        await _executor.ExecuteAsync(new WatchCommand(null, true), CancellationToken.None);
        _watcher.Start();
        await Task.Delay(150);
        _herdr.PromptDelay = TimeSpan.FromMilliseconds(400);
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "只回复：收到"), CancellationToken.None);
        Assert.Equal(AgentEventKind.Completed, (await _events.WaitAsync()).Kind);
        await Task.Delay(500);
        Assert.Single(_events.Received);
    }

    [Fact]
    public async Task Watch_settings_survive_a_restart_and_all_can_be_turned_off()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        await _executor.ExecuteAsync(new WatchCommand("tideq", true), CancellationToken.None);
        var current = await _client.GetAgentAsync("tideq", CancellationToken.None);
        await using (var reloaded = new AgentWatcher(_client, _replies, _events, _tracker, _temp.Combine("node.watch.json"), NullLogger.Instance))
        {
            Assert.True(reloaded.IsWatched(current));
            Assert.False(reloaded.WatchAll);
        }

        await _executor.ExecuteAsync(new WatchCommand(null, true), CancellationToken.None);
        var listed = Assert.IsType<AgentListData>((await _executor.ExecuteAsync(new ListAgentsCommand(), CancellationToken.None)).Data);
        Assert.True(listed.WatchAll);
        Assert.True(listed.Agents.Single().Watched);

        await _executor.ExecuteAsync(new WatchCommand(null, false), CancellationToken.None);
        listed = Assert.IsType<AgentListData>((await _executor.ExecuteAsync(new ListAgentsCommand(), CancellationToken.None)).Data);
        Assert.False(listed.WatchAll);
        Assert.False(listed.Agents.Single().Watched);
        Assert.Equal(agent.PaneId, listed.Agents.Single().PaneId);
    }

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 7, 7];

    private static MediaRef PngRef => new("abc", Png.Length, Convert.ToHexStringLower(SHA256.HashData(Png)));

    private CommandExecutor MediaExecutor(HttpMessageHandler handler, TimeSpan? commandBudget = null) =>
        new(_client, new PathPolicy(_config.AllowedRoots), _tracker, _replies, _config, NullLogger<CommandExecutor>.Instance, _watcher,
            attachments: new AttachmentDownloader(new HttpClient(handler), new Uri("https://example.com"), "tok", _temp.Combine("fallback")),
            commandBudget: commandBudget);

    private sealed class FixedHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    /// <summary>下载时先做一件事（等一会儿、或者改 agent 的状态）再返回图片。</summary>
    private sealed class SlowMediaHandler(byte[] body, Func<Task> whileDownloading) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await whileDownloading();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }
}

/// <summary><c>/diff</c>（F-47）：用真的 git 在临时仓库里验证；没装 git 的环境跳过。</summary>
public sealed class GitInspectorTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly GitInspector _git = new(new ProcessRunner(), "git", TimeSpan.FromSeconds(20));

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Summary_and_file_diff()
    {
        if (!Git("init", "-q", "-b", "main"))
        {
            return;
        }

        File.WriteAllText(_repo.Combine("a.txt"), "旧内容\n");
        Git("add", "a.txt");
        Git("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "第一次提交");
        File.WriteAllText(_repo.Combine("a.txt"), "新内容\n");
        File.WriteAllText(_repo.Combine("新文件.md"), "x");

        var summary = await _git.SummaryAsync(_repo.Path, CancellationToken.None);
        Assert.Contains("分支：main", summary, StringComparison.Ordinal);
        Assert.Contains(" M a.txt", summary, StringComparison.Ordinal);
        Assert.Contains("?? 新文件.md", summary, StringComparison.Ordinal);
        Assert.Contains("1 file changed", summary, StringComparison.Ordinal);
        Assert.Contains("第一次提交", summary, StringComparison.Ordinal);

        var diff = await _git.FileDiffAsync(_repo.Path, "a.txt", CancellationToken.None);
        Assert.Contains("-旧内容", diff, StringComparison.Ordinal);
        Assert.Contains("+新内容", diff, StringComparison.Ordinal);
        Assert.Contains("还没纳入 git", await _git.FileDiffAsync(_repo.Path, "新文件.md", CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("没有改动", await _git.FileDiffAsync(_repo.Path, "nope.txt", CancellationToken.None), StringComparison.Ordinal);
        await Assert.ThrowsAsync<GitException>(() => _git.FileDiffAsync(_repo.Path, "../x", CancellationToken.None));
    }

    [Fact]
    public async Task Not_a_repository()
    {
        if (!Git("--version"))
        {
            return;
        }

        var ex = await Assert.ThrowsAsync<GitException>(() => _git.SummaryAsync(_repo.Path, CancellationToken.None));
        Assert.Contains("不是 git 仓库", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_git_is_reported_clearly()
    {
        var missing = new GitInspector(new ProcessRunner(), "definitely-not-git-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<GitException>(() => missing.SummaryAsync(_repo.Path, CancellationToken.None));
        Assert.Contains("找不到 git", ex.Message, StringComparison.Ordinal);
    }

    private bool Git(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = _repo.Path, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            process.WaitForExit(20_000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>图片附件（F-48）：下载、校验、只按内容决定扩展名、不进 git。</summary>
public sealed class AttachmentDownloaderTests : IDisposable
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Saves_into_the_agent_directory_with_a_self_ignoring_folder()
    {
        var handler = new StubHandler(Png);
        var downloader = new AttachmentDownloader(new HttpClient(handler), new Uri("https://example.com/sub"), "tok", _temp.Combine("fallback"));
        var project = _temp.CreateDirectory("proj");
        var saved = await downloader.SaveAsync([Ref(Png)], project, CancellationToken.None);

        var path = Assert.Single(saved);
        Assert.StartsWith(Path.Combine(project, ".chatpilot", "attachments"), path, StringComparison.Ordinal);
        Assert.EndsWith(".png", path, StringComparison.Ordinal);
        Assert.Equal(Png, File.ReadAllBytes(path));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(project, ".chatpilot", ".gitignore")));
        Assert.Equal("https://example.com/sub/api/media/abc123", handler.LastUri?.ToString());
        Assert.Equal("Bearer tok", handler.LastAuthorization);
        Assert.Contains(path, AttachmentDownloader.AppendToPrompt("看看", saved), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_the_node_directory_and_rejects_tampered_or_non_image_content()
    {
        var downloader = new AttachmentDownloader(new HttpClient(new StubHandler(Png)), new Uri("https://example.com"), "tok", _temp.Combine("fallback"));
        var saved = await downloader.SaveAsync([Ref(Png)], agentDirectory: null, CancellationToken.None);
        Assert.StartsWith(_temp.Combine("fallback"), Assert.Single(saved), StringComparison.Ordinal);

        var wrongHash = Ref(Png) with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<AttachmentException>(() => downloader.SaveAsync([wrongHash], null, CancellationToken.None));

        var text = "<svg onload=x>"u8.ToArray();
        var notImage = new AttachmentDownloader(new HttpClient(new StubHandler(text)), new Uri("https://example.com"), "tok", _temp.Combine("fallback"));
        await Assert.ThrowsAsync<AttachmentException>(() => notImage.SaveAsync([Ref(text)], null, CancellationToken.None));

        var gone = new AttachmentDownloader(new HttpClient(new StubHandler(null)), new Uri("https://example.com"), "tok", _temp.Combine("fallback"));
        var ex = await Assert.ThrowsAsync<AttachmentException>(() => gone.SaveAsync([Ref(Png)], null, CancellationToken.None));
        Assert.Contains("HTTP 404", ex.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<AttachmentException>(() => downloader.SaveAsync([Ref(Png) with { Id = "../x" }], null, CancellationToken.None));
    }

    [Fact]
    public async Task Links_in_the_agent_directory_are_not_followed()
    {
        // 审查：克隆的仓库里带了 .chatpilot -> ../别的目录（或者 .gitignore 是个链接），不能把 .gitignore 和图片写到那边去。
        var downloader = new AttachmentDownloader(new HttpClient(new StubHandler(Png)), new Uri("https://example.com"), "tok", _temp.Combine("fallback"));
        var project = _temp.CreateDirectory("proj");
        var elsewhere = _temp.CreateDirectory("elsewhere");
        if (!TryLinkDirectory(Path.Combine(project, ".chatpilot"), elsewhere))
        {
            return;
        }

        var saved = await downloader.SaveAsync([Ref(Png)], project, CancellationToken.None);
        Assert.StartsWith(_temp.Combine("fallback"), Assert.Single(saved), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));

        // .chatpilot 是真目录、里面的 .gitignore 是指向别处（还不存在）的链接：同样不写。
        var other = _temp.CreateDirectory("other");
        Directory.CreateDirectory(Path.Combine(other, ".chatpilot"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(other, ".chatpilot", ".gitignore"), Path.Combine(elsewhere, ".gitignore"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        saved = await downloader.SaveAsync([Ref(Png)], other, CancellationToken.None);
        Assert.StartsWith(_temp.Combine("fallback"), Assert.Single(saved), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public async Task Old_images_are_cleaned_up_and_only_saved_images_are_ever_deleted()
    {
        var downloader = new AttachmentDownloader(new HttpClient(new StubHandler(Png)), new Uri("https://example.com"), "tok", _temp.Combine("fallback"));
        var project = _temp.CreateDirectory("proj");
        var folder = _temp.CreateDirectory("proj", ".chatpilot", "attachments");
        var old = Path.Combine(folder, "20200101-000000-1-abcdef.png");
        var recent = Path.Combine(folder, "20200101-000000-2-abcdef.png");
        var foreign = Path.Combine(folder, "notes.txt");
        File.WriteAllBytes(old, Png);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - AttachmentDownloader.RetentionPeriod - TimeSpan.FromHours(1));
        File.WriteAllBytes(recent, Png);
        File.WriteAllText(foreign, "x");
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddYears(-1));

        await downloader.SaveAsync([Ref(Png)], project, CancellationToken.None);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));

        // 排队记录在磁盘上，路径可能被改过：不是节点自己起名的图片一律不删。
        AttachmentDownloader.DeleteQuietly([foreign]);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task Images_already_saved_are_removed_when_a_later_one_fails()
    {
        var downloader = new AttachmentDownloader(new HttpClient(new StubHandler(Png)), new Uri("https://example.com"), "tok", _temp.Combine("fallback"));
        var project = _temp.CreateDirectory("proj");
        var tampered = Ref(Png) with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<AttachmentException>(() => downloader.SaveAsync([Ref(Png), tampered], project, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(project, ".chatpilot", "attachments")));
    }

    private static MediaRef Ref(byte[] data) => new("abc123", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));

    /// <summary>建一个指向 <paramref name="target"/> 的目录链接；Windows 上没有建符号链接的权限时改用目录联接。都建不了时返回 false（跳过测试）。</summary>
    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        var info = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private sealed class StubHandler(byte[]? body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
