using ChatPilot.Node.Cli;
using ChatPilot.Node.Herdr;

namespace ChatPilot.Tests.Node;

/// <summary>
/// <c>chatpilot-node update</c> 之后重启开机自启服务：按传进来的系统走 Windows / macOS / Linux 的分支，
/// 系统命令全部由假的进程启动器回答，不碰这台电脑上真的计划任务、launchd 和 systemd，也不结束真的进程。
/// </summary>
public sealed class ServiceRestarterTests
{
    private const string WindowsExe = @"C:\Tools\chatpilot\node\chatpilot-node.exe";
    private const string WindowsBackup = @"C:\Tools\chatpilot\node\chatpilot-node.exe.old";

    /// <summary>install-node-task.ps1 注册的动作：cmd.exe /c ""程序" run >> "日志" 2>&amp;1"（路径大小写和这里不一样）。</summary>
    private static readonly string TaskActions =
        "execute=cmd.exe\narguments=/c \"\"" + WindowsExe.ToUpperInvariant() + "\" run >> \"C:\\Users\\me\\AppData\\Local\\ChatPilot\\node.log\" 2>&1\"\n";

    [Fact]
    public async Task Windows_restart_also_stops_the_node_left_behind_by_stop_scheduled_task()
    {
        // 审查 #1：Stop-ScheduledTask 只结束 cmd.exe，从改名后的 .old 运行的旧节点还在；要结束它（不结束自己、不结束别的命令和别的程序），再启动任务。
        var self = Environment.ProcessId;
        var runner = Windows(
            "state=Running\n" + TaskActions,
            processes: $"""
                {self}|{WindowsBackup}|"{WindowsExe}" run
                4242|{WindowsBackup}|"{WindowsExe}" run
                4343|{WindowsExe}|"{WindowsExe}" doctor
                4444|C:\Other\chatpilot-node.exe|C:\Other\chatpilot-node.exe run
                """);

        var result = await Restarter(runner, ServicePlatform.Windows).RestartAsync(WindowsExe, WindowsBackup, CancellationToken.None);

        Assert.Equal(RestartStatus.Restarted, result.Status);
        Assert.Equal(["Get-ScheduledTask", "Stop-ScheduledTask", "Get-CimInstance", "Stop-Process", "Start-ScheduledTask"], runner.Steps);
        // 只结束 4242：-Id 后面是逗号分开的进程号列表。
        Assert.Contains("Stop-Process -Id 4242 -Force", runner.Script("Stop-Process"), StringComparison.Ordinal);

        // 审查 #14：PowerShell 用 System32 下的绝对路径，不会运行当前目录里的同名程序；脚本里不能有双引号。
        Assert.All(runner.Calls, call =>
        {
            Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", call.File.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("powershell", call.File);
            Assert.DoesNotContain('"', call.Args[^1]);
        });
    }

    [Fact]
    public async Task Windows_task_the_user_stopped_is_not_started()
    {
        // 审查 #2：停掉节点就是关掉远程控制，升级不能替用户打开。
        var runner = Windows("state=Ready\n" + TaskActions);
        var result = await Restarter(runner, ServicePlatform.Windows).RestartAsync(WindowsExe, WindowsBackup, CancellationToken.None);

        Assert.Equal(RestartStatus.NotRunning, result.Status);
        Assert.Contains("Start-ScheduledTask -TaskName ChatPilotNode", result.Detail, StringComparison.Ordinal);
        Assert.Equal(["Get-ScheduledTask"], runner.Steps);
    }

