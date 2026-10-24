using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ChatPilot.Node.Cli;

/// <summary>
/// 独占打开一个锁文件，保证同一时间只有一个进程在做某件事：同一个配置只运行一个节点、同一个程序只有一个升级在进行。
/// 进程退出（包括被强制结束）时系统自动释放。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Windows 上靠不共享打开（其他进程打不开这个文件），关闭时文件自动删掉。</item>
/// <item>Linux / macOS 上 .NET 用 <c>flock</c> 加独占锁；锁文件留在原地，不删除（删了再建会让两个进程各锁一个文件）。</item>
/// <item>文件里写着拿锁的进程号，方便排查。</item>
/// </list>
/// </remarks>
public sealed class ExclusiveFileLock : IDisposable
{
    /// <summary>等锁时每隔多久再试一次。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>独占打开的锁文件；关闭即释放锁。</summary>
    private readonly FileStream _stream;

    /// <summary>包装已经拿到的锁文件。</summary>
    /// <param name="stream">独占打开的锁文件。</param>
    private ExclusiveFileLock(FileStream stream) => _stream = stream;

    /// <summary>锁文件的完整路径。</summary>
    public string Path => _stream.Name;

    /// <summary>试一次拿锁，不等。</summary>
    /// <param name="path">锁文件路径；所在目录必须存在，文件不存在时创建。</param>
    /// <returns>拿到的锁，由调用方释放；别的进程拿着时为 null。</returns>
    /// <exception cref="UnauthorizedAccessException">没有权限创建或打开锁文件。</exception>
    /// <exception cref="DirectoryNotFoundException">锁文件所在的目录不存在。</exception>
    public static ExclusiveFileLock? TryAcquire(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);
        }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
        {
            // Windows 上是共享冲突，Linux / macOS 上是 flock 拿不到：别的进程正拿着。
            return null;
        }

        try
        {
            stream.SetLength(0);
            stream.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n"));
            stream.Flush();
        }
        catch (IOException)
        {
            // 进程号只是方便排查，写不进去不影响加锁。
        }

        return new ExclusiveFileLock(stream);
    }

    /// <summary>拿锁，别的进程拿着时每隔一会儿再试，最多等 <paramref name="wait"/>。</summary>
    /// <param name="path">锁文件路径；所在目录必须存在。</param>
    /// <param name="wait">最多等多久；<see cref="TimeSpan.Zero"/> 表示只试一次。</param>
    /// <param name="cancellationToken">取消等待；取消时抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>拿到的锁，由调用方释放；等到时限还拿不到时为 null。</returns>
    /// <exception cref="UnauthorizedAccessException">没有权限创建或打开锁文件。</exception>
    /// <exception cref="DirectoryNotFoundException">锁文件所在的目录不存在。</exception>
    public static async Task<ExclusiveFileLock?> AcquireAsync(string path, TimeSpan wait, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAcquire(path) is { } acquired)
            {
                return acquired;
            }

            var remaining = wait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(remaining < RetryInterval ? remaining : RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>释放锁（Windows 上同时删掉锁文件）。</summary>
    public void Dispose() => _stream.Dispose();
}
