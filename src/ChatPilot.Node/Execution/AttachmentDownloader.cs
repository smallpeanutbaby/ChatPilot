using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ChatPilot.Shared.Media;
using ChatPilot.Shared.Net;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Execution;

/// <summary>下载附件失败（网络、校验值不对、不是图片、写不进磁盘）。</summary>
/// <param name="message">给用户看的原因。</param>
public sealed class AttachmentException(string message) : Exception(message);

/// <summary>
/// 下载随指令发来的图片，存到 agent 工作目录下的 <c>.chatpilot/attachments/</c>，返回保存位置。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>凭设备令牌从服务器下载（和 WebSocket 一样走证书指纹校验），核对大小和 SHA-256。</item>
/// <item>只存图片：文件名和扩展名由节点按内容决定，不用任何外来的文件名。</item>
/// <item><c>.chatpilot/</c> 里放一个内容为 <c>*</c> 的 .gitignore，图片不会出现在 git 的改动里。工作目录写不进去、
/// 或者 <c>.chatpilot</c> 这几个路径是链接（可能指到 agent 目录外面）时，存到节点自己的目录。</item>
/// <item>超过 <see cref="RetentionPeriod"/> 的旧图片在下次保存时删掉；排队的指令没发出去就被清掉时，它带的图片也删掉。</item>
/// </list>
/// </remarks>
/// <param name="http">下载用的 HTTP 客户端（带服务器证书指纹校验和超时）。</param>
/// <param name="server">服务器地址（节点配置的 <c>serverUrl</c>）。</param>
/// <param name="token">设备令牌，下载时放在 <c>Authorization</c> 头里。</param>
/// <param name="fallbackDirectory">工作目录写不进去时存图片的节点自己的目录。</param>
public sealed partial class AttachmentDownloader(HttpClient http, Uri server, string token, string fallbackDirectory)
{
    /// <summary>存下的图片保留多久，更早的在下次保存时删掉（排队的指令最多等 48 小时，agent 之后也可能回头再看）。</summary>
    internal static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

