using System.Globalization;
using System.Text.RegularExpressions;
using ChatPilot.Node.Execution;
using ChatPilot.Node.Herdr;

namespace ChatPilot.Node.Cli;

/// <summary>开机自启服务所在的系统，决定用哪种服务管理器。</summary>
public enum ServicePlatform
{
    /// <summary>Windows：计划任务。</summary>
    Windows,

    /// <summary>macOS：launchd。</summary>
    MacOS,

    /// <summary>Linux：用户级 systemd。</summary>
    Linux,

    /// <summary>其他系统：没有能重启的服务。</summary>
    Other,
}

/// <summary>重启开机自启服务的结果。</summary>
public enum RestartStatus
{
    /// <summary>没找到运行这个程序的开机自启服务（或者查不到），什么都没动。</summary>
    NotFound,

    /// <summary>服务运行的正是这个程序，但它现在没在运行（多半是用户自己停掉的），没有替用户启动。</summary>
    NotRunning,

    /// <summary>已经重启，新版本在运行。</summary>
    Restarted,

    /// <summary>重启时出错：节点可能停着，也可能还是旧版本在运行。</summary>
    Failed,
}

/// <summary>重启开机自启服务的结果。</summary>
/// <param name="Status">结果。</param>
/// <param name="Service">服务的说明，例如 <c>计划任务 ChatPilotNode</c>。</param>
/// <param name="Detail">
/// 补充说明：<see cref="RestartStatus.Failed"/> 时是出错原因，<see cref="RestartStatus.NotRunning"/> 时是启动它的命令，
/// <see cref="RestartStatus.NotFound"/> 时是查询出错的原因（正常查完没找到时为 null）。
/// </param>
public sealed record RestartResult(RestartStatus Status, string Service, string? Detail = null);

