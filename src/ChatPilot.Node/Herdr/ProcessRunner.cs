using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ChatPilot.Node.Herdr;

/// <summary>外部程序的运行结果。</summary>
/// <param name="ExitCode">退出码。</param>
/// <param name="StandardOutput">标准输出。</param>
/// <param name="StandardError">标准错误输出。</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>进程启动失败（通常是找不到可执行文件）。</summary>
/// <param name="message">给用户看的说明，带上了程序名。</param>
/// <param name="inner">启动时的原始异常。</param>
public sealed class ProcessStartException(string message, Exception inner) : Exception(message, inner);

/// <summary>启动外部程序。参数一律按数组传，不经过 shell。</summary>
public interface IProcessRunner
{
    /// <summary>运行程序并收集输出。</summary>
    /// <param name="fileName">可执行文件。</param>
    /// <param name="arguments">参数，每一项原样作为一个参数传给程序。</param>
    /// <param name="timeout">超时时间，到时结束整个进程树。</param>
    /// <param name="cancellationToken">取消时同样结束进程。</param>
    /// <returns>退出码和全部输出（不论退出码是多少）。</returns>
    /// <exception cref="ProcessStartException">程序启动失败。</exception>
    /// <exception cref="TimeoutException">超时，进程已被结束。</exception>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>用 <see cref="Process"/> 启动程序：参数数组、无 shell、去掉 herdr 环境变量、超时结束整个进程树。</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// herdr 会根据这些环境变量推断"当前 pane"。节点可能是在某个 herdr 终端里启动的，
    /// 去掉它们，保证每次调用都只受显式参数控制。
    /// </summary>
    private static readonly string[] StrippedVariables =
        ["HERDR_ENV", "HERDR_PANE_ID", "HERDR_TAB_ID", "HERDR_WORKSPACE_ID", "HERDR_SOCKET_PATH"];

    /// <inheritdoc />
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

            // 进程退出后如果留下了继承输出管道的后台进程，管道不会关闭；读输出也必须在截止时间内完成。
            var output = await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, output[0], output[1]);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{fileName} 运行超过 {timeout.TotalSeconds:0} 秒，已结束");
        }
    }

    /// <summary>结束整个进程树；进程已经退出时忽略。</summary>
    /// <param name="process">要结束的进程。</param>
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