    /// <summary>下载并保存所有附件，返回每张图片的绝对路径（顺序和传入的一样）。中途失败或取消时，已经保存的几张也删掉。</summary>
    /// <param name="attachments">附件。</param>
    /// <param name="agentDirectory">agent 的工作目录；为 null 或写不进去时存到节点自己的目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>每张图片的绝对路径；没有附件时为空列表。</returns>
    /// <exception cref="AttachmentException">任何一张下载或保存失败。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 被取消。</exception>
    public async Task<IReadOnlyList<string>> SaveAsync(IReadOnlyList<MediaRef> attachments, string? agentDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var directory = PrepareDirectory(agentDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var saved = new List<string>();
        var complete = false;
        try
        {
            for (var i = 0; i < attachments.Count; i++)
            {
                var data = await DownloadAsync(attachments[i], cancellationToken).ConfigureAwait(false);
                var extension = ImageFormat.DetectExtension(data) ?? throw new AttachmentException("收到的附件不是图片");
                var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
                var path = Path.Combine(directory, $"{stamp}-{i + 1}-{suffix}.{extension}");

                // 先记下再写：写到一半失败的文件也要删掉。
                saved.Add(path);
                try
                {
                    await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new AttachmentException($"图片保存失败：{ex.Message}");
                }
            }

            complete = true;
            return saved;
        }
        finally
        {
            if (!complete)
            {
                DeleteQuietly(saved);
            }
        }
    }

    /// <summary>
    /// 删掉已经保存的图片（指令没交出去、排队的指令被清掉时用）；删不掉也不报错。
    /// 只删文件名是节点自己起的图片：路径可能来自磁盘上的排队记录，不能凭它删别的文件。
    /// </summary>
    /// <param name="paths">要删的图片路径。</param>
    public static void DeleteQuietly(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths.Where(IsSavedImage))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 留一个没用的文件不影响什么。
            }
        }
    }

    /// <summary>把图片的保存位置附在指令后面，让 agent 自己去看。</summary>
    /// <param name="text">原来的指令。</param>
    /// <param name="paths">图片的保存位置。</param>
    /// <returns>附上图片路径的指令；没有图片时原样返回。</returns>
    public static string AppendToPrompt(string text, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Count == 0
            ? text
            : text + "\n\n（主人从手机发来了图片，请先查看：\n" + string.Join("\n", paths) + "）";
    }

    /// <summary>从服务器下载一张图片，先检查附件信息，下载后核对大小和 SHA-256。</summary>
    /// <param name="media">服务器发来的附件信息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>核对过的图片内容。</returns>
    /// <exception cref="AttachmentException">附件信息不合法、下载失败或超时、大小或校验值不对。</exception>
    private async Task<byte[]> DownloadAsync(MediaRef media, CancellationToken cancellationToken)
    {
        if (media.Size is <= 0 or > ProtocolInfo.MaxImageBytes || media.Id.Length is 0 or > 64 || !media.Id.All(char.IsAsciiLetterOrDigit))
        {
            throw new AttachmentException("附件信息不合法");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, MediaUri(server, media.Id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        byte[] data;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AttachmentException($"从服务器下载图片失败（HTTP {(int)response.StatusCode}），可能已经过期，请重新发一次图片");
            }

            if (response.Content.Headers.ContentLength is { } length && length != media.Size)
            {
                throw new AttachmentException("下载的图片大小不对");
            }

            data = await response.Content.ReadBoundedAsync(media.Size, cancellationToken).ConfigureAwait(false)
                ?? throw new AttachmentException("下载的图片大小不对");
        }
        catch (HttpRequestException ex)
        {
            throw new AttachmentException($"从服务器下载图片失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AttachmentException("从服务器下载图片超时");
        }

        if (data.Length != media.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), media.Sha256, StringComparison.Ordinal))
        {
            throw new AttachmentException("下载的图片校验值不对");
        }

        return data;
    }

    /// <summary>
    /// 优先存到 agent 工作目录（agent 读它不需要额外授权），写不进去或者路上有链接时存到节点自己的目录。
    /// 顺手删掉保存目录里超过 <see cref="RetentionPeriod"/> 的旧图片。
    /// </summary>
    /// <param name="agentDirectory">agent 的工作目录；为 null、不是绝对路径或不存在时直接用节点自己的目录。</param>
    /// <returns>建好的保存目录。</returns>
    /// <exception cref="AttachmentException">两个目录都写不进去。</exception>
    private string PrepareDirectory(string? agentDirectory)
    {
        if (agentDirectory is not null && Path.IsPathFullyQualified(agentDirectory) && Directory.Exists(agentDirectory))
        {
            try
            {
                if (PrepareInside(agentDirectory) is { } inside)
                {
                    RemoveExpired(inside);
                    return inside;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 退回节点自己的目录。
            }
        }

        try
        {
            SecureFile.EnsurePrivateDirectory(fallbackDirectory);
            RemoveExpired(fallbackDirectory);
            return fallbackDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AttachmentException($"找不到能保存图片的目录：{ex.Message}");
        }
    }

    /// <summary>
    /// 在 agent 工作目录下建好 <c>.chatpilot/attachments/</c> 和内容为 <c>*</c> 的 <c>.chatpilot/.gitignore</c>。
    /// 这几个路径有一个是链接（比如克隆下来的仓库里带了 <c>.chatpilot -> ../别的仓库</c>）时什么都不写：
    /// 跟着链接写会把 .gitignore（让那边的东西全被 git 忽略）和图片写到 agent 目录外面去。
    /// </summary>
    /// <param name="agentDirectory">agent 的工作目录（已经确认存在）。</param>
    /// <returns>保存目录；路上有链接时为 null。</returns>
    /// <exception cref="IOException">建目录或写 .gitignore 失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有写权限。</exception>
    private static string? PrepareInside(string agentDirectory)
    {
        var root = Path.Combine(agentDirectory, ".chatpilot");
        var ignore = Path.Combine(root, ".gitignore");
        var attachments = Path.Combine(root, "attachments");
        if (IsLink(root))
        {
            return null;
        }

        Directory.CreateDirectory(root);
        if (IsLink(root) || IsLink(ignore) || IsLink(attachments))
        {
            return null;
        }

        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, "*\n");
        }

        Directory.CreateDirectory(attachments);
        return attachments;
    }

    /// <summary>路径是不是链接：符号链接（包括指向不存在的目标的）、Windows 的目录联接等重解析点。</summary>
    /// <param name="path">要检查的路径。</param>
    /// <returns>是链接为 true；路径不存在或是普通文件、目录时为 false。</returns>
    /// <exception cref="IOException">读不了这个路径的属性。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限读这个路径的属性。</exception>
    private static bool IsLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            return true;
        }

        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>删掉保存目录里超过 <see cref="RetentionPeriod"/> 的图片（只删文件名是节点自己起的）；出错不影响这次保存。</summary>
    /// <param name="directory">保存目录。</param>
    private static void RemoveExpired(string directory)
    {
        List<string> expired;
        try
        {
            var cutoff = DateTime.UtcNow - RetentionPeriod;
            expired = Directory.EnumerateFiles(directory)
                .Where(path => IsSavedImage(path) && File.GetLastWriteTimeUtc(path) < cutoff)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        DeleteQuietly(expired);
    }

    /// <summary>是不是节点自己存下的图片：文件名形如 <c>20260101-093000-1-a1b2c3.png</c>（见 <see cref="SaveAsync"/>）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件名符合节点起名的格式为 true。</returns>
    internal static bool IsSavedImage(string path) => SavedImageName().IsMatch(Path.GetFileName(path));

    /// <summary>节点给图片起的文件名：时间戳、序号、6 位随机十六进制和识别出的扩展名。</summary>
    /// <returns>匹配整个文件名的正则。</returns>
    [GeneratedRegex(@"^\d{8}-\d{6}-\d{1,2}-[0-9a-f]{6}\.(?:png|jpg|gif|webp|bmp)\z", RegexOptions.CultureInvariant)]
    private static partial Regex SavedImageName();

    /// <summary>下载地址：服务器地址（可以带子路径）加 <see cref="ProtocolInfo.MediaPath"/> 和 ID。</summary>
    /// <param name="server">服务器地址。</param>
    /// <param name="id">附件 ID。</param>
    /// <returns>去掉查询串和片段的下载地址。</returns>
    internal static Uri MediaUri(Uri server, string id)
    {
        var basePath = server.AbsolutePath.TrimEnd('/');
        return new UriBuilder(server) { Path = basePath + ProtocolInfo.MediaPath + id, Query = "", Fragment = "" }.Uri;
    }
}
