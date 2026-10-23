using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ChatPilot.Node.Herdr;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>进程启动失败（通常是找不到可执行文件）。</summary>
public sealed class ProcessStartException(string message, Exception inner) : Exception(message, inner);

/// <summary>启动外部程序。参数一律按数组传，不经过 shell。</summary>
public interface IProcessRunner
{
    /// <exception cref="ProcessStartException">程序启动失败。</exception>
    /// <exception cref="TimeoutException">超时，进程已被结束。</exception>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// herdr 会根据这些环境变量推断"当前 pane"。节点可能是在某个 herdr 终端里启动的，
    /// 去掉它们，保证每次调用都只受显式参数控制。
    /// </summary>
    private static readonly string[] StrippedVariables =
        ["HERDR_ENV", "HERDR_PANE_ID", "HERDR_TAB_ID", "HERDR_WORKSPACE_ID", "HERDR_SOCKET_PATH"];

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in StrippedVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ProcessStartException($"无法启动 {fileName}：{ex.Message}", ex);
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{fileName} 运行超过 {timeout.TotalSeconds:0} 秒，已结束");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 进程已经退出。
        }
    }
}
