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
    /// <summary>解析一个路径时最多跟这么多次符号链接，防止链接成环。</summary>
    private const int MaxLinkHops = 40;

    /// <summary>实际存在的允许目录，已解析符号链接、去重；第二步检查用它。</summary>
    private readonly IReadOnlyList<string> _roots;

    /// <summary>第一步纯字符串检查用的目录：配置里写的路径加上解析后的真实路径。</summary>
    private readonly IReadOnlyList<string> _lexicalRoots;

    /// <summary>根据配置的允许目录创建白名单；不存在的目录会被忽略。</summary>
    /// <param name="roots">配置的允许目录，可以以 <c>~</c> 开头；不是绝对路径的忽略。</param>
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

    /// <summary>和 <see cref="PathComparer"/> 一样的大小写规则，用于前缀比较。</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>检查目录是否允许；允许时 <paramref name="resolved"/> 是解析符号链接后的真实路径。</summary>
    /// <param name="input">用户给的目录，必须是绝对路径或以 <c>~</c> 开头。</param>
    /// <param name="resolved">允许时为解析符号链接后的真实路径；不允许时为空字符串。</param>
    /// <param name="error">不允许时给用户看的原因；允许时为空字符串。</param>
    /// <returns>目录存在且在白名单内为 true。</returns>
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

    /// <summary>目录不在白名单内时的提示，列出允许的目录，方便用户改。</summary>
    /// <returns>提示文字；没有配置任何允许目录时提示去配置 <c>AllowedRoots</c>。</returns>
    private string OutsideMessage() =>
        _roots.Count == 0
            ? "这台设备没有配置允许的目录（AllowedRoots）"
            : "不在允许的目录内。允许的目录：" + string.Join("、", _roots);

    /// <summary>把开头的 <c>~</c> 换成用户主目录（不支持 <c>~user</c> 写法）。</summary>
    /// <param name="path">要展开的路径。</param>
    /// <returns>展开后的路径；不以 <c>~</c>、<c>~/</c> 或 <c>~\</c> 开头时原样返回。</returns>
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

    /// <summary>纯字符串判断路径是不是根目录本身或在它下面；按完整的一级目录比，<c>/a/bc</c> 不算在 <c>/a/b</c> 里。</summary>
    /// <param name="path">已规范化的完整路径。</param>
    /// <param name="root">允许的目录。</param>
    /// <returns>在根目录内（含根目录本身）为 true。</returns>
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
    /// <param name="fullPath">已规范化的绝对路径。</param>
    /// <returns>每一级都解析过符号链接的真实路径。</returns>
    /// <exception cref="IOException">符号链接层数超过 <see cref="MaxLinkHops"/>（多半是链接成环）。</exception>
    /// <exception cref="ArgumentException">路径不是绝对路径。</exception>
    internal static string ResolveRealPath(string fullPath)
    {
        var hops = 0;
        return Resolve(fullPath, ref hops);
    }

    /// <summary><see cref="ResolveRealPath"/> 的递归实现：链接目标里的链接也要解析，跳转次数累计在一起算。</summary>
    /// <param name="fullPath">已规范化的绝对路径。</param>
    /// <param name="hops">到目前为止跟过的符号链接次数。</param>
    /// <returns>解析后的真实路径。</returns>
    /// <exception cref="IOException">符号链接层数超过 <see cref="MaxLinkHops"/>。</exception>
    /// <exception cref="ArgumentException">路径不是绝对路径。</exception>
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
