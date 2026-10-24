using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ChatPilot.Server.Options;
using ChatPilot.Shared.Net;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.WeChat;

/// <summary>iLink 接口出错（HTTP 失败或 ret 不为 0）。</summary>
public sealed class ILinkException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 腾讯 iLink（微信 ClawBot）HTTP 接口：二维码登录、长轮询收消息、发消息。
/// 协议参照腾讯开源的 openclaw-weixin 插件（MIT）。
/// </summary>
public sealed class ILinkClient(HttpClient http, IOptions<ServerOptions> options)
{
    private static readonly TimeSpan QrPollTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MediaTimeout = TimeSpan.FromSeconds(60);

    private WeChatOptions WeChat => options.Value.WeChat;

    /// <summary>获取登录二维码。</summary>
    /// <param name="localTokens">本机已有的 bot token（插件的做法，让腾讯识别重复绑定）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<QrCodeResponse> GetBotQrCodeAsync(IReadOnlyList<string> localTokens, CancellationToken cancellationToken)
    {
        var uri = Endpoint(WeChat.BaseUrl, $"ilink/bot/get_bot_qrcode?bot_type={Uri.EscapeDataString(WeChat.BotType)}");
        using var request = Post(uri, new { local_token_list = localTokens }, token: null);
        return await SendAsync<QrCodeResponse>(request, ApiTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>长轮询扫码状态。超时或网络错误都当作"等待中"，由调用方继续轮询。</summary>
    public async Task<QrStatusResponse> GetQrCodeStatusAsync(string baseUrl, string qrcode, string? verifyCode, CancellationToken cancellationToken)
    {
        var path = $"ilink/bot/get_qrcode_status?qrcode={Uri.EscapeDataString(qrcode)}";
        if (verifyCode is not null)
        {
            path += $"&verify_code={Uri.EscapeDataString(verifyCode)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(baseUrl, path));
        AddCommonHeaders(request);
        try
        {
            return await SendAsync<QrStatusResponse>(request, QrPollTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ILinkException)
        {
            return new QrStatusResponse("wait");
        }
    }

    /// <summary>长轮询新消息。客户端超时是正常情况，返回空结果和原游标。</summary>
    public async Task<GetUpdatesResponse> GetUpdatesAsync(string baseUrl, string token, string syncBuf, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = Post(Endpoint(baseUrl, "ilink/bot/getupdates"), new { get_updates_buf = syncBuf, base_info = BaseInfo() }, token);
        try
        {
            return await SendAsync<GetUpdatesResponse>(request, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ILinkException ex) when (ex.InnerException is TimeoutException)
        {
            return new GetUpdatesResponse(0, null, null, [], syncBuf, null);
        }
    }

    /// <summary>给用户发一条文字消息。</summary>
    /// <param name="baseUrl">iLink 接口地址。</param>
    /// <param name="token">bot token。</param>
    /// <param name="toUserId">接收人。</param>
    /// <param name="text">文字（调用方负责分段，单条不超过 4000 字）。</param>
    /// <param name="contextToken">对方最近一条消息的 context_token。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ILinkException">请求失败或 ret 不为 0。</exception>
    public async Task SendTextAsync(string baseUrl, string token, string toUserId, string text, string? contextToken, CancellationToken cancellationToken)
    {
        var message = new
        {
            msg = new
            {
                from_user_id = "",
                to_user_id = toUserId,
                client_id = ClientId(),
                message_type = ILinkConstants.MessageTypeBot,
                message_state = ILinkConstants.MessageStateFinish,
                item_list = new[] { new { type = ILinkConstants.ItemTypeText, text_item = new { text } } },
                context_token = contextToken,
            },
            base_info = BaseInfo(),
        };
        using var request = Post(Endpoint(baseUrl, "ilink/bot/sendmessage"), message, token);
        var response = await SendAsync<SendMessageResponse>(request, ApiTimeout, cancellationToken).ConfigureAwait(false);
        if (response.Ret is { } ret and not 0)
        {
            throw new ILinkException($"sendmessage 失败：ret={ret} {response.Errmsg}");
        }
    }

    /// <summary>
    /// 下载并解密主人发来的图片原图：优先用服务端给的完整地址，否则用 CDN 地址拼下载参数；
    /// 有密钥时按 AES-128-ECB（PKCS7）解密。做法和腾讯插件一致。
    /// </summary>
    /// <exception cref="ILinkException">缺少下载信息、地址不是微信的、下载失败、太大或解密失败。</exception>
    public async Task<byte[]> DownloadImageAsync(ImageItem image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var uri = ImageUri(image.Media);
        var key = ParseImageKey(image.Aeskey, image.Media?.AesKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MediaTimeout);
        byte[] data;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ILinkException($"下载图片返回 HTTP {(int)response.StatusCode}");
            }

            // 加密后会多出最多 16 字节的填充。
            data = await response.Content.ReadBoundedAsync(ProtocolInfo.MaxImageBytes + 16, deadline.Token).ConfigureAwait(false)
                ?? throw new ILinkException($"图片太大了（上限 {ProtocolInfo.MaxImageBytes / 1024 / 1024} MB）");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ILinkException("下载图片超时", new TimeoutException(ex.Message, ex));
        }
        catch (HttpRequestException ex)
        {
            throw new ILinkException($"下载图片失败：{ex.Message}", ex);
        }

        if (key is null)
        {
            return data;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.DecryptEcb(data, PaddingMode.PKCS7);
        }
        catch (CryptographicException ex)
        {
            throw new ILinkException("图片解密失败", ex);
        }
    }

    /// <summary>
    /// 图片的解密密钥：<c>aeskey</c>（十六进制）优先；否则 <c>media.aes_key</c> 的 base64，解出来是 16 字节就是密钥，
    /// 是 32 位十六进制字符串就再按十六进制解。都没有时返回 null（不加密）。
    /// </summary>
    /// <exception cref="ILinkException">密钥格式不对。</exception>
    internal static byte[]? ParseImageKey(string? hexKey, string? base64Key)
    {
        if (!string.IsNullOrEmpty(hexKey))
        {
            return hexKey.Length == 32 && hexKey.All(char.IsAsciiHexDigit)
                ? Convert.FromHexString(hexKey)
                : throw new ILinkException("图片密钥格式不对");
        }

        if (string.IsNullOrEmpty(base64Key))
        {
            return null;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new ILinkException("图片密钥格式不对", ex);
        }

        if (decoded.Length == 16)
        {
            return decoded;
        }

        return decoded.Length == 32 && decoded.All(b => char.IsAsciiHexDigit((char)b))
            ? Convert.FromHexString(System.Text.Encoding.ASCII.GetString(decoded))
            : throw new ILinkException("图片密钥格式不对");
    }

    /// <summary>
    /// 图片下载地址。服务端给的完整地址只接受 https 的微信域名（qq.com 或配置的 CDN），防止被诱导去访问别的地址。
    /// </summary>
    private Uri ImageUri(CdnMedia? media)
    {
        var cdn = new Uri(WeChat.CdnBaseUrl);
        if (!string.IsNullOrEmpty(media?.FullUrl))
        {
            return Uri.TryCreate(media.FullUrl, UriKind.Absolute, out var full) && full.Scheme == "https"
                && (full.Host == cdn.Host || full.Host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase))
                    ? full
                    : throw new ILinkException("图片地址不是微信的地址");
        }

        return !string.IsNullOrEmpty(media?.EncryptQueryParam)
            ? new Uri(WeChat.CdnBaseUrl.TrimEnd('/') + "/download?encrypted_query_param=" + Uri.EscapeDataString(media.EncryptQueryParam))
            : throw new ILinkException("图片消息里没有下载信息");
    }

    /// <summary>iLink-App-ClientVersion：0x00MMNNPP，例如 2.4.10 → 0x0002040A。</summary>
    internal static uint ClientVersion(string version)
    {
        var parts = version.Split('.').Select(p => uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        uint Part(int i) => i < parts.Length ? parts[i] & 0xFF : 0;
        return (Part(0) << 16) | (Part(1) << 8) | Part(2);
    }

    private object BaseInfo() => new { channel_version = WeChat.ChannelVersion, bot_agent = WeChat.BotAgent };

    private static Uri Endpoint(string baseUrl, string path) => new(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), path);

    private static string ClientId() =>
        $"chatpilot:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

    private HttpRequestMessage Post(Uri uri, object body, string? token)
    {
        // 先序列化成字节、带 Content-Length 发送：JsonContent 不预先算长度，会用分块传输（chunked），腾讯的网关对此返回 HTTP 412。
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, ILinkConstants.Json))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        AddCommonHeaders(request);
        request.Headers.TryAddWithoutValidation("AuthorizationType", "ilink_bot_token");
        // 插件的做法：随机 uint32 转十进制字符串再 base64。
        var uin = RandomNumberGenerator.GetInt32(int.MaxValue).ToString(CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("X-WECHAT-UIN", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(uin)));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());
        }

        return request;
    }

    private void AddCommonHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("iLink-App-Id", "bot");
        request.Headers.TryAddWithoutValidation("iLink-App-ClientVersion", ClientVersion(WeChat.ChannelVersion).ToString(CultureInfo.InvariantCulture));
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ILinkException($"{request.RequestUri?.AbsolutePath} 返回 HTTP {(int)response.StatusCode}");
            }

            return JsonSerializer.Deserialize<T>(body, ILinkConstants.Json)
                ?? throw new ILinkException($"{request.RequestUri?.AbsolutePath} 返回了空结果");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ILinkException($"{request.RequestUri?.AbsolutePath} 超时", new TimeoutException(ex.Message, ex));
        }
        catch (HttpRequestException ex)
        {
            throw new ILinkException($"{request.RequestUri?.AbsolutePath} 请求失败：{ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new ILinkException($"{request.RequestUri?.AbsolutePath} 返回的不是合法 JSON", ex);
        }
    }
}