    [Theory]
    [InlineData("")]
    [InlineData("state=Running\nexecute=cmd.exe\narguments=/c \"\"C:\\Tools\\chatpilot\\node\\chatpilot-node.exe.real\" run\"\n")]
    [InlineData("state=Running\nexecute=C:\\Tools\\chatpilot\\node\\chatpilot-node-dev.exe\narguments=run\n")]
    [InlineData("state=Running\nexecute=cmd.exe\narguments=/c chatpilot-node.exe run\n")]
    public async Task Windows_task_that_is_missing_or_runs_another_program_is_left_alone(string query)
    {
        // 审查 #7：只是包含这个路径（.real、-dev）或者是相对路径都不算。
        var runner = Windows(query);
        var result = await Restarter(runner, ServicePlatform.Windows).RestartAsync(WindowsExe, null, CancellationToken.None);

        Assert.Equal(RestartStatus.NotFound, result.Status);
        Assert.Null(result.Detail);
        Assert.Equal(["Get-ScheduledTask"], runner.Steps);
    }

    [Fact]
    public async Task Windows_task_path_with_spaces_is_recognised()
    {
        const string exe = @"C:\Program Files\ChatPilot\chatpilot-node.exe";
        var runner = Windows($"state=Running\nexecute=cmd.exe\narguments=/c \"\"{exe}\" run >> \"C:\\log\" 2>&1\"\n");
        var result = await Restarter(runner, ServicePlatform.Windows).RestartAsync(exe, null, CancellationToken.None);
        Assert.Equal(RestartStatus.Restarted, result.Status);
    }

