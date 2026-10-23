using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ChatPilot.Server.Options;
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
