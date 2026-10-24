using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ChatPilot.Tests.Support;

/// <summary>模拟腾讯 iLink 服务器：按脚本返回扫码状态，getupdates 长轮询从队列取，记录所有请求。</summary>
internal sealed class FakeILink : HttpMessageHandler
{
    private int _qrCount;

    public sealed record Request(HttpMethod Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers)
    {
        public JsonElement Json => JsonDocument.Parse(Body.Length == 0 ? "{}" : Body).RootElement;
    }

    public ConcurrentQueue<Request> Requests { get; } = new();

    public ConcurrentQueue<object> QrStatuses { get; } = new();

    public Channel<object> Updates { get; } = Channel.CreateUnbounded<object>();

    /// <summary>微信 CDN 上的文件（加密后的字节），按下载参数 encrypted_query_param 取。</summary>
    public ConcurrentDictionary<string, byte[]> Cdn { get; } = new(StringComparer.Ordinal);

    public int Count(string path) => Requests.Count(r => r.Uri.AbsolutePath == path);

    public IReadOnlyList<Request> Sent => Requests.Where(r => r.Uri.AbsolutePath == "/ilink/bot/sendmessage").ToList();

    public static object Message(string from, string text, string? contextToken = null, int type = 1) => new
    {
        message_id = 18446744073709551615UL,
        from_user_id = from,
        to_user_id = "bot",
        message_type = type,
        item_list = new[] { new { type = 1, text_item = new { text } } },
        context_token = contextToken,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 和真实的 iLink 一样：请求体没有 Content-Length（分块传输）时返回 412。要在读请求体之前判断，读过之后长度就已知了。
        var chunked = request.Content is not null && request.Content.Headers.ContentLength is null;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var uri = request.RequestUri ?? throw new InvalidOperationException("缺少地址");
        Requests.Enqueue(new Request(request.Method, uri, body, headers));
        if (chunked)
        {
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        }

        if (uri.AbsolutePath == "/c2c/download")
        {
            var param = Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('=', 2)[1]);
            return Cdn.TryGetValue(param, out var file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        object response = uri.AbsolutePath switch
        {
            "/ilink/bot/get_bot_qrcode" => NextQr(),
            "/ilink/bot/get_qrcode_status" => QrStatuses.TryDequeue(out var status) ? status : new { status = "wait" },
            "/ilink/bot/getupdates" => await Updates.Reader.ReadAsync(cancellationToken),
            "/ilink/bot/sendmessage" => new { ret = 0 },
            _ => new { ret = -1, errmsg = "unknown path" },
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response as string ?? JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
        };
    }

    private object NextQr()
    {
        var n = Interlocked.Increment(ref _qrCount);
        return new { qrcode = $"qr-{n}", qrcode_img_content = $"https://liteapp.weixin.qq.com/q/{n}" };
    }
}
