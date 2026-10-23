namespace ChatPilot.Shared.Security;

/// <summary>写入只有当前用户能读写的文件（Unix 下权限 600，目录 700），并且原子替换。</summary>
public static class SecureFile
{
    public static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

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
