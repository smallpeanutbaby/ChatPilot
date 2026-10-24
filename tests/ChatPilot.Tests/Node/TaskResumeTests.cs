using ChatPilot.Node.Config;
using ChatPilot.Node.Execution;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatPilot.Tests.Node;

/// <summary>节点重启后接着跟踪没做完的任务。</summary>
public sealed class TaskResumeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeHerdr _herdr = new();
    private readonly NodeConfig _config = new() { AllowedRoots = [Path.GetTempPath()] };
    private readonly string _tasksPath;

    public TaskResumeTests() => _tasksPath = _temp.Combine("node.tasks.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Prompt_is_recorded_until_its_result_reaches_the_server()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        var sink = new RecordingSink { DeliverImmediately = false };
        var tasks = Tasks();
        await using (var tracker = Tracker(sink, tasks))
        {
            Assert.True((await Executor(tracker).ExecuteAsync(new PromptCommand(agent.PaneId, "只回复：收到"), CancellationToken.None)).Ok);
            Assert.Equal("只回复：收到", Assert.Single(Tasks().Snapshot()).Prompt);
            Assert.Equal(AgentEventKind.Completed, (await sink.WaitAsync()).Kind);

            // 结果还没发到服务器（比如正好断网），记录要留着。
            await Task.Delay(200);
            Assert.Single(Tasks().Snapshot());
        }

        // 节点重启：结果再报一次，这次发出去了，记录删掉。
        var again = new RecordingSink();
        await using var restarted = Tracker(again, Tasks());
        restarted.ResumeTrackedTasks();
        var ev = await again.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
        Assert.Equal("收到", ev.Text);
        await DevChat.EventuallyAsync(() => Task.FromResult(Tasks().Snapshot().Count == 0), "结果发出后应该删掉记录");
    }

    [Fact]
    public async Task Task_that_finished_while_the_node_was_down_is_reported_on_start()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        Remember(agent, prompt: "跑一下测试");
        agent.Status = "working";
        agent.Status = "idle";

        var sink = new RecordingSink();
        await using var tracker = Tracker(sink, Tasks());
        tracker.ResumeTrackedTasks();
        var ev = await sink.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
        Assert.Equal("收到", ev.Text);
    }

    [Fact]
    public async Task Task_still_running_after_restart_is_followed_to_the_end()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        Remember(agent, prompt: "跑一下测试");
        agent.Status = "working";

        var sink = new RecordingSink();
        await using var tracker = Tracker(sink, Tasks());
        tracker.ResumeTrackedTasks();
        await Task.Delay(300);
        Assert.Empty(sink.Received);

        agent.Status = "idle";
        Assert.Equal(AgentEventKind.Completed, (await sink.WaitAsync()).Kind);
    }

    [Fact]
    public async Task Pane_now_held_by_another_terminal_is_not_resumed()
    {
        // herdr 重启后 pane ID 从头编号，同一个 pane ID 可能已经是别的终端了。
        var agent = _herdr.AddAgent("tideq", "TideQ");
        Remember(agent, prompt: "跑一下测试");
        agent.TerminalId = "term_after_herdr_restart";
        agent.Status = "working";
        agent.Status = "idle";

        var sink = new RecordingSink();
        await using var tracker = Tracker(sink, Tasks());
        tracker.ResumeTrackedTasks();
        await DevChat.EventuallyAsync(() => Task.FromResult(Tasks().Snapshot().Count == 0), "应该丢掉这条记录");
        Assert.Empty(sink.Received);
    }

    [Fact]
    public async Task Task_that_never_started_says_so()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        Remember(agent, prompt: "跑一下测试");

        var sink = new RecordingSink();
        await using var tracker = Tracker(sink, Tasks());
        tracker.ResumeTrackedTasks();
        var ev = await sink.WaitAsync();
        Assert.Equal(AgentEventKind.Failed, ev.Kind);
        Assert.Contains("没看到它开始工作", ev.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_a_trust_dialog_that_goes_straight_to_idle_leaves_no_record()
    {
        // 确认后直接回到空闲、没有结果要通知时，不能留下记录，否则重启后会莫名其妙通知一次。
        var agent = _herdr.AddAgent("tideq", "TideQ", "blocked");
        var sink = new RecordingSink();
        var tasks = Tasks();
        await using var tracker = Tracker(sink, tasks);
        tracker.WatchAfterResponse(await Client().GetAgentAsync(agent.PaneId, CancellationToken.None));
        Assert.Single(tasks.Snapshot());
        agent.Status = "idle";
        await DevChat.EventuallyAsync(() => Task.FromResult(tasks.Snapshot().Count == 0), "应该删掉记录");
        Assert.Empty(sink.Received);
    }

    [Fact]
    public void Corrupt_or_missing_record_files_are_ignored()
    {
        Assert.Empty(Tasks().Snapshot());
        File.WriteAllText(_tasksPath, "{ not json");
        Assert.Empty(Tasks().Snapshot());
        Assert.Equal(_temp.Combine("node.tasks.json"), TrackedTasks.PathFor(_temp.Combine("node.json")));
    }

    private TrackedTasks Tasks() => new(_tasksPath, NullLogger.Instance);

    private void Remember(FakeHerdr.Agent agent, string? prompt) =>
        Tasks().Track(new TrackedTask(Guid.NewGuid(), agent.PaneId, agent.TerminalId, agent.Kind, prompt, agent.Seq, DateTimeOffset.UtcNow));

    private HerdrClient Client() => new(_herdr, "herdr", null, TimeSpan.FromSeconds(5));

    private PromptTracker Tracker(RecordingSink sink, TrackedTasks tasks)
    {
        var client = Client();
        var replies = new ReplyService(client, _config, NullLogger<ReplyService>.Instance);
        return new PromptTracker(client, replies, sink, _config, NullLogger<PromptTracker>.Instance,
            initialPromptGrace: TimeSpan.FromMilliseconds(50), activityTimeout: TimeSpan.FromSeconds(1), tasks: tasks);
    }

    private CommandExecutor Executor(PromptTracker tracker)
    {
        var client = Client();
        var replies = new ReplyService(client, _config, NullLogger<ReplyService>.Instance);
        return new CommandExecutor(client, new PathPolicy(_config.AllowedRoots), tracker, replies, _config, NullLogger<CommandExecutor>.Instance);
    }
}
