namespace ChatPilot.Node.Execution;

/// <summary>
/// 目录白名单：远程只能在这些目录（含子目录）里开工作区。
/// </summary>
/// <remarks>
/// 检查分两步：
/// <list type="number">
/// <item>纯字符串检查（展开 <c>~</c>、规范化 <c>..</c>）：不在白名单内的路径直接拒绝，<b>不访问文件系统</b>，
/// 避免访问 <c>\host\share</c>（Windows 会带着用户凭据连 SMB）或 <c>/net/host</c>（macOS 自动挂载），
/// 也不泄露白名单外某个目录是否存在。</item>
/// <item>逐级解析符号链接得到真实路径，再检查一次，防止通过链接跳出白名单。</item>
/// </list>
/// </remarks>
public sealed class PathPolicy
{
    private const int MaxLinkHops = 40;

    private readonly IReadOnlyList<string> _roots;
    private readonly IReadOnlyList<string> _lexicalRoots;

    /// <summary>根据配置的允许目录创建白名单；不存在的目录会被忽略。</summary>
    public PathPolicy(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var configured = roots
            .Select(ExpandHome)
            .Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath)
            .ToList();
        _roots = configured
            .Where(Directory.Exists)
            .Select(ResolveRealPath)
            .Distinct(PathComparer)
            .ToList();

        // 字符串检查同时认配置里写的路径和解析后的真实路径（例如 /tmp 和 /private/tmp）。
        _lexicalRoots = configured.Concat(_roots).Distinct(PathComparer).ToList();
    }

    /// <summary>白名单里第一个存在的目录；没有时为 null。</summary>
    public string? DefaultDirectory => _roots.Count > 0 ? _roots[0] : null;

    /// <summary>解析符号链接后、实际存在的允许目录。</summary>
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

        string full;
        try
        {
            full = Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "无法解析这个目录";
            return false;
        }

        // 第一步：只看字符串，不碰文件系统。
        if (!_lexicalRoots.Any(root => IsWithin(full, root)))
        {
            error = OutsideMessage();
            return false;
        }

        // 第二步：确认存在，解析符号链接后再查一次。
        string real;
        try
        {
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
            error = OutsideMessage();
            return false;
        }

        resolved = real;
        error = "";
        return true;
    }

    private string OutsideMessage() =>
        _roots.Count == 0
            ? "这台设备没有配置允许的目录（AllowedRoots）"
            : "不在允许的目录内。允许的目录：" + string.Join("、", _roots);

    /// <summary>把开头的 <c>~</c> 换成用户主目录（不支持 <c>~user</c> 写法）。</summary>
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