    [Fact]
    public async Task Windows_start_failure_is_reported_as_failure()
    {
        // 审查 #3：任务停掉了、却启动不了时，不能说成"没找到服务"。
        var runner = Windows("state=Running\n" + TaskActions, startFails: true);
        var result = await Restarter(runner, ServicePlatform.Windows).RestartAsync(WindowsExe, WindowsBackup, CancellationToken.None);

        Assert.Equal(RestartStatus.Failed, result.Status);
        Assert.Contains("拒绝访问", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_leftover_node_that_cannot_be_stopped_is_reported_and_the_task_still_starts()
    {
        var runner = Windows(
            "state=Running\n" + TaskActions,
            processes: $"4242|{WindowsBackup}|\"{WindowsExe}\" run",
            killFails: true);
        var result = await Restarter(runner, ServicePlatform.Windows).RestartAsync(WindowsExe, WindowsBackup, CancellationToken.None);

        Assert.Equal(RestartStatus.Failed, result.Status);
        Assert.Contains("4242", result.Detail, StringComparison.Ordinal);
        Assert.Contains("Start-ScheduledTask", runner.Steps);
    }

    [Fact]
    public async Task Windows_query_that_cannot_run_says_why()
    {
        var runner = new FakeRunner((_, _) => throw new ProcessStartException("无法启动 powershell.exe：找不到文件", new InvalidOperationException()));
        var result = await Restarter(runner, ServicePlatform.Windows).RestartAsync(WindowsExe, null, CancellationToken.None);

        Assert.Equal(RestartStatus.NotFound, result.Status);
        Assert.Contains("找不到文件", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"C:\\a b\\chatpilot-node.exe\" run", "run")]
    [InlineData("C:\\x\\chatpilot-node.exe  run --config \"C:\\a b\\node.json\"", "run")]
    [InlineData("\"C:\\x\\chatpilot-node.exe\" \"run\"", "run")]
    [InlineData("\"C:\\x\\chatpilot-node.exe\" update", "update")]
    [InlineData("\"C:\\x\\chatpilot-node.exe\"", null)]
    [InlineData("C:\\x\\chatpilot-node.exe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void First_argument_of_a_windows_command_line(string? commandLine, string? expected) =>
        Assert.Equal(expected, ServiceRestarter.FirstArgument(commandLine));

    [Fact]
    public async Task MacOS_restart_under_sudo_targets_the_invoking_users_gui_domain()
    {
        var runner = MacOS(Launchd("running", "/usr/local/bin/chatpilot-node"));
        var result = await Restarter(runner, ServicePlatform.MacOS, sudoUid: "501").RestartAsync("/usr/local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.Restarted, result.Status);
        Assert.Equal(
            new[] { "/bin/launchctl print gui/501/com.chatpilot.node", "/bin/launchctl kickstart -k gui/501/com.chatpilot.node" },
            runner.Calls.Select(c => c.File + " " + string.Join(' ', c.Args)));
    }

    [Fact]
    public async Task MacOS_restart_without_sudo_asks_id_for_the_uid()
    {
        var runner = MacOS(Launchd("running", "/usr/local/bin/chatpilot-node"), uid: "502\n");
        var result = await Restarter(runner, ServicePlatform.MacOS).RestartAsync("/usr/local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.Restarted, result.Status);
        Assert.Equal("/usr/bin/id", runner.Calls[0].File);
        Assert.Equal(["kickstart", "-k", "gui/502/com.chatpilot.node"], runner.Calls[^1].Args);
    }

    [Fact]
    public async Task MacOS_service_the_user_stopped_is_not_kickstarted()
    {
        // 审查 #2：kickstart 会把没在运行的服务打开。
        var runner = MacOS(Launchd("not running", "/usr/local/bin/chatpilot-node"));
        var result = await Restarter(runner, ServicePlatform.MacOS, sudoUid: "501").RestartAsync("/usr/local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.NotRunning, result.Status);
        Assert.Contains("launchctl kickstart gui/501/com.chatpilot.node", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, c => c.Args[0] == "kickstart");
    }

    [Fact]
    public async Task MacOS_service_is_matched_by_exact_path_after_resolving_symlinks()
    {
        // 审查 #7：服务里写的是指向这个程序的符号链接时也认；只是路径前缀相同（-dev）不认。
        var links = new Dictionary<string, string>(StringComparer.Ordinal) { ["/Users/me/.local/bin/chatpilot-node"] = "/opt/chatpilot/chatpilot-node" };
        var linked = MacOS(Launchd("running", "/Users/me/.local/bin/chatpilot-node"));
        var result = await Restarter(linked, ServicePlatform.MacOS, sudoUid: "501", links: links).RestartAsync("/opt/chatpilot/chatpilot-node", null, CancellationToken.None);
        Assert.Equal(RestartStatus.Restarted, result.Status);

        var other = MacOS(Launchd("running", "/usr/local/bin/chatpilot-node-dev"));
        var unrelated = await Restarter(other, ServicePlatform.MacOS, sudoUid: "501").RestartAsync("/usr/local/bin/chatpilot-node", null, CancellationToken.None);
        Assert.Equal(RestartStatus.NotFound, unrelated.Status);
        Assert.DoesNotContain(other.Calls, c => c.Args[0] == "kickstart");
    }

    [Fact]
    public async Task Linux_running_service_is_restarted_with_try_restart()
    {
        // 审查 #2：用 try-restart，刚好被停掉的服务不会被打开。审查 #14：systemctl 用绝对路径。
        var runner = Linux(Systemd("active", "/home/me/.local/bin/chatpilot-node"));
        var result = await Restarter(runner, ServicePlatform.Linux).RestartAsync("/home/me/.local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.Restarted, result.Status);
        Assert.All(runner.Calls, call => Assert.Matches("^(/usr)?/bin/systemctl$", call.File));
        Assert.Equal(["--user", "try-restart", "chatpilot-node"], runner.Calls[^1].Args);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("failed")]
    public async Task Linux_service_the_user_stopped_is_not_started(string state)
    {
        var runner = Linux(Systemd(state, "/home/me/.local/bin/chatpilot-node"));
        var result = await Restarter(runner, ServicePlatform.Linux).RestartAsync("/home/me/.local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.NotRunning, result.Status);
        Assert.Contains("systemctl --user start chatpilot-node", result.Detail, StringComparison.Ordinal);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("/usr/local/bin/chatpilot-node-dev")]
    [InlineData("/usr/local/bin/chatpilot-node.real")]
    [InlineData("/usr/local/bin/chatpilot-node/run")]
    public async Task Linux_service_running_a_similarly_named_program_is_left_alone(string execStart)
    {
        // 审查 #7：以前按子串匹配，这些都会被当成"运行的正是这个程序"。
        var runner = Linux(Systemd("active", execStart));
        var result = await Restarter(runner, ServicePlatform.Linux).RestartAsync("/usr/local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.NotFound, result.Status);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Linux_missing_unit_is_not_found()
    {
        var runner = Linux("ExecStart=\nLoadState=not-found\nActiveState=inactive\n");
        var result = await Restarter(runner, ServicePlatform.Linux).RestartAsync("/home/me/.local/bin/chatpilot-node", null, CancellationToken.None);
        Assert.Equal(RestartStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Linux_restart_under_sudo_talks_to_the_invoking_users_systemd()
    {
        // 审查 #4：sudo 下 systemctl --user 连的是 root 自己的 systemd，看不到用户的服务。
        var runner = Linux(Systemd("active", "/usr/local/bin/chatpilot-node"));
        var restarter = Restarter(runner, ServicePlatform.Linux, sudoUid: "1000");
        var result = await restarter.RestartAsync("/usr/local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.Restarted, result.Status);
        Assert.All(runner.Calls, call =>
        {
            Assert.Equal("/usr/bin/sudo", call.File);
            Assert.Equal(["-n", "-u", "#1000", "/usr/bin/env", "XDG_RUNTIME_DIR=/run/user/1000"], call.Args[..5]);
            Assert.Matches("^(/usr)?/bin/systemctl$", call.Args[5]);
            Assert.Equal("--user", call.Args[6]);
        });
        Assert.Contains("不要加 sudo", restarter.ManualHint(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_systemd_that_cannot_be_reached_says_why()
    {
        var runner = new FakeRunner((_, _) => new ProcessResult(1, "", "Failed to connect to bus: No medium found"));
        var result = await Restarter(runner, ServicePlatform.Linux).RestartAsync("/home/me/.local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.NotFound, result.Status);
        Assert.Contains("Failed to connect to bus", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_failed_restart_is_a_failure()
    {
        var runner = new FakeRunner((_, args) => args.Contains("show")
            ? new ProcessResult(0, Systemd("active", "/home/me/.local/bin/chatpilot-node"), "")
            : new ProcessResult(1, "", "Job for chatpilot-node.service failed"));
        var result = await Restarter(runner, ServicePlatform.Linux).RestartAsync("/home/me/.local/bin/chatpilot-node", null, CancellationToken.None);

        Assert.Equal(RestartStatus.Failed, result.Status);
        Assert.Contains("Job for chatpilot-node.service failed", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_systems_have_no_service()
    {
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("不该运行任何命令"));
        var result = await Restarter(runner, ServicePlatform.Other).RestartAsync("/opt/chatpilot-node", null, CancellationToken.None);
        Assert.Equal(RestartStatus.NotFound, result.Status);
    }

    [Fact]
    public void Windows_manual_hint_also_stops_the_leftover_node()
    {
        var hint = Restarter(new FakeRunner((_, _) => new ProcessResult(0, "", "")), ServicePlatform.Windows).ManualHint();
        Assert.Contains("Stop-Process", hint, StringComparison.Ordinal);
        Assert.Contains("Start-ScheduledTask -TaskName ChatPilotNode", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RestartStatus.Restarted, 0, "已重启计划任务 ChatPilotNode")]
    [InlineData(RestartStatus.NotRunning, 0, "没有替你启动")]
    [InlineData(RestartStatus.NotFound, 0, "没找到运行这个程序的开机自启服务")]
    [InlineData(RestartStatus.Failed, 1, "重启计划任务 ChatPilotNode 失败了")]
    public void Update_reports_restart_failure_with_a_non_zero_exit_code(RestartStatus status, int exitCode, string message)
    {
        // 审查 #3：重启失败时节点可能停着，update 要以非 0 退出，并且和"没找到服务"说法不同。
        var (exit, text) = NodeCli.DescribeRestart(new RestartResult(status, "计划任务 ChatPilotNode", "原因"), "请重启节点");
        Assert.Equal(exitCode, exit);
        Assert.Contains(message, text, StringComparison.Ordinal);
    }

    private static ServiceRestarter Restarter(IProcessRunner runner, ServicePlatform platform, string? sudoUid = null, Dictionary<string, string>? links = null) =>
        new(
            runner,
            platform,
            name => name == "SUDO_UID" ? sudoUid : null,
            path => links is not null && links.TryGetValue(path, out var target) ? target : path);

    /// <summary>Windows：按脚本里的命令回答查询、停止、列进程、结束进程、启动。</summary>
    private static FakeRunner Windows(string query, string processes = "", bool startFails = false, bool killFails = false) =>
        new((_, args) => args[^1] switch
        {
            var s when s.Contains("Get-ScheduledTask", StringComparison.Ordinal) => new ProcessResult(0, query, ""),
            var s when s.Contains("Get-CimInstance", StringComparison.Ordinal) => new ProcessResult(0, processes, ""),
            var s when s.Contains("Stop-Process", StringComparison.Ordinal) => new ProcessResult(killFails ? 1 : 0, "", ""),
            var s when s.Contains("Start-ScheduledTask", StringComparison.Ordinal) && startFails => new ProcessResult(1, "", "Start-ScheduledTask : 拒绝访问。"),
            _ => new ProcessResult(0, "", ""),
        });

    /// <summary>macOS：<c>id -u</c> 回答 <paramref name="uid"/>，<c>launchctl print</c> 回答 <paramref name="print"/>，其余成功。</summary>
    private static FakeRunner MacOS(string print, string uid = "501\n") =>
        new((file, args) => file == "/usr/bin/id" ? new ProcessResult(0, uid, "")
            : args[0] == "print" ? new ProcessResult(0, print, "")
            : new ProcessResult(0, "", ""));

    /// <summary>Linux：<c>systemctl show</c> 回答 <paramref name="show"/>，其余成功。</summary>
    private static FakeRunner Linux(string show) =>
        new((_, args) => args.Contains("show") ? new ProcessResult(0, show, "") : new ProcessResult(0, "", ""));

    /// <summary><c>launchctl print</c> 的样子（节选）：README 的 plist 用 caffeinate 包着节点。</summary>
    private static string Launchd(string state, string program) => $$"""
        gui/501/com.chatpilot.node = {
        	active count = 1
        	path = /Users/me/Library/LaunchAgents/com.chatpilot.node.plist
        	type = LaunchAgent
        	state = {{state}}

        	program = /usr/bin/caffeinate
        	arguments = {
        		/usr/bin/caffeinate
        		-i
        		{{program}}
        		run
        	}

        	pid = 812
        }
        """;

    /// <summary><c>systemctl --user show chatpilot-node --property=…</c> 的样子。</summary>
    private static string Systemd(string activeState, string program) =>
        $"ExecStart={{ path={program} ; argv[]={program} run ; ignore_errors=no ; start_time=[n/a] ; stop_time=[n/a] ; pid=0 ; code=(null) ; status=0/0 }}\n"
        + $"LoadState=loaded\nActiveState={activeState}\n";

    /// <summary>按调用回答的假进程启动器，记下每次调用。</summary>
    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        private static readonly string[] Commands = ["Get-ScheduledTask", "Stop-ScheduledTask", "Get-CimInstance", "Stop-Process", "Start-ScheduledTask"];

        public List<(string File, string[] Args)> Calls { get; } = [];

        /// <summary>Windows 上依次运行了哪些 PowerShell 命令。</summary>
        public List<string> Steps => Calls.Select(c => Commands.First(command => c.Args[^1].Contains(command, StringComparison.Ordinal))).ToList();

        public string Script(string command) => Calls.Select(c => c.Args[^1]).Single(s => s.Contains(command, StringComparison.Ordinal));

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(respond(fileName, arguments));
        }
    }
}
