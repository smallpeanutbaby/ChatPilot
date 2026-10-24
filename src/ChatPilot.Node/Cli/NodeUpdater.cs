using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatPilot.Node.Herdr;

namespace ChatPilot.Node.Cli;

/// <summary>升级失败，<see cref="Exception.Message"/> 是给用户看的原因。</summary>
/// <param name="message">给用户看的原因。</param>
/// <param name="innerException">引起失败的原始异常；没有时为 null。</param>
public sealed class UpdateException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>版本号：主.次.修订，可带预发布后缀（例如 <c>0.2.0-beta.1</c>）。</summary>
/// <param name="Major">主版本。</param>
/// <param name="Minor">次版本。</param>
/// <param name="Patch">修订号。</param>
/// <param name="Prerelease">预发布后缀；正式版为 null。</param>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease)
{
    /// <summary>解析 <c>0.1.0</c>、<c>v0.1.0</c>、<c>0.2.0-beta.1</c>、<c>0.1.0+构建信息</c>。</summary>
    /// <param name="text">版本号文本，例如程序集的 InformationalVersion 或 GitHub 发布的标签。</param>
    /// <returns>解析出的版本（构建信息被丢掉）；为 null 或格式不对时为 null。</returns>
    public static ReleaseVersion? Parse(string? text)
    {
        var match = VersionRegex().Match(text ?? "");
        return match.Success
            ? new ReleaseVersion(
                int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture),
                match.Groups["pre"].Success ? match.Groups["pre"].Value : null)
            : null;
    }

    /// <summary>比 <paramref name="other"/> 新吗。同一个版本号，正式版比预发布版新。</summary>
    /// <param name="other">要比较的版本，通常是正在运行的版本。</param>
    /// <returns>严格更新时为 true；相同或更旧时为 false。两个预发布后缀按 SemVer 规则比较，见 <see cref="ComparePrerelease"/>。</returns>
    public bool IsNewerThan(ReleaseVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var numbers = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (numbers != 0)
        {
            return numbers > 0;
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => false,
            (null, _) => true,
            (_, null) => false,
            _ => ComparePrerelease(Prerelease, other.Prerelease) > 0,
        };
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (Prerelease is null ? "" : "-" + Prerelease);

    /// <summary>
    /// 按 SemVer 比较两个预发布后缀：按 <c>.</c> 分段逐段比较，纯数字的段按数值比（<c>beta.10</c> 比 <c>beta.9</c> 新），
    /// 纯数字的段比带字母的段旧，带字母的段按 ASCII 比；前面的段都相同时，段多的新（<c>beta.1</c> 比 <c>beta</c> 新）。
    /// </summary>
    /// <param name="left">一个预发布后缀，例如 <c>beta.10</c>。</param>
    /// <param name="right">另一个预发布后缀。</param>
    /// <returns><paramref name="left"/> 更新时为正数，更旧时为负数，相同时为 0。</returns>
    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var leftNumeric = IsNumeric(a[i]);
            var rightNumeric = IsNumeric(b[i]);
            var result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => CompareNumbers(a[i], b[i]),
                (true, false) => -1,
                (false, true) => 1,
                _ => Math.Sign(string.CompareOrdinal(a[i], b[i])),
            };
            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>预发布后缀的一段是不是纯数字。</summary>
    /// <param name="part">一段，例如 <c>10</c> 或 <c>beta</c>。</param>
    /// <returns>非空并且只有 ASCII 数字时为 true。</returns>
    private static bool IsNumeric(string part) => part.Length > 0 && part.All(char.IsAsciiDigit);

    /// <summary>比较两个纯数字的段，不转成整数（段最长 64 位，可能超出范围）。</summary>
    /// <param name="left">纯数字的段。</param>
    /// <param name="right">另一个纯数字的段。</param>
    /// <returns><paramref name="left"/> 数值更大时为正数，更小时为负数，相等时为 0。</returns>
    private static int CompareNumbers(string left, string right)
    {
        var a = left.TrimStart('0');
        var b = right.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : Math.Sign(string.CompareOrdinal(a, b));
    }

    /// <summary>
    /// 可选的 <c>v</c> 前缀、三段数字（每段最多 6 位）、可选的预发布后缀和构建信息。
    /// 数字只认 ASCII 的 <c>[0-9]</c>：.NET 的 <c>\d</c> 还匹配其他文字的数字，<see cref="int.Parse(string, IFormatProvider)"/> 却不认。
    /// </summary>
    /// <returns>编译期生成的正则。</returns>
    [GeneratedRegex(@"^v?(?<major>[0-9]{1,6})\.(?<minor>[0-9]{1,6})\.(?<patch>[0-9]{1,6})(?:-(?<pre>[0-9A-Za-z.-]{1,64}))?(?:\+[0-9A-Za-z.-]*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}

/// <summary>GitHub 上发布的一个版本。</summary>
/// <param name="Version">版本号。</param>
/// <param name="Assets">文件名 → 下载地址。</param>
public sealed record NodeRelease(ReleaseVersion Version, IReadOnlyDictionary<string, Uri> Assets);

/// <summary>
/// 从 GitHub Releases 升级节点程序：查最新版本 → 下载这个平台的压缩包和 <c>SHA256SUMS.txt</c> → 核对校验值 →
/// 解出程序、试运行一次 → 替换正在用的程序。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>校验值和压缩包来自同一个发布，能防下载损坏和传输途中被改，防不了仓库本身被攻破：信任程度和手动下载一样。</item>
/// <item>新程序先在这台电脑上运行一次 <c>help</c>，跑不起来（架构不对、被系统拦截）就不替换。</item>
/// <item>替换是原子的：Linux / macOS 直接改名覆盖（正在运行的进程不受影响）；Windows 不能覆盖运行中的程序，
/// 先把旧程序改名成 <c>.old</c>，下次启动时删掉。</item>
/// <item>同一个程序同时只能有一个升级在进行（程序旁边的升级锁），解出来的新程序每次用不同的名字。</item>
/// <item>下载有大小上限，读内容时卡住（一段时间没收到数据）或总时间太长都会放弃，不会一直挂着。</item>
/// <item>只在用户自己运行 <c>chatpilot-node update</c> 时升级，不能从微信远程触发。</item>
/// </list>
/// </remarks>
/// <param name="http">访问 GitHub 用的客户端。</param>
/// <param name="repository">仓库，例如 <c>owner/ChatPilot</c>。</param>
/// <param name="runtimeIdentifier">这台电脑的平台，例如 <c>win-x64</c>。</param>
/// <param name="executablePath">要替换的程序。</param>
/// <param name="tryRun">试运行新程序，能正常运行返回 true。</param>
public sealed class NodeUpdater(
    HttpClient http,
    string repository,
    string runtimeIdentifier,
    string executablePath,
    Func<string, CancellationToken, Task<bool>> tryRun)
{
    /// <summary>默认从这个仓库升级。</summary>
    public const string DefaultRepository = "smallpeanutbaby/ChatPilot";

    /// <summary>发布了程序的平台。</summary>
    public static readonly IReadOnlySet<string> SupportedRuntimes = new HashSet<string>(StringComparer.Ordinal)
    {
        "win-x64", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64",
    };

    /// <summary>发布里的校验文件名，内容是 <c>sha256sum</c> 的输出。</summary>
    private const string ChecksumFile = "SHA256SUMS.txt";

    /// <summary>压缩包的下载上限：300 MiB，防止被一个超大文件耗尽内存。</summary>
    private const long MaxArchiveBytes = 300L * 1024 * 1024;

    /// <summary>解压出来的程序的大小上限：500 MiB，防止解压炸弹。</summary>
    private const long MaxExecutableBytes = 500L * 1024 * 1024;

    /// <summary>版本信息（<c>releases/latest</c> 的 JSON）的大小上限：4 MiB。</summary>
    private const long MaxReleaseInfoBytes = 4L * 1024 * 1024;

    /// <summary>Windows 上替换时旧程序最多留几个备份名（<c>.old</c>、<c>.old1</c>…<c>.old9</c>）。</summary>
    private const int MaxBackups = 10;

    /// <summary>
    /// 读响应内容时多久没收到任何数据就放弃。收到响应头之后 <see cref="HttpClient.Timeout"/> 就不管了，
    /// 连接半断开时读内容会一直卡住，所以自己计时。测试里改短。
    /// </summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>读一个响应的内容最多用多久（网速很慢也够下完一个压缩包）。测试里改短。</summary>
    internal TimeSpan BodyDeadline { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>是不是 Windows 平台：决定压缩包格式（zip / tar.gz）和程序名是否带 <c>.exe</c>。</summary>
    /// <param name="rid">运行时标识，例如 <c>win-x64</c>。</param>
    /// <returns>以 <c>win-</c> 开头时为 true。</returns>
    private static bool IsWindowsRuntime(string rid) => rid.StartsWith("win-", StringComparison.Ordinal);

    /// <summary>这个平台的压缩包名，例如 <c>chatpilot-node-win-x64.zip</c>。</summary>
    /// <param name="rid">运行时标识，例如 <c>linux-arm64</c>。</param>
    /// <returns>Windows 是 <c>.zip</c>，其他平台是 <c>.tar.gz</c>。</returns>
    public static string AssetName(string rid) => $"chatpilot-node-{rid}{(IsWindowsRuntime(rid) ? ".zip" : ".tar.gz")}";

    /// <summary>压缩包里的程序名。</summary>
    /// <param name="rid">运行时标识。</param>
    /// <returns>Windows 上是 <c>chatpilot-node.exe</c>，其他平台是 <c>chatpilot-node</c>。</returns>
    public static string ExecutableName(string rid) => IsWindowsRuntime(rid) ? "chatpilot-node.exe" : "chatpilot-node";

    /// <summary>正在运行的节点的版本。</summary>
    /// <returns>程序集的 InformationalVersion；读不到或格式不对时为 <c>0.0.0</c>。</returns>
    public static ReleaseVersion CurrentVersion() =>
        ReleaseVersion.Parse(typeof(NodeUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new ReleaseVersion(0, 0, 0, null);

    /// <summary>访问 GitHub 用的客户端（走系统代理）。</summary>
    /// <returns>超时 10 分钟、带 <c>chatpilot-node/版本</c> User-Agent 的客户端，由调用方释放。</returns>
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"chatpilot-node/{CurrentVersion()}");
        return client;
    }

    /// <summary>查最新的正式版本。</summary>
    /// <param name="cancellationToken">取消查询。</param>
    /// <returns>最新版本和它的 https 下载地址。</returns>
    /// <exception cref="UpdateException">查不到、读版本信息时中断或卡住、版本信息看不懂。</exception>
    public async Task<NodeRelease> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.github.com/repos/{repository}/releases/latest"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UpdateException($"{repository} 还没有发布过版本");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"查询最新版本失败：GitHub 返回 {(int)response.StatusCode}");
        }

        var body = await ReadBodyAsync(response, "最新版本信息", MaxReleaseInfoBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var version = ReleaseVersion.Parse(root.GetProperty("tag_name").GetString())
                ?? throw new UpdateException("最新版本的标签不是版本号格式");
            var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() is { } name
                    && Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url)
                    && url.Scheme == Uri.UriSchemeHttps)
                {
                    assets[name] = url;
                }
            }

            return new NodeRelease(version, assets);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UpdateException("GitHub 返回的版本信息看不懂：" + ex.Message);
        }
    }

    /// <summary>
    /// 下载、核对、试运行，然后替换程序。整个过程拿着这个程序的升级锁（见 <see cref="UpdateLockPath"/>），
    /// 同时运行的另一个 <c>update</c> 直接失败，不会互相覆盖解出来的新程序。
    /// </summary>
    /// <param name="release">要安装的版本，通常来自 <see cref="GetLatestAsync"/>。</param>
    /// <param name="cancellationToken">取消下载和试运行。</param>
    /// <returns>Windows 上旧程序改名后的路径（例如 <c>chatpilot-node.exe.old</c>，还在运行的旧节点用的就是它）；其他系统为 null。</returns>
    /// <exception cref="UpdateException">
    /// 任何一步不通过，或者另一个 <c>update</c> 正在升级这个程序；这时正在用的程序没有被改动。
    /// 唯一的例外是 Windows 上换上新程序失败、把旧程序改回去也失败：消息里写明旧程序现在在哪。
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">没有权限在程序所在的目录里写文件。</exception>
    public async Task<string?> InstallAsync(NodeRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        var assetName = AssetName(runtimeIdentifier);
        if (!release.Assets.TryGetValue(assetName, out var assetUrl))
        {
            throw new UpdateException($"版本 {release.Version} 里没有这个平台的程序（{assetName}）");
        }

        if (!release.Assets.TryGetValue(ChecksumFile, out var checksumUrl))
        {
            throw new UpdateException($"版本 {release.Version} 没有校验文件 {ChecksumFile}，不安装");
        }

        using var updateLock = ExclusiveFileLock.TryAcquire(UpdateLockPath(executablePath))
            ?? throw new UpdateException("另一个 chatpilot-node update 正在升级这个程序，等它结束再试");
        var sums = Encoding.UTF8.GetString(await DownloadAsync(checksumUrl, 1024 * 1024, cancellationToken).ConfigureAwait(false));
        var expected = ParseChecksums(sums).GetValueOrDefault(assetName)
            ?? throw new UpdateException($"{ChecksumFile} 里没有 {assetName} 的校验值，不安装");
        var archive = await DownloadAsync(assetUrl, MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(archive)), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("下载的文件和校验值对不上（可能损坏或被篡改），没有安装");
        }

        // 每次用不同的名字，万一有别的进程绕过了升级锁，也不会互相覆盖、互相删掉对方解出来的程序。
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        var staged = Path.Combine(directory, $".chatpilot-node.new-{Guid.NewGuid():N}" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        try
        {
            ExtractExecutable(archive, staged);
            if (!await tryRun(staged, cancellationToken).ConfigureAwait(false))
            {
                throw new UpdateException("新版本在这台电脑上运行不起来，没有替换");
            }

            return Replace(staged, executablePath, OperatingSystem.IsWindows(), File.Move);
        }
        finally
        {
            TryDelete(staged);
        }
    }

    /// <summary>升级锁文件：程序旁边的 <c>.chatpilot-node.update.lock</c>，每个安装位置一把。</summary>
    /// <param name="executablePath">要升级的程序。</param>
    /// <returns>锁文件的完整路径。</returns>
    internal static string UpdateLockPath(string executablePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath))!, ".chatpilot-node.update.lock");

    /// <summary>Windows 上替换时给旧程序用的备份名，按顺序是 <c>程序名.old</c>、<c>程序名.old1</c>…<c>程序名.old9</c>。</summary>
    /// <param name="executablePath">程序的路径。</param>
    /// <returns>这 <see cref="MaxBackups"/> 个备份的完整路径；只有这些名字是升级自己建的。</returns>
    internal static IEnumerable<string> BackupPaths(string executablePath)
    {
        var full = Path.GetFullPath(executablePath);
        return Enumerable.Range(0, MaxBackups).Select(i => full + ".old" + (i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// 删掉 Windows 上替换时留下的旧程序。只删升级自己起的名字（<see cref="BackupPaths"/>），
    /// 用户自己留的其他备份不碰；还在运行、删不掉或没有权限的留到下次。不会抛异常，节点启动不会因为它失败。
    /// </summary>
    /// <param name="executablePath">当前程序的路径；删的是它旁边的 <c>程序名.old</c>、<c>程序名.old1</c>…<c>程序名.old9</c>。</param>
    public static void CleanUpOldBinaries(string executablePath)
    {
        IEnumerable<string> backups;
        try
        {
            backups = BackupPaths(executablePath).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var old in backups)
        {
            TryDelete(old);
        }
    }

    /// <summary>运行一次 <c>程序 help</c>，30 秒内正常退出就算能运行。</summary>
    /// <param name="path">要试运行的程序。</param>
    /// <param name="cancellationToken">取消试运行。</param>
    /// <returns>退出码为 0 时为 true；启动不了、超时（会结束进程）或退出码非 0 时为 false。</returns>
    public static Task<bool> TryRunAsync(string path, CancellationToken cancellationToken) =>
        TryRunAsync(new ProcessRunner(), path, cancellationToken);

    /// <summary>用指定的进程启动方式运行一次 <c>程序 help</c>，30 秒内正常退出就算能运行。</summary>
    /// <param name="runner">启动程序的方式（测试里换成假的）。</param>
    /// <param name="path">要试运行的程序。</param>
    /// <param name="cancellationToken">取消试运行；取消时照常抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>退出码为 0 时为 true；启动不了、超时（会结束进程）或退出码非 0 时为 false。</returns>
    internal static async Task<bool> TryRunAsync(IProcessRunner runner, string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(path, ["help"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is ProcessStartException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>解析 <c>sha256sum</c> 的输出：每行"校验值 两个空格 文件名"。</summary>
    /// <param name="text">校验文件内容。</param>
    /// <returns>文件名（去掉二进制模式的 <c>*</c>）→ 校验值；格式不对的行被跳过。</returns>
    internal static Dictionary<string, string> ParseChecksums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[0].All(char.IsAsciiHexDigit))
            {
                sums[parts[1].Trim().TrimStart('*')] = parts[0];
            }
        }

        return sums;
    }

    /// <summary>发请求，只等到响应头；网络错误和超时转成给用户看的 <see cref="UpdateException"/>。</summary>
    /// <param name="request">要发的请求。</param>
    /// <param name="cancellationToken">取消请求；取消时照常抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>响应，由调用方释放。</returns>
    /// <exception cref="UpdateException">连不上 GitHub 或超时。</exception>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"连不上 GitHub（{request.RequestUri?.Host}）：{ex.Message}");
        }
    }

    /// <summary>把文件整个下载到内存，声明的长度或实际读到的字节超过上限就放弃。</summary>
    /// <param name="url">下载地址。</param>
    /// <param name="maxBytes">允许的最大字节数。</param>
    /// <param name="cancellationToken">取消下载。</param>
    /// <returns>文件内容。</returns>
    /// <exception cref="UpdateException">返回非成功状态码、文件太大、下载中断、卡住或超时。</exception>
    private async Task<byte[]> DownloadAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"下载 {url.Segments[^1]} 失败：返回 {(int)response.StatusCode}");
        }

        return await ReadBodyAsync(response, url.Segments[^1], maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读完响应内容。超过 <paramref name="maxBytes"/> 就放弃；<see cref="StallTimeout"/> 内没收到任何数据、
    /// 或者总共超过 <see cref="BodyDeadline"/> 也放弃，不会因为连接半断开一直卡住。
    /// </summary>
    /// <param name="response">已经收到响应头的成功响应。</param>
    /// <param name="name">给用户看的名字，例如文件名。</param>
    /// <param name="maxBytes">允许的最大字节数。</param>
    /// <param name="cancellationToken">取消读取；取消时照常抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>响应内容。</returns>
    /// <exception cref="UpdateException">内容太大、读的时候连接出错、卡住或超时。</exception>
    private async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, string name, long maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new UpdateException($"{name} 太大了，不下载");
        }

        var started = Stopwatch.GetTimestamp();
        var deadlineArmed = BodyDeadline <= StallTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            timeout.CancelAfter(deadlineArmed ? BodyDeadline : StallTimeout);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                // 每读一次重新计时：等的是"下一块数据"，同时不超过总时限。记下这次是哪个时限先到，出错时说对原因。
                var remaining = BodyDeadline - Stopwatch.GetElapsedTime(started);
                deadlineArmed = remaining <= StallTimeout;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new OperationCanceledException(timeout.Token);
                }

                timeout.CancelAfter(deadlineArmed ? remaining : StallTimeout);
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > maxBytes)
                {
                    throw new UpdateException($"{name} 太大了，不下载");
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(deadlineArmed
                ? $"下载 {name} 超过 {BodyDeadline.TotalMinutes:0} 分钟还没下完，放弃了"
                : $"下载 {name} 卡住了：{StallTimeout.TotalSeconds:0} 秒没有收到数据，请检查网络后重试");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"下载 {name} 中断了：{ex.Message}", ex);
        }
    }

    /// <summary>从压缩包里解出程序（压缩包里应该只有一个同名程序）。</summary>
    /// <param name="archive">已经核对过校验值的压缩包内容。</param>
    /// <param name="destination">解出的程序放在哪；非 Windows 上会设成可执行（755）。</param>
    /// <exception cref="UpdateException">压缩包损坏、没有程序、有不止一个程序或程序大得不正常。</exception>
    private void ExtractExecutable(byte[] archive, string destination)
    {
        var name = ExecutableName(runtimeIdentifier);
        var found = false;
        try
        {
            if (IsWindowsRuntime(runtimeIdentifier))
            {
                using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
                foreach (var entry in zip.Entries.Where(e => Path.GetFileName(e.FullName) == name))
                {
                    CheckSize(entry.Length, found);
                    entry.ExtractToFile(destination, overwrite: true);
                    found = true;
                }
            }
            else
            {
                using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (tar.GetNextEntry() is { } entry)
                {
                    if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(entry.Name) == name)
                    {
                        CheckSize(entry.Length, found);
                        entry.ExtractToFile(destination, overwrite: true);
                        found = true;
                    }
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException("压缩包损坏：" + ex.Message);
        }

        if (!found)
        {
            throw new UpdateException($"压缩包里没有 {name}");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>解出一个程序之前检查：之前没找到过同名程序，大小也在上限内。</summary>
    /// <param name="length">这个条目解压后的字节数。</param>
    /// <param name="alreadyFound">之前是否已经解出过同名程序。</param>
    /// <exception cref="UpdateException">压缩包里有不止一个程序，或程序超过 <see cref="MaxExecutableBytes"/>。</exception>
    private static void CheckSize(long length, bool alreadyFound)
    {
        if (alreadyFound)
        {
            throw new UpdateException("压缩包里有不止一个程序，不安装");
        }

        if (length > MaxExecutableBytes)
        {
            throw new UpdateException("压缩包里的程序大得不正常，不安装");
        }
    }

    /// <summary>用解出来的新程序换掉旧程序。</summary>
    /// <param name="staged">解出来、试运行过的新程序。</param>
    /// <param name="target">正在用的程序；Windows 上它被改名成 <c>.old</c>（或 <c>.old1</c>…<c>.old9</c>），换不上时改回去。</param>
    /// <param name="windows">按 Windows 的方式替换（不能覆盖正在运行的程序，先给旧程序改名）。</param>
    /// <param name="move">改名 / 移动文件：源路径、目标路径、是否覆盖（测试里换成会失败的）。</param>
    /// <returns>Windows 上旧程序改名后的路径；其他系统为 null。</returns>
    /// <exception cref="UpdateException">
    /// Windows 上旁边的 <c>.old</c> 旧程序已经有 10 个，没有可用的备份名；
    /// 或者换上新程序失败、把旧程序改回去也失败（消息里有两次失败的原因和旧程序现在的位置，原始异常在 <see cref="Exception.InnerException"/>）。
    /// </exception>
    /// <exception cref="IOException">换上新程序失败，旧程序已经改回原来的名字。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限改名，旧程序没有被改动（或已经改回原来的名字）。</exception>
    internal static string? Replace(string staged, string target, bool windows, Action<string, string, bool> move)
    {
        ArgumentNullException.ThrowIfNull(move);
        if (!windows)
        {
            // 改名覆盖是原子的；正在运行的旧进程还用着旧文件，不受影响。
            move(staged, target, true);
            return null;
        }

        // Windows 不能覆盖正在运行的程序，但可以给它改名。
        CleanUpOldBinaries(target);
        var backup = BackupPaths(target).FirstOrDefault(p => !File.Exists(p))
            ?? throw new UpdateException($"{target} 旁边的旧程序太多了，删掉 {Path.GetFileName(target)}.old* 再试");
        move(target, backup, false);
        try
        {
            move(staged, target, false);
        }
        catch (Exception error)
        {
            try
            {
                move(backup, target, false);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                // 改不回去时不能让第二个错误盖掉第一个，也要告诉用户旧程序在哪：现在 target 位置上什么都没有。
                throw new UpdateException(
                    $"换上新程序失败（{error.Message}），把旧程序改回原来的名字也失败了（{rollback.Message}）。"
                    + $"旧程序现在是 {backup}，请手动把它改名回 {Path.GetFileName(target)}，否则节点启动不了",
                    error);
            }

            throw;
        }

        return backup;
    }

    /// <summary>尽力删除文件；文件被占用（例如 Windows 上正在运行的旧程序）或没有权限时忽略。</summary>
    /// <param name="path">要删的文件；不存在时什么也不做。</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 还在用（Windows 上正在运行的旧程序），下次再删。
        }
    }
}
