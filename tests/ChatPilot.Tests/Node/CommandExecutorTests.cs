using System.Collections.Concurrent;
using ChatPilot.Node.Config;
using ChatPilot.Node.Connection;
using ChatPilot.Node.Execution;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatPilot.Tests.Node;

/// <summary>节点执行指令（F-10 到 F-21，S-07、S-10、S-11），herdr 用模拟的。</summary>
public sealed class CommandExecutorTests : IAsyncDisposable
{
    private readonly TempDirectory _root = new();
    private readonly FakeHerdr _herdr = new();
    private readonly RecordingSink _events = new();
    private readonly NodeConfig _config;
    private readonly PromptTracker _tracker;
    private readonly CommandExecutor _executor;

    public CommandExecutorTests()
    {
        _config = new NodeConfig { AllowedRoots = [_root.Path], AgentStartTimeoutSeconds = 10 };
        var client = new HerdrClient(_herdr, "herdr", null, TimeSpan.FromSeconds(5));
        var replies = new ReplyService(client, _config, NullLogger<ReplyService>.Instance);
        _tracker = new PromptTracker(client, replies, _events, _config, NullLogger<PromptTracker>.Instance, initialPromptGrace: TimeSpan.FromMilliseconds(50), activityTimeout: TimeSpan.FromSeconds(1));
        _executor = new CommandExecutor(client, new PathPolicy(_config.AllowedRoots), _tracker, replies, _config, NullLogger<CommandExecutor>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _tracker.DisposeAsync();
        _root.Dispose();
    }

    [Fact]
    public async Task List_includes_workspace_labels()
    {
        _herdr.AddAgent("tideq", "TideQ", "working");
        _herdr.AddAgent(null, "workhub", "done");
        var outcome = await _executor.ExecuteAsync(new ListAgentsCommand(), CancellationToken.None);
        var data = Assert.IsType<AgentListData>(outcome.Data);
        Assert.Equal(["tideq", "workhub"], data.Agents.Select(a => a.DisplayName));
        Assert.Equal("TideQ", data.Agents[0].WorkspaceLabel);
    }

    [Fact]
    public async Task Prompt_acks_then_reports_reply_when_done()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.PromptDelay = TimeSpan.FromMilliseconds(300);
        var outcome = await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "只回复：收到"), CancellationToken.None);
        Assert.True(outcome.Ok);
        Assert.IsType<AckData>(outcome.Data);

        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
        Assert.Equal("收到", ev.Text);
        Assert.Contains(_herdr.CallsStartingWith("agent", "prompt"), c => c.Contains("只回复：收到") && !c.Contains("--wait"));
        Assert.NotEmpty(_herdr.CallsStartingWith("agent", "wait"));
    }

    [Fact]
    public async Task Completion_is_reported_even_when_reading_the_reply_fails()
    {
        // 审查 #2：整理回复失败不能吞掉通知。
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.ReadError = "timeout";
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "hi"), CancellationToken.None);
        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
        Assert.Contains("/read", ev.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocked_is_reported_even_when_reading_the_screen_fails()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.StatusAfterPrompt = "blocked";
        _herdr.ReadError = "timeout";
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "删掉 build"), CancellationToken.None);
        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Blocked, ev.Kind);
        Assert.Contains("读取屏幕失败", ev.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_watcher_per_agent_so_back_to_back_prompts_report_once()
    {
        // 审查 #6：同一个 agent 不会同时挂多个后台等待，也不会重复通知。
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.PromptDelay = TimeSpan.FromMilliseconds(500);
        Assert.True((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "一"), CancellationToken.None)).Ok);
        Assert.True((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "二"), CancellationToken.None)).Ok);
        await _events.WaitAsync();
        await Task.Delay(800);
        Assert.Single(_events.Received);
    }

    [Fact]
    public async Task Failed_start_closes_the_new_workspace()
    {
        // 审查 #10：启动失败不能留下半启动的工作区。
        _herdr.StartError = "boom";
        var outcome = await _executor.ExecuteAsync(new StartAgentCommand(_root.Path, "claude", "x", null), CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Single(_herdr.CallsStartingWith("workspace", "close"));
    }

    [Fact]
    public async Task Confirming_with_keys_keeps_tracking_like_yes()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ", "blocked");
        await _executor.ExecuteAsync(new SendKeysCommand(agent.PaneId, ["down", "enter"]), CancellationToken.None);
        agent.Status = "working";
        await Task.Delay(700);
        agent.Status = "idle";
        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
    }

    [Fact]
    public async Task Panes_without_a_recognized_agent_are_not_prompted()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        agent.Kind = null;
        var outcome = await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "hi"), CancellationToken.None);
        Assert.Equal(ErrorCodes.NotFound, outcome.Error?.Code);
        Assert.Empty(_herdr.CallsStartingWith("agent", "prompt"));
    }

    [Fact]
    public async Task Prompt_reports_blocked_with_screen()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.StatusAfterPrompt = "blocked";
        _herdr.Screen = SampleScreens.PermissionDialog;
        await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "删掉 build"), CancellationToken.None);
        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Blocked, ev.Kind);
        Assert.Contains("Do you want to proceed?", ev.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_fails_fast_when_agent_is_waiting_for_confirmation()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ", "blocked");
        _herdr.Screen = SampleScreens.TrustDialog;
        var outcome = await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "hi"), CancellationToken.None);
        Assert.Equal(ErrorCodes.AgentBlocked, outcome.Error?.Code);
        Assert.Contains("Yes, I trust this folder", outcome.Error?.Message, StringComparison.Ordinal);
        Assert.Empty(_herdr.CallsStartingWith("agent", "prompt"));
    }

    [Fact]
    public async Task Quick_prompt_failures_are_returned_directly()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.PromptError = "agent_prompt_stalled";
        var outcome = await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "hi"), CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal(ErrorCodes.HerdrError, outcome.Error?.Code);
        await Task.Delay(200);
        Assert.Empty(_events.Received);
    }

    [Theory]
    [InlineData("bad target!")]
    [InlineData("--help")]
    [InlineData("Tideq")]
    public async Task Invalid_targets_are_rejected_without_calling_herdr(string target)
    {
        var outcome = await _executor.ExecuteAsync(new PromptCommand(target, "hi"), CancellationToken.None);
        Assert.Equal(ErrorCodes.InvalidArgument, outcome.Error?.Code);
        Assert.Empty(_herdr.Calls);
    }

    [Fact]
    public async Task Oversized_prompt_is_rejected()
    {
        // S-11
        var outcome = await _executor.ExecuteAsync(new PromptCommand("tideq", new string('x', ProtocolInfo.MaxPromptChars + 1)), CancellationToken.None);
        Assert.Equal(ErrorCodes.InvalidArgument, outcome.Error?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Read_line_count_is_bounded(int lines)
    {
        var outcome = await _executor.ExecuteAsync(new ReadCommand("tideq", lines), CancellationToken.None);
        Assert.Equal(ErrorCodes.InvalidArgument, outcome.Error?.Code);
    }

    [Fact]
    public async Task Read_returns_terminal_text()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        var outcome = await _executor.ExecuteAsync(new ReadCommand(agent.PaneId, 40), CancellationToken.None);
        Assert.Contains("⏺ 收到", Assert.IsType<TextData>(outcome.Data).Text, StringComparison.Ordinal);
        Assert.Contains(_herdr.CallsStartingWith("agent", "read"), c => c.SequenceEqual(["agent", "read", agent.PaneId, "--source", "recent-unwrapped", "--lines", "120"]));
    }

    [Fact]
    public async Task Start_agent_creates_workspace_and_uses_builtin_kind()
    {
        var project = _root.CreateDirectory("TideQ");
        var outcome = await _executor.ExecuteAsync(new StartAgentCommand(project, "claude", null, null), CancellationToken.None);
        var started = Assert.IsType<AgentStartedData>(outcome.Data);
        Assert.True(started.Ready);
        Assert.Equal("tideq", started.Agent.Name);
        Assert.Contains(_herdr.CallsStartingWith("workspace", "create"), c => c.Contains(project) && c.Contains("--no-focus"));
        Assert.Contains(_herdr.CallsStartingWith("agent", "start"), c => c.Contains("tideq") && c.Contains("claude"));
    }

    [Fact]
    public async Task Start_agent_with_reclaude_runs_the_configured_command_and_renames()
    {
        var project = _root.CreateDirectory("web");
        var outcome = await _executor.ExecuteAsync(new StartAgentCommand(project, "reclaude", "webby", null), CancellationToken.None);
        var started = Assert.IsType<AgentStartedData>(outcome.Data);
        Assert.Equal("webby", started.Agent.Name);
        Assert.Contains(_herdr.CallsStartingWith("pane", "run"), c => c[^1] == "reclaude");
        Assert.Contains(_herdr.CallsStartingWith("agent", "rename"), c => c[^1] == "webby");
        Assert.Empty(_herdr.CallsStartingWith("agent", "start"));
    }

    [Fact]
    public async Task Start_agent_blocked_on_trust_dialog_returns_screen()
    {
        _herdr.StartBlocked = true;
        _herdr.Screen = SampleScreens.TrustDialog;
        var outcome = await _executor.ExecuteAsync(new StartAgentCommand(_root.Path, null, "fresh", null), CancellationToken.None);
        var started = Assert.IsType<AgentStartedData>(outcome.Data);
        Assert.False(started.Ready);
        Assert.Contains("trust this folder", started.Screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initial_prompt_is_sent_when_the_user_approves_late()
    {
        // 回归：管家卡在"信任此目录"上，用户隔了很久才 /yes，初始说明仍然要发出去。
        _herdr.StartBlocked = true;
        _herdr.Screen = SampleScreens.TrustDialog;
        var outcome = await _executor.ExecuteAsync(new StartAgentCommand(_root.Path, null, "butler", "你是管家"), CancellationToken.None);
        Assert.False(Assert.IsType<AgentStartedData>(outcome.Data).Ready);
        await Task.Delay(300);
        Assert.Empty(_herdr.CallsStartingWith("agent", "prompt"));

        // 和真实情况一样：发 /yes 时它还卡在确认框上，按键之后才回到空闲。
        var butler = _herdr.Find("butler");
        Assert.NotNull(butler);
        Assert.True((await _executor.ExecuteAsync(new RespondCommand(butler.PaneId, true), CancellationToken.None)).Ok);
        butler.Status = "idle";

        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
        Assert.Single(_herdr.CallsStartingWith("agent", "prompt"), c => c[3] == "你是管家");
    }

    [Fact]
    public async Task Swallowed_initial_prompt_is_resent()
    {
        // 真实验收发现：刚确认完"信任此目录"时发的初始说明会被 Claude 吞掉，要重发。
        _herdr.SwallowPrompts = 1;
        var outcome = await _executor.ExecuteAsync(new StartAgentCommand(_root.Path, "claude", "butler", "你是管家"), CancellationToken.None);
        Assert.True(Assert.IsType<AgentStartedData>(outcome.Data).Ready);
        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
        Assert.Equal(2, _herdr.CallsStartingWith("agent", "prompt").Count(c => c[3] == "你是管家"));
    }

    [Fact]
    public async Task Dropped_user_prompt_is_resent_once()
    {
        // 真机测试发现：pi 刚启动时 herdr 已经报告空闲，但界面还在加载，紧接着发的指令被吞了，屏幕上找不到它。
        var agent = _herdr.AddAgent("pi", "demo", kind: "pi");
        _herdr.Screen = SampleScreens.PiReply;
        _herdr.SwallowPrompts = 1;
        Assert.True((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "列出当前目录的文件"), CancellationToken.None)).Ok);

        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
        Assert.Equal(2, _herdr.CallsStartingWith("agent", "prompt").Count);
    }

    [Theory]
    [InlineData("只回复两个字：收到。不要使用任何工具。", "回显在屏幕上，说明它收到了")]
    [InlineData("/compact", "斜杠命令执行完不一定有状态变化")]
    [InlineData("继续", "太短，判断不了在不在屏幕上")]
    public async Task Prompt_without_activity_is_not_resent_unless_surely_dropped(string prompt, string reason)
    {
        // 默认屏幕（SampleScreens.ClaudeReply）里回显着"只回复两个字：收到。不要使用任何工具。"
        var agent = _herdr.AddAgent("tideq", "TideQ");
        _herdr.SwallowPrompts = 1;
        Assert.True((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, prompt), CancellationToken.None)).Ok);

        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Failed, ev.Kind);
        Assert.Contains("没看到它开始工作", ev.Text, StringComparison.Ordinal);
        Assert.True(_herdr.CallsStartingWith("agent", "prompt").Count == 1, reason);
    }

    [Fact]
    public async Task Agent_that_wakes_up_before_the_resend_is_not_prompted_twice()
    {
        // 审查发现：判断"被吞"之后要等一会儿才重发，这期间 agent 可能把排队的输入吃进去开始干活了。
        var agent = _herdr.AddAgent("pi", "demo", kind: "pi");
        _herdr.Screen = SampleScreens.PiReply;
        _herdr.SwallowPrompts = 1;
        _herdr.AfterRead = a =>
        {
            _herdr.AfterRead = null;
            a.Status = "working";
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                a.Status = "idle";
            });
        };
        Assert.True((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "列出当前目录的文件"), CancellationToken.None)).Ok);

        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Completed, ev.Kind);
        Assert.Single(_herdr.CallsStartingWith("agent", "prompt"));
    }

    [Fact]
    public async Task Resent_prompt_that_is_dropped_again_is_reported_once()
    {
        var agent = _herdr.AddAgent("pi", "demo", kind: "pi");
        _herdr.Screen = SampleScreens.PiReply;
        _herdr.SwallowPrompts = 2;
        Assert.True((await _executor.ExecuteAsync(new PromptCommand(agent.PaneId, "列出当前目录的文件"), CancellationToken.None)).Ok);

        var ev = await _events.WaitAsync();
        Assert.Equal(AgentEventKind.Failed, ev.Kind);
        Assert.Contains("已自动重发一次", ev.Text, StringComparison.Ordinal);
        Assert.Equal(2, _herdr.CallsStartingWith("agent", "prompt").Count);
    }

    [Fact]
    public async Task Start_agent_refuses_paths_outside_whitelist_and_unknown_kinds()
    {
        // S-07 / S-10
        using var outside = new TempDirectory();
        var forbidden = await _executor.ExecuteAsync(new StartAgentCommand(outside.Path, "claude", null, null), CancellationToken.None);
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Error?.Code);

        var badKind = await _executor.ExecuteAsync(new StartAgentCommand(_root.Path, "bash", null, null), CancellationToken.None);
        Assert.Equal(ErrorCodes.InvalidArgument, badKind.Error?.Code);
        Assert.Empty(_herdr.CallsStartingWith("workspace", "create"));
    }

    [Fact]
    public async Task Start_agent_rejects_taken_name()
    {
        _herdr.AddAgent("web", "web");
        var outcome = await _executor.ExecuteAsync(new StartAgentCommand(_root.Path, "claude", "web", null), CancellationToken.None);
        Assert.Contains("已经被占用", outcome.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_keys_enforces_whitelist_and_count()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ");
        Assert.True((await _executor.ExecuteAsync(new SendKeysCommand(agent.PaneId, ["down", "enter"]), CancellationToken.None)).Ok);
        Assert.False((await _executor.ExecuteAsync(new SendKeysCommand(agent.PaneId, ["ctrl+d"]), CancellationToken.None)).Ok);
        Assert.False((await _executor.ExecuteAsync(new SendKeysCommand(agent.PaneId, []), CancellationToken.None)).Ok);
        Assert.False((await _executor.ExecuteAsync(new SendKeysCommand(agent.PaneId, Enumerable.Repeat("down", 21).ToList()), CancellationToken.None)).Ok);
        Assert.Single(_herdr.CallsStartingWith("agent", "send-keys"));
    }

    [Fact]
    public async Task Approve_navigates_trust_dialog_and_reject_presses_escape()
    {
        var agent = _herdr.AddAgent("tideq", "TideQ", "blocked");
        _herdr.Screen = SampleScreens.TrustDialog;
        var approve = await _executor.ExecuteAsync(new RespondCommand(agent.PaneId, true), CancellationToken.None);
        Assert.Contains("Yes, I trust this folder", Assert.IsType<AckData>(approve.Data).Message, StringComparison.Ordinal);
        Assert.Contains(_herdr.CallsStartingWith("agent", "send-keys"), c => c[^2] == "down" && c[^1] == "enter");

        var reject = await _executor.ExecuteAsync(new RespondCommand(agent.PaneId, false), CancellationToken.None);
        Assert.True(reject.Ok);
        Assert.Contains(_herdr.CallsStartingWith("agent", "send-keys"), c => c[^1] == "esc" && c.Length == 4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Yes_and_no_press_nothing_unless_the_agent_is_waiting_for_confirmation(bool approve)
    {
        // 审查发现：迟到的 /yes 会把回车按进输入框，迟到的 /no 会用 Esc 打断正在干的活。
        var agent = _herdr.AddAgent("tideq", "TideQ", "working");
        _herdr.Screen = SampleScreens.TrustDialog;
        var outcome = await _executor.ExecuteAsync(new RespondCommand(agent.PaneId, approve), CancellationToken.None);
        Assert.Contains("没在等确认", outcome.Error?.Message, StringComparison.Ordinal);
        Assert.Empty(_herdr.CallsStartingWith("agent", "send-keys"));
    }

    [Fact]
    public async Task Rename_validates_name()
    {
        var agent = _herdr.AddAgent(null, "x");
        Assert.False((await _executor.ExecuteAsync(new RenameAgentCommand(agent.PaneId, "Bad Name"), CancellationToken.None)).Ok);
        Assert.True((await _executor.ExecuteAsync(new RenameAgentCommand(agent.PaneId, "good"), CancellationToken.None)).Ok);
        Assert.Equal("good", agent.Name);
    }

    private sealed class RecordingSink : IAgentEventSink
    {
        private readonly BlockingCollection<AgentEvent> _queue = [];

        public ConcurrentQueue<AgentEvent> Received { get; } = new();

        public void Publish(AgentEvent agentEvent)
        {
            Received.Enqueue(agentEvent);
            _queue.Add(agentEvent);
        }

        public Task<AgentEvent> WaitAsync() =>
            Task.Run(() => _queue.TryTake(out var item, TimeSpan.FromSeconds(10)) ? item : throw new TimeoutException("没有收到事件"));
    }
}

/// <summary>节点配置校验和保存（S-06、S-14）。</summary>
public class NodeConfigTests
{
    private static NodeConfig Valid() => new()
    {
        ServerUrl = "https://example.com:8443",
        ServerFingerprint = "sha256:" + new string('a', 64),
        DeviceId = "d_1",
        DeviceName = "mac",
        Token = "token",
        AllowedRoots = ["/tmp"],
    };

    [Fact]
    public void Valid_config_passes() => Assert.Empty(Valid().Validate());

    [Fact]
    public void Https_without_fingerprint_is_rejected()
    {
        var config = Valid();
        config.ServerFingerprint = null;
        Assert.Contains(config.Validate(), e => e.Contains("指纹", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://example.com", false)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("ftp://example.com", false)]
    public void Plain_http_only_for_loopback(string url, bool ok)
    {
        var config = Valid();
        config.ServerUrl = url;
        config.ServerFingerprint = null;
        Assert.Equal(ok, config.Validate().Count == 0);
    }

    [Fact]
    public void Launcher_commands_cannot_contain_newlines_and_must_detect_a_known_kind()
    {
        var config = Valid();
        config.Launchers["evil"] = new AgentLauncher { Command = "reclaude\nrm -rf ~", DetectAs = "claude" };
        config.Launchers["odd"] = new AgentLauncher { Command = "x", DetectAs = "bash" };
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("evil", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("odd", StringComparison.Ordinal));
    }

    [Fact]
    public void Saved_config_round_trips_with_private_permissions()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("node.json");
        NodeConfigStore.Save(path, Valid());
        var loaded = NodeConfigStore.Load(path);
        Assert.Equal("mac", loaded.DeviceName);
        Assert.Equal("reclaude", loaded.Launchers["reclaude"].Command);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Theory]
    [InlineData("https://example.com", "wss://example.com/ws/node")]
    [InlineData("http://127.0.0.1:8080", "ws://127.0.0.1:8080/ws/node")]
    [InlineData("https://example.com/chatpilot/", "wss://example.com/chatpilot/ws/node")]
    public void WebSocket_uri_is_derived_from_server_url(string server, string expected) =>
        Assert.Equal(expected, ServerEndpoint.WebSocketUri(new Uri(server)).ToString());
}
