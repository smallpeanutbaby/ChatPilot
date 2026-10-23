namespace ChatPilot.Tests.Support;

/// <summary>测试用的临时目录，用完删除。</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        // macOS 的 /var 是指向 /private/var 的链接，用真实路径避免比较时出岔子。
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chatpilot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Path = ChatPilot.Node.Execution.PathPolicy.ResolveRealPath(root);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string CreateDirectory(params string[] parts)
    {
        var path = Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