/// <summary>
/// 升级后重启按 README 设置的开机自启服务：Windows 计划任务 <c>ChatPilotNode</c>、macOS launchd <c>com.chatpilot.node</c>、
/// Linux 用户级 systemd 服务 <c>chatpilot-node</c>。只重启运行的正是这个程序、而且正在运行的服务，别的不碰。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>"运行的是这个程序"：服务的启动命令里要有一个参数和这个程序的路径完全相同（两边都先解析符号链接），只是包含这个路径不算。</item>
/// <item>只重启正在运行的服务：用户自己停掉的（停掉节点就是关掉远程控制）不会被重新打开。</item>
/// <item>
/// Windows 的 <c>Stop-ScheduledTask</c> 只结束计划任务启动的 <c>cmd.exe</c>，节点进程会留下来继续运行。
/// 所以停掉任务后，还要结束从这个程序（或者升级时改名成的 <c>.old</c>）启动的 <c>run</c> 进程，等它们退出后再启动任务。
/// 新节点启动时也会等旧节点放开单实例锁（见 <see cref="NodeCli.AcquireInstanceLockAsync"/>），不会两个一起运行。
/// </item>
/// <item>系统命令都用绝对路径，不会运行当前目录或程序目录里的同名程序。</item>
/// <item>用 sudo 升级时（程序在 <c>/usr/local/bin</c> 这类目录），重启的是原来那个用户的服务：macOS 的 gui 域、Linux 的用户级 systemd。</item>
/// </list>
/// </remarks>
/// <param name="runner">运行系统命令（测试里换成假的）。</param>
/// <param name="platform">按哪个系统的服务管理器处理。</param>
/// <param name="environment">读环境变量，用来认出 sudo（<c>SUDO_UID</c>）。</param>
/// <param name="realPath">把路径解析成真实路径（展开符号链接）；比较服务里的路径和这个程序时两边都用它。</param>
public sealed partial class ServiceRestarter(
    IProcessRunner runner,
    ServicePlatform platform,
    Func<string, string?> environment,
    Func<string, string> realPath)
{
    /// <summary>README 里 Windows 计划任务的名字。</summary>
    private const string WindowsTask = "ChatPilotNode";

    /// <summary>README 里 macOS launchd 服务的标签。</summary>
    private const string LaunchdLabel = "com.chatpilot.node";

    /// <summary>README 里 Linux systemd 用户服务的名字。</summary>
    private const string SystemdUnit = "chatpilot-node";

    /// <summary>macOS 的 launchctl。</summary>
    private const string Launchctl = "/bin/launchctl";

    /// <summary>macOS 上查当前用户 uid 用的 id。</summary>
    private const string Id = "/usr/bin/id";

    /// <summary>Linux 上用 sudo 升级时，用它切回原来的用户去操作那个用户的 systemd。</summary>
    private const string Sudo = "/usr/bin/sudo";

    /// <summary>切回原来的用户时用它设置环境变量（sudo 会清掉大部分环境变量）。</summary>
    private const string Env = "/usr/bin/env";

    /// <summary>每个系统命令最多运行多久。</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>出错原因最多显示多少个字符。</summary>
    private const int MaxDetailChars = 300;

    /// <summary>用这台电脑的真实环境：真的运行系统命令、当前操作系统、真的环境变量、真的解析符号链接。</summary>
    /// <returns>给 <c>chatpilot-node update</c> 用的重启器。</returns>
    public static ServiceRestarter ForThisComputer() =>
        new(new ProcessRunner(), CurrentPlatform(), Environment.GetEnvironmentVariable, RealPath);

    /// <summary>当前操作系统对应的服务管理器。</summary>
    /// <returns>Windows、macOS、Linux 之一；其他系统为 <see cref="ServicePlatform.Other"/>。</returns>
    public static ServicePlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? ServicePlatform.Windows
        : OperatingSystem.IsMacOS() ? ServicePlatform.MacOS
        : OperatingSystem.IsLinux() ? ServicePlatform.Linux
        : ServicePlatform.Other;

    /// <summary>
    /// 重启运行着这个程序、而且正在运行的服务。Windows 上会结束停掉任务后留下来的旧节点进程，等它退出后再启动任务。
    /// </summary>
    /// <param name="executablePath">刚替换掉的程序；服务的启动命令里必须有一个参数正是这个路径才会重启。</param>
    /// <param name="replacedPath">Windows 上旧程序改名后的路径（还在运行的旧节点用的就是它），见 <see cref="NodeUpdater.InstallAsync"/>；没有时为 null。</param>
    /// <param name="cancellationToken">取消查询和重启。</param>
    /// <returns>重启的结果：没找到服务、服务没在运行、已重启或重启失败。</returns>
    public Task<RestartResult> RestartAsync(string executablePath, string? replacedPath, CancellationToken cancellationToken) =>
        platform switch
        {
            ServicePlatform.Windows => RestartScheduledTaskAsync(executablePath, replacedPath, cancellationToken),
            ServicePlatform.MacOS => RestartLaunchdAsync(executablePath, cancellationToken),
            ServicePlatform.Linux => RestartSystemdAsync(executablePath, cancellationToken),
            _ => Task.FromResult(new RestartResult(RestartStatus.NotFound, "开机自启服务")),
        };

    /// <summary>没重启时，告诉用户怎么自己重启。</summary>
    /// <returns>按系统给出的重启命令；用 sudo 升级时写明要用自己的账号（或者那个用户的 uid）。</returns>
    public string ManualHint() =>
        platform switch
        {
            // Stop-ScheduledTask 只结束外面的 cmd.exe，节点进程要单独结束，否则旧版本会一直运行。
            ServicePlatform.Windows =>
                $"请重启节点：Stop-ScheduledTask -TaskName {WindowsTask}; Get-Process chatpilot-node -ErrorAction SilentlyContinue | Stop-Process -Force; "
                + $"Start-ScheduledTask -TaskName {WindowsTask}（或者重新运行 chatpilot-node run）",
            ServicePlatform.MacOS => $"请重启节点：launchctl kickstart -k gui/{SudoUid ?? "$(id -u)"}/{LaunchdLabel}（或者重新运行 chatpilot-node run）",
            ServicePlatform.Linux => SudoUid is null
                ? $"请重启节点：systemctl --user restart {SystemdUnit}（或者重新运行 chatpilot-node run）"
                : $"请用你自己的账号（不要加 sudo）运行：systemctl --user restart {SystemdUnit}",
            _ => "请重启节点（重新运行 chatpilot-node run）",
        };

    /// <summary>用 sudo 运行时原来那个用户的 uid（<c>SUDO_UID</c>）。</summary>
    /// <value>纯数字、不是 0 的 uid；没用 sudo、原来就是 root 或者格式不对时为 null。</value>
    private string? SudoUid => environment("SUDO_UID") is { } uid && IsUid(uid) && uid != "0" ? uid : null;

    /// <summary>Windows PowerShell 的绝对路径：<c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c>。</summary>
    private static string PowerShell =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System) is { Length: > 0 } system ? system : @"C:\Windows\System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    /// <summary>systemctl 的绝对路径：一般在 <c>/usr/bin</c>，没合并 <c>/usr</c> 的老系统在 <c>/bin</c>。</summary>
    private static string Systemctl =>
        File.Exists("/usr/bin/systemctl") || !File.Exists("/bin/systemctl") ? "/usr/bin/systemctl" : "/bin/systemctl";

    /// <summary>
    /// Windows：查计划任务的状态和启动命令；运行的是这个程序并且在运行时，停掉任务、结束留下来的旧节点进程、再启动任务。
    /// </summary>
    /// <param name="executablePath">刚替换掉的程序。</param>
    /// <param name="replacedPath">旧程序改名后的路径；没有时为 null。</param>
    /// <param name="cancellationToken">取消查询和重启。</param>
    /// <returns>重启的结果。</returns>
    private async Task<RestartResult> RestartScheduledTaskAsync(string executablePath, string? replacedPath, CancellationToken cancellationToken)
    {
        const string service = $"计划任务 {WindowsTask}";
        var query = await PowerShellAsync(
            $"$task = Get-ScheduledTask -TaskName '{WindowsTask}' -ErrorAction SilentlyContinue; if ($null -eq $task) {{ exit 0 }}; "
            + "'state=' + $task.State; $task.Actions | ForEach-Object { 'execute=' + $_.Execute; 'arguments=' + $_.Arguments }",
            cancellationToken).ConfigureAwait(false);
        if (query.ExitCode != 0)
        {
            return new RestartResult(RestartStatus.NotFound, service, "查询计划任务失败：" + Describe(query));
        }

        var lines = Lines(query.StandardOutput);
        var state = lines.FirstOrDefault(l => l.StartsWith("state=", StringComparison.Ordinal))?["state=".Length..];
        var words = lines
            .Where(l => l.StartsWith("execute=", StringComparison.Ordinal) || l.StartsWith("arguments=", StringComparison.Ordinal))
            .SelectMany(l => CommandWords(l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..]));
        if (state is null || !RunsExecutable(words, executablePath))
        {
            return new RestartResult(RestartStatus.NotFound, service);
        }

        if (state != "Running")
        {
            return new RestartResult(RestartStatus.NotRunning, service, $"Start-ScheduledTask -TaskName {WindowsTask}");
        }

        var stop = await PowerShellAsync($"Stop-ScheduledTask -TaskName '{WindowsTask}' -ErrorAction Stop", cancellationToken).ConfigureAwait(false);
        if (stop.ExitCode != 0)
        {
            return new RestartResult(RestartStatus.Failed, service, "停止计划任务失败：" + Describe(stop));
        }

        // 结束不了旧进程也照样启动任务：新节点会等旧节点放开锁，旧进程稍后退出的话它就能接手。
        var leftover = await StopLeftoverNodesAsync(executablePath, replacedPath, cancellationToken).ConfigureAwait(false);
        var start = await PowerShellAsync($"Start-ScheduledTask -TaskName '{WindowsTask}' -ErrorAction Stop", cancellationToken).ConfigureAwait(false);
        if (start.ExitCode != 0)
        {
            return new RestartResult(RestartStatus.Failed, service, "启动计划任务失败：" + Describe(start) + (leftover is null ? "" : "；" + leftover));
        }

        return leftover is null
            ? new RestartResult(RestartStatus.Restarted, service)
            : new RestartResult(RestartStatus.Failed, service, leftover);
    }

    /// <summary>
    /// Windows：结束停掉计划任务后留下来的旧节点进程（从这个程序或改名后的 <c>.old</c> 启动、命令是 <c>run</c>，不含自己），
    /// 最多等 15 秒让它们退出。
    /// </summary>
    /// <param name="executablePath">刚替换掉的程序。</param>
    /// <param name="replacedPath">旧程序改名后的路径；没有时为 null。</param>
    /// <param name="cancellationToken">取消查询和结束进程。</param>
    /// <returns>出错时给用户看的原因；都结束了（或者本来就没有）时为 null。</returns>
    private async Task<string?> StopLeftoverNodesAsync(string executablePath, string? replacedPath, CancellationToken cancellationToken)
    {
        var filter = WindowsFileName(executablePath) is var name && SafeProcessName().IsMatch(name)
            ? $" -Filter 'Name LIKE ''{name[..^".exe".Length]}%'''"
            : "";
        var list = await PowerShellAsync(
            $"Get-CimInstance Win32_Process{filter} | Where-Object {{ $_.ExecutablePath }} | "
            + "ForEach-Object { [string]$_.ProcessId + '|' + $_.ExecutablePath + '|' + $_.CommandLine }",
            cancellationToken).ConfigureAwait(false);
        if (list.ExitCode != 0)
        {
            return "查不到还在运行的旧节点进程：" + Describe(list);
        }

        var ids = FindLeftoverNodes(list.StandardOutput, executablePath, replacedPath, Environment.ProcessId);
        if (ids.Count == 0)
        {
            return null;
        }

        var idList = string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var kill = await PowerShellAsync(
            $"Stop-Process -Id {idList} -Force -ErrorAction SilentlyContinue; Wait-Process -Id {idList} -Timeout 15 -ErrorAction SilentlyContinue; "
            + $"if (Get-Process -Id {idList} -ErrorAction SilentlyContinue) {{ exit 1 }}",
            cancellationToken).ConfigureAwait(false);
        return kill.ExitCode == 0
            ? null
            : $"旧节点进程（PID {string.Join("、", ids)}）结束不了，新版本可能接不上：请在任务管理器里结束 chatpilot-node，再运行 Start-ScheduledTask -TaskName {WindowsTask}";
    }

    /// <summary>从进程列表里挑出要结束的旧节点：从这个程序或改名后的旧程序启动、第一个参数是 <c>run</c>、不是自己。</summary>
    /// <param name="listing">每行"进程号|程序路径|命令行"。</param>
    /// <param name="executablePath">刚替换掉的程序。</param>
    /// <param name="replacedPath">旧程序改名后的路径；没有时为 null。</param>
    /// <param name="selfId">自己的进程号（<c>update</c> 进程本身也是从旧程序启动的）。</param>
    /// <returns>要结束的进程号；没有时为空列表。</returns>
    internal IReadOnlyList<int> FindLeftoverNodes(string listing, string executablePath, string? replacedPath, int selfId)
    {
        var programs = new[] { executablePath, replacedPath }.OfType<string>().Select(realPath).ToList();
        var ids = new List<int>();
        foreach (var line in Lines(listing))
        {
            var parts = line.Split('|', 3);
            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && id != selfId
                && programs.Contains(realPath(parts[1]), PathComparer)
                && FirstArgument(parts[2]) == "run")
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>macOS：查 launchd 服务的状态和启动命令；运行的是这个程序并且在运行时，用 <c>kickstart -k</c> 重启。</summary>
    /// <param name="executablePath">刚替换掉的程序。</param>
    /// <param name="cancellationToken">取消查询和重启。</param>
    /// <returns>重启的结果。</returns>
    private async Task<RestartResult> RestartLaunchdAsync(string executablePath, CancellationToken cancellationToken)
    {
        const string service = $"launchd 服务 {LaunchdLabel}";
        var uid = SudoUid;
        if (uid is null)
        {
            var id = await RunAsync(Id, ["-u"], cancellationToken).ConfigureAwait(false);
            uid = id.StandardOutput.Trim();
            if (id.ExitCode != 0 || !IsUid(uid))
            {
                return new RestartResult(RestartStatus.NotFound, service, "查不到当前用户的 uid：" + Describe(id));
            }
        }

        var target = $"gui/{uid}/{LaunchdLabel}";
        var print = await RunAsync(Launchctl, ["print", target], cancellationToken).ConfigureAwait(false);
        if (print.ExitCode != 0)
        {
            // 没有这个服务时 launchctl print 返回 113。
            return new RestartResult(RestartStatus.NotFound, service);
        }

        var lines = Lines(print.StandardOutput);
        if (!RunsExecutable(LaunchdCommand(lines), executablePath))
        {
            return new RestartResult(RestartStatus.NotFound, service);
        }

        if (lines.FirstOrDefault(l => l.StartsWith("state = ", StringComparison.Ordinal)) != "state = running")
        {
            return new RestartResult(RestartStatus.NotRunning, service, $"launchctl kickstart {target}");
        }

        var kickstart = await RunAsync(Launchctl, ["kickstart", "-k", target], cancellationToken).ConfigureAwait(false);
        return kickstart.ExitCode == 0
            ? new RestartResult(RestartStatus.Restarted, service)
            : new RestartResult(RestartStatus.Failed, service, "launchctl kickstart 失败：" + Describe(kickstart));
    }

    /// <summary>
    /// Linux：查 systemd 用户服务的状态和启动命令；运行的是这个程序并且在运行时，用 <c>try-restart</c> 重启
    /// （服务刚好被停掉时它什么都不做，不会把服务打开）。
    /// </summary>
    /// <param name="executablePath">刚替换掉的程序。</param>
    /// <param name="cancellationToken">取消查询和重启。</param>
    /// <returns>重启的结果。</returns>
    private async Task<RestartResult> RestartSystemdAsync(string executablePath, CancellationToken cancellationToken)
    {
        const string service = $"systemd 用户服务 {SystemdUnit}";
        var show = await SystemctlAsync(
            ["show", SystemdUnit, "--property=LoadState", "--property=ActiveState", "--property=ExecStart"],
            cancellationToken).ConfigureAwait(false);
        if (show.ExitCode != 0)
        {
            return new RestartResult(RestartStatus.NotFound, service, "查询失败：" + Describe(show));
        }

        var lines = Lines(show.StandardOutput);
        if (Property(lines, "LoadState") != "loaded" || !RunsExecutable(SystemdCommand(lines), executablePath))
        {
            return new RestartResult(RestartStatus.NotFound, service);
        }

        if (Property(lines, "ActiveState") is not ("active" or "activating" or "reloading"))
        {
            return new RestartResult(
                RestartStatus.NotRunning,
                service,
                SudoUid is null ? $"systemctl --user start {SystemdUnit}" : $"systemctl --user start {SystemdUnit}（用你自己的账号，不要加 sudo）");
        }

        var restart = await SystemctlAsync(["try-restart", SystemdUnit], cancellationToken).ConfigureAwait(false);
        return restart.ExitCode == 0
            ? new RestartResult(RestartStatus.Restarted, service)
            : new RestartResult(RestartStatus.Failed, service, "systemctl --user try-restart 失败：" + Describe(restart));
    }

    /// <summary>
    /// 对用户级 systemd 运行 systemctl。用 sudo 升级时，切回原来的用户（<c>sudo -n -u #uid</c>）并指向那个用户的
    /// <c>XDG_RUNTIME_DIR</c>，否则 <c>--user</c> 连的是 root 自己的 systemd，看不到用户的服务。
    /// </summary>
    /// <param name="arguments"><c>--user</c> 后面的参数。</param>
    /// <param name="cancellationToken">取消运行。</param>
    /// <returns>命令的结果；启动不了或超时时退出码为 -1。</returns>
    private Task<ProcessResult> SystemctlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var systemctl = Systemctl;
        return SudoUid is { } uid
            ? RunAsync(Sudo, ["-n", "-u", "#" + uid, Env, $"XDG_RUNTIME_DIR=/run/user/{uid}", systemctl, "--user", .. arguments], cancellationToken)
            : RunAsync(systemctl, ["--user", .. arguments], cancellationToken);
    }

    /// <summary>用绝对路径的 Windows PowerShell 运行一段脚本，输出 UTF-8（路径里可能有中文）。</summary>
    /// <param name="script">脚本；只用单引号字符串，不能有双引号（PowerShell 5.1 解析命令行参数里的双引号有问题）。</param>
    /// <param name="cancellationToken">取消运行。</param>
    /// <returns>脚本的结果；启动不了或超时时退出码为 -1。</returns>
    private Task<ProcessResult> PowerShellAsync(string script, CancellationToken cancellationToken) =>
        RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + script], cancellationToken);

    /// <summary>运行一个系统命令，最多 <see cref="CommandTimeout"/>。</summary>
    /// <param name="fileName">命令的绝对路径。</param>
    /// <param name="arguments">参数，逐个传入，不经过 shell。</param>
    /// <param name="cancellationToken">取消运行；取消时照常抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>命令的结果；启动不了或超时时退出码为 -1，原因放在标准错误里。</returns>
    private async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(fileName, arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ProcessStartException or TimeoutException)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    /// <summary>服务的启动命令里有没有一个参数正是这个程序（两边都解析符号链接后按路径完整比较）。</summary>
    /// <param name="words">启动命令里可能是程序路径的词，见 <see cref="CommandWords"/>。</param>
    /// <param name="executablePath">这个程序的路径。</param>
    /// <returns>有一个绝对路径的词和这个程序是同一个文件时为 true；只是包含这个路径（例如 <c>chatpilot-node-dev</c>）不算。</returns>
    internal bool RunsExecutable(IEnumerable<string> words, string executablePath)
    {
        var program = realPath(executablePath);
        return words.Any(word => IsAbsolute(word) && PathComparer.Equals(realPath(word), program));
    }

    /// <summary>
    /// 启动命令里可能是程序路径的词：引号里的整段（路径可能有空格），以及按空白分开的每个词。
    /// 计划任务的 <c>cmd.exe /c ""程序" run &gt;&gt; 日志"</c> 这种写法引号是成对叠着的，所以每一段都算，不按引号的奇偶判断。
    /// </summary>
    /// <param name="text">启动命令或其中一部分。</param>
    /// <returns>去掉首尾空白后的各段和各个词。</returns>
    internal static IEnumerable<string> CommandWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var segment in text.Split('"'))
        {
            if (segment.Trim() is { Length: > 0 } whole)
            {
                yield return whole;
            }

            foreach (var word in segment.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                yield return word;
            }
        }
    }

    /// <summary>Windows 命令行的第一个参数（程序名后面的那个），例如 <c>"C:\…\chatpilot-node.exe" run</c> 里的 <c>run</c>。</summary>
    /// <param name="commandLine">进程的完整命令行；读不到时为 null 或空。</param>
    /// <returns>第一个参数（去掉引号）；没有参数时为 null。</returns>
    internal static string? FirstArgument(string? commandLine)
    {
        var text = commandLine?.TrimStart();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var end = text[0] == '"' ? text.IndexOf('"', 1) : text.IndexOfAny([' ', '\t']);
        if (end < 0)
        {
            return null;
        }

        var rest = text[(end + 1)..].TrimStart();
        var stop = rest.IndexOfAny([' ', '\t']);
        var first = (stop < 0 ? rest : rest[..stop]).Trim('"');
        return first.Length > 0 ? first : null;
    }

    /// <summary>
    /// 从 <c>launchctl print</c> 的输出里取出启动命令：<c>program = …</c> 和 <c>arguments = { … }</c> 里的每一行
    /// （每行是一个完整的参数，可能有空格）。
    /// </summary>
    /// <param name="lines">去掉首尾空白的输出行。</param>
    /// <returns>程序和各个参数。</returns>
    internal static IEnumerable<string> LaunchdCommand(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.FirstOrDefault(l => l.StartsWith("program = ", StringComparison.Ordinal)) is { } program)
        {
            yield return program["program = ".Length..];
        }

        var start = lines.ToList().IndexOf("arguments = {");
        if (start < 0)
        {
            yield break;
        }

        foreach (var line in lines.Skip(start + 1).TakeWhile(l => l != "}"))
        {
            yield return line;
        }
    }

    /// <summary>
    /// 从 <c>systemctl show --property=ExecStart</c> 的输出里取出启动命令：每个 <c>path=</c> 的值，
    /// 以及 <c>argv[]=</c> 里按空格分开的每个参数。
    /// </summary>
    /// <param name="lines">去掉首尾空白的输出行。</param>
    /// <returns>程序和各个参数。</returns>
    internal static IEnumerable<string> SystemdCommand(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines.Where(l => l.StartsWith("ExecStart=", StringComparison.Ordinal)))
        {
            foreach (Match match in SystemdExecField().Matches(line))
            {
                if (match.Groups["key"].Value == "path")
                {
                    yield return match.Groups["value"].Value.Trim();
                    continue;
                }

                foreach (var word in match.Groups["value"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return word;
                }
            }
        }
    }

    /// <summary>在 <c>systemctl show</c> 的 <c>键=值</c> 输出里取一个属性。</summary>
    /// <param name="lines">去掉首尾空白的输出行。</param>
    /// <param name="name">属性名，例如 <c>ActiveState</c>。</param>
    /// <returns>属性的值；没有这个属性时为 null。</returns>
    private static string? Property(IReadOnlyList<string> lines, string name) =>
        lines.FirstOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    /// <summary>真实环境里怎么解析路径：转成完整路径、逐级展开符号链接；解析不了时原样返回。</summary>
    /// <param name="path">要解析的路径。</param>
    /// <returns>真实路径；出错时为原来的路径。</returns>
    internal static string RealPath(string path)
    {
        try
        {
            return PathPolicy.ResolveRealPath(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>按这个系统的规则，路径是不是绝对路径（Windows：<c>C:\…</c> 或 <c>\\服务器\…</c>；其他系统：以 <c>/</c> 开头）。</summary>
    /// <param name="path">启动命令里的一个词。</param>
    /// <returns>是绝对路径时为 true。</returns>
    private bool IsAbsolute(string path) =>
        platform == ServicePlatform.Windows
            ? (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') || path.StartsWith(@"\\", StringComparison.Ordinal)
            : path.StartsWith('/');

    /// <summary>这个系统的路径比较规则：Windows 和 macOS（默认）不区分大小写。</summary>
    private StringComparer PathComparer =>
        platform is ServicePlatform.Windows or ServicePlatform.MacOS ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>按 Windows 的规则取路径里的文件名（<c>\</c> 和 <c>/</c> 都是分隔符，在别的系统上测试时也一样）。</summary>
    /// <param name="path">Windows 路径。</param>
    /// <returns>最后一个分隔符后面的部分。</returns>
    private static string WindowsFileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>是不是 uid：1 到 10 位 ASCII 数字。</summary>
    /// <param name="text">要检查的文字。</param>
    /// <returns>是 uid 时为 true。</returns>
    private static bool IsUid(string text) => text.Length is > 0 and <= 10 && text.All(char.IsAsciiDigit);

    /// <summary>把命令输出拆成行，去掉首尾空白、开头可能有的 BOM 和空行。</summary>
    /// <param name="text">命令输出。</param>
    /// <returns>非空的行。</returns>
    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim().TrimStart('﻿')).Where(l => l.Length > 0).ToList();

    /// <summary>把命令失败的原因整理成一行给用户看：优先用标准错误，没有时用标准输出，都没有时写退出码。</summary>
    /// <param name="result">失败的命令结果。</param>
    /// <returns>最多 <see cref="MaxDetailChars"/> 个字符的一行说明。</returns>
    private static string Describe(ProcessResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError
            : !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput
            : $"退出码 {result.ExitCode}";
        return Shared.Text.TextChunker.Truncate(string.Join(' ', Lines(text)), MaxDetailChars);
    }

    /// <summary>
    /// 能直接放进 WQL <c>LIKE</c> 和 PowerShell 单引号字符串的程序名：只有字母、数字、<c>-</c>、<c>.</c>，以 <c>.exe</c> 结尾。
    /// 别的名字不加过滤条件，列出所有进程再按路径挑。
    /// </summary>
    /// <returns>编译期生成的正则。</returns>
    [GeneratedRegex(@"^[A-Za-z0-9.-]+\.exe\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SafeProcessName();

    /// <summary><c>systemctl show</c> 里 ExecStart 的 <c>path=…</c> 和 <c>argv[]=…</c> 字段，值到下一个 <c> ;</c> 为止。</summary>
    /// <returns>编译期生成的正则。</returns>
    [GeneratedRegex(@"(?<key>path|argv\[\])=(?<value>[^;]*?) ;", RegexOptions.CultureInvariant)]
    private static partial Regex SystemdExecField();
}
