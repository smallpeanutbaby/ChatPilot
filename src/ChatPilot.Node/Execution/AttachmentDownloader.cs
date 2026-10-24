using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ChatPilot.Shared.Media;
using ChatPilot.Shared.Net;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Execution;

/// <summary>下载附件失败（网络、校验值不对、不是图片、写不进磁盘）。</summary>
public sealed class AttachmentException(string message) : Exception(message);

/// <summary>
/// 下载随指令发来的图片，存到 agent 工作目录下的 <c>.chatpilot/attachments/</c>，返回保存位置。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>凭设备令牌从服务器下载（和 WebSocket 一样走证书指纹校验），核对大小和 SHA-256。</item>
/// <item>只存图片：文件名和扩展名由节点按内容决定，不用任何外来的文件名。</item>
/// <item><c>.chatpilot/</c> 里放一个内容为 <c>*</c> 的 .gitignore，图片不会出现在 git 的改动里。工作目录写不进去时存到节点自己的目录。</item>
/// </list>
/// </remarks>
public sealed class AttachmentDownloader(HttpClient http, Uri server, string token, string fallbackDirectory)
{
    /// <summary>下载并保存所有附件，返回每张图片的绝对路径（顺序和传入的一样）。</summary>
    /// <param name="attachments">附件。</param>
    /// <param name="agentDirectory">agent 的工作目录；为 null 或写不进去时存到节点自己的目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="AttachmentException">任何一张下载或保存失败。</exception>
    public async Task<IReadOnlyList<string>> SaveAsync(IReadOnlyList<MediaRef> attachments, string? agentDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var directory = PrepareDirectory(agentDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var saved = new List<string>();
        for (var i = 0; i < attachments.Count; i++)
        {
            var data = await DownloadAsync(attachments[i], cancellationToken).ConfigureAwait(false);
            var extension = ImageFormat.DetectExtension(data) ?? throw new AttachmentException("收到的附件不是图片");
            var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
            var path = Path.Combine(directory, $"{stamp}-{i + 1}-{suffix}.{extension}");
            try
            {
                await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AttachmentException($"图片保存失败：{ex.Message}");
            }

            saved.Add(path);
        }

        return saved;
    }

    /// <summary>删掉已经保存的图片（指令没交出去时用）；删不掉也不报错。</summary>
    public static void DeleteQuietly(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
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
    public static string AppendToPrompt(string text, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Count == 0
            ? text
            : text + "\n\n（主人从手机发来了图片，请先查看：\n" + string.Join("\n", paths) + "）";
    }

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

    /// <summary>优先存到 agent 工作目录（agent 读它不需要额外授权），写不进去时存到节点自己的目录。</summary>
    private string PrepareDirectory(string? agentDirectory)
    {
        if (agentDirectory is not null && Path.IsPathFullyQualified(agentDirectory) && Directory.Exists(agentDirectory))
        {
            try
            {
                var root = Path.Combine(agentDirectory, ".chatpilot");
                Directory.CreateDirectory(root);
                var ignore = Path.Combine(root, ".gitignore");
                if (!File.Exists(ignore))
                {
                    File.WriteAllText(ignore, "*\n");
                }

                var attachments = Path.Combine(root, "attachments");
                Directory.CreateDirectory(attachments);
                return attachments;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 退回节点自己的目录。
            }
        }

        try
        {
            SecureFile.EnsurePrivateDirectory(fallbackDirectory);
            return fallbackDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AttachmentException($"找不到能保存图片的目录：{ex.Message}");
        }
    }

    /// <summary>下载地址：服务器地址（可以带子路径）加 <see cref="ProtocolInfo.MediaPath"/> 和 ID。</summary>
    internal static Uri MediaUri(Uri server, string id)
    {
        var basePath = server.AbsolutePath.TrimEnd('/');
        return new UriBuilder(server) { Path = basePath + ProtocolInfo.MediaPath + id, Query = "", Fragment = "" }.Uri;
    }
}
