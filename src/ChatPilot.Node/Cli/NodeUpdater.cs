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

namespace ChatPilot.Node.Cli;

/// <summary>升级失败，<see cref="Exception.Message"/> 是给用户看的原因。</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>版本号：主.次.修订，可带预发布后缀（例如 <c>0.2.0-beta.1</c>）。</summary>
/// <param name="Major">主版本。</param>
/// <param name="Minor">次版本。</param>
/// <param name="Patch">修订号。</param>
/// <param name="Prerelease">预发布后缀；正式版为 null。</param>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease)
{
    /// <summary>解析 <c>0.1.0</c>、<c>v0.1.0</c>、<c>0.2.0-beta.1</c>、<c>0.1.0+构建信息</c>。</summary>
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
            _ => string.CompareOrdinal(Prerelease, other.Prerelease) > 0,
        };
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (Prerelease is null ? "" : "-" + Prerelease);

    [GeneratedRegex(@"^v?(?<major>\d{1,6})\.(?<minor>\d{1,6})\.(?<patch>\d{1,6})(?:-(?<pre>[0-9A-Za-z.-]{1,64}))?(?:\+[0-9A-Za-z.-]*)?\z", RegexOptions.CultureInvariant)]
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

    private const string ChecksumFile = "SHA256SUMS.txt";
    private const long MaxArchiveBytes = 300L * 1024 * 1024;
    private const long MaxExecutableBytes = 500L * 1024 * 1024;

    private static bool IsWindowsRuntime(string rid) => rid.StartsWith("win-", StringComparison.Ordinal);

    /// <summary>这个平台的压缩包名，例如 <c>chatpilot-node-win-x64.zip</c>。</summary>
    public static string AssetName(string rid) => $"chatpilot-node-{rid}{(IsWindowsRuntime(rid) ? ".zip" : ".tar.gz")}";

    /// <summary>压缩包里的程序名。</summary>
    public static string ExecutableName(string rid) => IsWindowsRuntime(rid) ? "chatpilot-node.exe" : "chatpilot-node";

    /// <summary>正在运行的节点的版本。</summary>
    public static ReleaseVersion CurrentVersion() =>
        ReleaseVersion.Parse(typeof(NodeUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new ReleaseVersion(0, 0, 0, null);

    /// <summary>访问 GitHub 用的客户端（走系统代理）。</summary>
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"chatpilot-node/{CurrentVersion()}");
        return client;
    }

    /// <summary>查最新的正式版本。</summary>
    /// <exception cref="UpdateException">查不到。</exception>
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

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
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

    /// <summary>下载、核对、试运行，然后替换程序。</summary>
    /// <exception cref="UpdateException">任何一步不通过；这时正在用的程序没有被改动。</exception>
    public async Task InstallAsync(NodeRelease release, CancellationToken cancellationToken)
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

        var sums = Encoding.UTF8.GetString(await DownloadAsync(checksumUrl, 1024 * 1024, cancellationToken).ConfigureAwait(false));
        var expected = ParseChecksums(sums).GetValueOrDefault(assetName)
            ?? throw new UpdateException($"{ChecksumFile} 里没有 {assetName} 的校验值，不安装");
        var archive = await DownloadAsync(assetUrl, MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(archive)), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("下载的文件和校验值对不上（可能损坏或被篡改），没有安装");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        var staged = Path.Combine(directory, ".chatpilot-node.new" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        try
        {
            ExtractExecutable(archive, staged);
            if (!await tryRun(staged, cancellationToken).ConfigureAwait(false))
            {
                throw new UpdateException("新版本在这台电脑上运行不起来，没有替换");
            }

            Replace(staged, executablePath);
        }
        finally
        {
            TryDelete(staged);
        }
    }

    /// <summary>删掉 Windows 上替换时留下的旧程序（<c>.old</c>）；还在运行、删不掉的留到下次。</summary>
    public static void CleanUpOldBinaries(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var old in Directory.EnumerateFiles(directory, Path.GetFileName(executablePath) + ".old*"))
        {
            TryDelete(old);
        }
    }

    /// <summary>运行一次 <c>程序 help</c>，30 秒内正常退出就算能运行。</summary>
    public static async Task<bool> TryRunAsync(string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("help");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>解析 <c>sha256sum</c> 的输出：每行"校验值 两个空格 文件名"。</summary>
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

    private async Task<byte[]> DownloadAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"下载 {url.Segments[^1]} 失败：返回 {(int)response.StatusCode}");
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new UpdateException($"{url.Segments[^1]} 太大了，不下载");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new UpdateException($"{url.Segments[^1]} 太大了，不下载");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"下载 {url.Segments[^1]} 中断了：{ex.Message}");
        }
    }

    /// <summary>从压缩包里解出程序（压缩包里应该只有一个同名程序）。</summary>
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
    private static void Replace(string staged, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            // 改名覆盖是原子的；正在运行的旧进程还用着旧文件，不受影响。
            File.Move(staged, target, overwrite: true);
            return;
        }

        // Windows 不能覆盖正在运行的程序，但可以给它改名。
        CleanUpOldBinaries(target);
        var backup = Enumerable.Range(0, 10).Select(i => target + ".old" + (i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture)))
            .FirstOrDefault(p => !File.Exists(p))
            ?? throw new UpdateException($"{target} 旁边的旧程序太多了，删掉 {Path.GetFileName(target)}.old* 再试");
        File.Move(target, backup);
        try
        {
            File.Move(staged, target);
        }
        catch
        {
            File.Move(backup, target);
            throw;
        }
    }

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
