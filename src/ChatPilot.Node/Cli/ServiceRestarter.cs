using System.Diagnostics;
using System.Text;

namespace ChatPilot.Node.Cli;

/// <summary>
/// 升级后重启按 README 设置的开机自启服务：Windows 计划任务 <c>ChatPilotNode</c>、macOS launchd <c>com.chatpilot.node</c>、
/// Linux 用户级 systemd 服务 <c>chatpilot-node</c>。只重启运行的正是这个程序的服务，别的不碰。
/// </summary>
/// <remarks>
/// 新节点连上服务器时会顶掉还没退出的旧节点（同一个设备只能有一个连接），旧节点收到后自己退出，所以不用等旧进程完全结束。
/// </remarks>
public static class ServiceRestarter
{
    private const string WindowsTask = "ChatPilotNode";
    private const string LaunchdLabel = "com.chatpilot.node";
    private const string SystemdUnit = "chatpilot-node";

    /// <summary>重启运行着这个程序的服务。</summary>
    /// <returns>重启了的服务的说明；没找到（或者不是运行这个程序的）返回 null。</returns>
    public static async Task<string?> TryRestartAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var actions = await RunAsync("powershell", [
                "-NoProfile", "-NonInteractive", "-Command",
                $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; (Get-ScheduledTask -TaskName {WindowsTask} -ErrorAction Stop).Actions | ForEach-Object {{ $_.Execute + ' ' + $_.Arguments }}",
            ], cancellationToken).ConfigureAwait(false);
            if (!RunsExecutable(actions, executablePath, ignoreCase: true))
            {
                return null;
            }

            var restart = await RunAsync("powershell", [
                "-NoProfile", "-NonInteractive", "-Command",
                $"Stop-ScheduledTask -TaskName {WindowsTask} -ErrorAction Stop; Start-ScheduledTask -TaskName {WindowsTask} -ErrorAction Stop",
            ], cancellationToken).ConfigureAwait(false);
            return restart?.ExitCode == 0 ? $"计划任务 {WindowsTask}" : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            // 用 sudo 升级时（程序在 /usr/local/bin 下），重启的是原来那个用户的服务。
            var uid = Environment.GetEnvironmentVariable("SUDO_UID")
                ?? (await RunAsync("id", ["-u"], cancellationToken).ConfigureAwait(false))?.Output.Trim();
            var service = $"gui/{uid}/{LaunchdLabel}";
            if (!RunsExecutable(await RunAsync("launchctl", ["print", service], cancellationToken).ConfigureAwait(false), executablePath, ignoreCase: false))
            {
                return null;
            }

            var restart = await RunAsync("launchctl", ["kickstart", "-k", service], cancellationToken).ConfigureAwait(false);
            return restart?.ExitCode == 0 ? $"launchd 服务 {LaunchdLabel}" : null;
        }

        if (OperatingSystem.IsLinux())
        {
            var show = await RunAsync("systemctl", ["--user", "show", SystemdUnit, "--property=ExecStart", "--value"], cancellationToken).ConfigureAwait(false);
            if (!RunsExecutable(show, executablePath, ignoreCase: false))
            {
                return null;
            }

            var restart = await RunAsync("systemctl", ["--user", "restart", SystemdUnit], cancellationToken).ConfigureAwait(false);
            return restart?.ExitCode == 0 ? $"systemd 用户服务 {SystemdUnit}" : null;
        }

        return null;
    }

    /// <summary>没找到服务时，告诉用户怎么自己重启。</summary>
    public static string ManualHint() =>
        OperatingSystem.IsWindows() ? $"请重启节点：Stop-ScheduledTask -TaskName {WindowsTask}; Start-ScheduledTask -TaskName {WindowsTask}（或者重新运行 chatpilot-node run）"
        : OperatingSystem.IsMacOS() ? $"请重启节点：launchctl kickstart -k gui/$(id -u)/{LaunchdLabel}（或者重新运行 chatpilot-node run）"
        : $"请重启节点：systemctl --user restart {SystemdUnit}（或者重新运行 chatpilot-node run）";

    /// <summary>服务的启动命令里是不是这个程序（命令输出里出现它的完整路径）。</summary>
    internal static bool RunsExecutable(ProcessOutput? output, string executablePath, bool ignoreCase) =>
        output is { ExitCode: 0 } && output.Output.Contains(
            Path.GetFullPath(executablePath),
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>运行一个系统命令；命令不存在或 30 秒内没结束返回 null。</summary>
    private static async Task<ProcessOutput?> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return new ProcessOutput(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}

/// <summary>系统命令的结果。</summary>
/// <param name="ExitCode">退出码。</param>
/// <param name="Output">标准输出。</param>
public sealed record ProcessOutput(int ExitCode, string Output);
