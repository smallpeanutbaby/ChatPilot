namespace ChatPilot.Node.Execution;

/// <summary>
/// 目录白名单：远程只能在这些目录（含子目录）里开工作区。
/// 先展开 <c>~</c>、规范化 <c>..</c>，再逐级解析符号链接，用真实路径比较，防止路径穿越和链接跳出。
/// </summary>
public sealed class PathPolicy
{
    private const int MaxLinkHops = 40;

    private readonly IReadOnlyList<string> _roots;

    public PathPolicy(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots
            .Select(ExpandHome)
            .Where(r => Path.IsPathFullyQualified(r) && Directory.Exists(r))
            .Select(r => ResolveRealPath(Path.GetFullPath(r)))
            .Distinct(PathComparer)
            .ToList();
    }

    /// <summary>白名单里第一个存在的目录；没有时为 null。</summary>
    public string? DefaultDirectory => _roots.Count > 0 ? _roots[0] : null;

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>macOS（默认）和 Windows 的文件系统不区分大小写。</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>检查目录是否允许；允许时 <paramref name="resolved"/> 是解析符号链接后的真实路径。</summary>
    public bool TryResolve(string? input, out string resolved, out string error)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "目录不能为空";
            return false;
        }

        if (input.Contains('\0', StringComparison.Ordinal))
        {
            error = "目录里有非法字符";
            return false;
        }

        var expanded = ExpandHome(input.Trim());
        if (!Path.IsPathFullyQualified(expanded))
        {
            error = "必须是绝对路径（或以 ~ 开头）";
            return false;
        }

        string real;
        try
        {
            var full = Path.GetFullPath(expanded);
            if (!Directory.Exists(full))
            {
                error = "目录不存在";
                return false;
            }

            real = ResolveRealPath(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "无法解析这个目录";
            return false;
        }

        if (!_roots.Any(root => IsWithin(real, root)))
        {
            error = _roots.Count == 0
                ? "这台设备没有配置允许的目录（AllowedRoots）"
                : "不在允许的目录内。允许的目录：" + string.Join("、", _roots);
            return false;
        }

        resolved = real;
        error = "";
        return true;
    }

    public static string ExpandHome(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
    }

    private static bool IsWithin(string path, string root)
    {
        if (string.Equals(path, root, PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>从根开始逐级解析符号链接，得到真实路径。</summary>
    internal static string ResolveRealPath(string fullPath)
    {
        var hops = 0;
        return Resolve(fullPath, ref hops);
    }

    private static string Resolve(string fullPath, ref int hops)
    {
        var root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("不是绝对路径", nameof(fullPath));
        var parts = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is null)
            {
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new IOException("符号链接层数太多");
            }

            var target = info.LinkTarget;
            var absolute = Path.IsPathFullyQualified(target)
                ? target
                : Path.Combine(Path.GetDirectoryName(current) ?? root, target);
            current = Resolve(Path.GetFullPath(absolute), ref hops);
        }

        return current;
    }
}
