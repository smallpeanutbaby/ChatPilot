namespace ChatPilot.Shared.Security;

/// <summary>写入只有当前用户能读写的文件（Unix 下权限 600，新建的目录 700），先落盘再原子替换。</summary>
public static class SecureFile
{
    /// <summary>
    /// 确保目录存在。只有目录是这次新建的才把权限设成 700：
    /// 已有的目录（比如 <c>--config ~/node.json</c> 时的用户主目录）不去改它的权限。
    /// </summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>写入文本文件（UTF-8）：先写权限 600 的临时文件并落盘，再原子替换目标文件。</summary>
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("路径没有所在目录", nameof(path));
        EnsurePrivateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // 先建好权限为 600 的空文件再写内容，避免内容短暂地以默认权限存在。
            using (var stream = CreatePrivateFile(temp))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                // 改名前先落盘，断电后不会留下一个空的状态文件。
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>写入二进制文件，规则同 <see cref="WriteAllText"/>。</summary>
    public static void WriteAllBytes(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("路径没有所在目录", nameof(path));
        EnsurePrivateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = CreatePrivateFile(temp))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }
}
