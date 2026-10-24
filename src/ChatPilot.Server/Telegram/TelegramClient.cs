using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatPilot.Server.Options;
using ChatPilot.Shared.Net;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Telegram;

/// <summary>Telegram Bot API 出错。<see cref="StatusCode"/> 是 HTTP 状态码（401 token 无效，409 有别的程序在收这个 bot 的消息）；网络错误时为 null。</summary>
public sealed class TelegramException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>HTTP 状态码；网络错误、超时时为 null。</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>一条 update（只关心私聊消息）。</summary>
/// <param name="UpdateId">update 编号，下次请求时 offset 传它加一，表示这条已经处理过。</param>
/// <param name="Message">消息；其他类型的 update 为 null。</param>
public sealed record TelegramUpdate(long UpdateId, TelegramMessage? Message);

/// <summary>一条消息。</summary>
/// <param name="MessageId">消息编号。</param>
/// <param name="From">发送人。</param>
/// <param name="Chat">所在的对话。</param>
/// <param name="Text">文字。</param>
/// <param name="Caption">图片的说明文字。</param>
/// <param name="Photo">图片的各个尺寸（从小到大）。</param>
public sealed record TelegramMessage(
    long MessageId,
    TelegramUser? From,
    TelegramChat Chat,
    string? Text,
    string? Caption,
    IReadOnlyList<TelegramPhotoSize>? Photo);

/// <summary>Telegram 用户。</summary>
/// <param name="Id">用户 ID。</param>
public sealed record TelegramUser(long Id);

/// <summary>对话。</summary>
/// <param name="Id">对话 ID；私聊时等于对方的用户 ID。</param>
/// <param name="Type">private、group……</param>
public sealed record TelegramChat(long Id, string? Type);

/// <summary>图片的一个尺寸。</summary>
/// <param name="FileId">文件 ID，用 getFile 换下载路径。</param>
/// <param name="FileSize">字节数（可能没有）。</param>
public sealed record TelegramPhotoSize(string FileId, long? FileSize);

/// <summary>
/// Telegram Bot API：长轮询收消息、发文字、下载图片。bot token 是地址的一部分，所以这里出的错误信息里一律不带地址。
/// </summary>
public sealed class TelegramClient(HttpClient http, IOptions<ServerOptions> options)
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MediaTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private TelegramOptions Settings => options.Value.Telegram;

    private sealed record Envelope<T>(bool Ok, T? Result, string? Description);

    private sealed record TelegramFile(string FileId, long? FileSize, string? FilePath);

    /// <summary>长轮询新消息。</summary>
    /// <param name="offset">从这个 update_id 开始取（之前的视为已处理）；为 null 时从头取。</param>
    /// <param name="timeout">长轮询等多久。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long? offset, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["timeout"] = (int)timeout.TotalSeconds,
            ["allowed_updates"] = new[] { "message" },
        };
        if (offset is { } value)
        {
            body["offset"] = value;
        }

        return await CallAsync<List<TelegramUpdate>>("getUpdates", body, timeout + ApiTimeout, cancellationToken).ConfigureAwait(false) ?? [];
    }

    /// <summary>发一条纯文字消息（调用方负责分段）。</summary>
    public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken) =>
        CallAsync<JsonElement>(
            "sendMessage",
            new Dictionary<string, object?> { ["chat_id"] = chatId, ["text"] = text, ["link_preview_options"] = new { is_disabled = true } },
            ApiTimeout,
            cancellationToken);

    /// <summary>下载一张图片（不超过 <see cref="ProtocolInfo.MaxImageBytes"/>）。</summary>
    /// <exception cref="TelegramException">下载失败或太大。</exception>
    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
    {
        var file = await CallAsync<TelegramFile>("getFile", new Dictionary<string, object?> { ["file_id"] = fileId }, ApiTimeout, cancellationToken).ConfigureAwait(false);
        if (file?.FilePath is not { Length: > 0 } path || path.Contains("..", StringComparison.Ordinal))
        {
            throw new TelegramException("getFile 没有返回下载路径");
        }

        if (file.FileSize > ProtocolInfo.MaxImageBytes)
        {
            throw new TelegramException($"图片太大了（上限 {ProtocolInfo.MaxImageBytes / 1024 / 1024} MB）");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Settings.ApiBaseUrl.TrimEnd('/')}/file/bot{Settings.BotToken}/{path}"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MediaTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TelegramException($"下载图片返回 HTTP {(int)response.StatusCode}", (int)response.StatusCode);
            }

            return await response.Content.ReadBoundedAsync(ProtocolInfo.MaxImageBytes, deadline.Token).ConfigureAwait(false)
                ?? throw new TelegramException($"图片太大了（上限 {ProtocolInfo.MaxImageBytes / 1024 / 1024} MB）");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TelegramException("下载图片超时", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TelegramException("下载图片失败：" + ex.HttpRequestError, inner: ex);
        }
    }

    /// <summary>创建访问 Telegram 用的 HTTP 客户端（配置了代理时走代理）。</summary>
    public static HttpClient CreateHttpClient(TelegramOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (settings.Proxy is { Length: > 0 } proxy)
        {
            handler.Proxy = new WebProxy(new Uri(proxy));
            handler.UseProxy = true;
        }

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>调用一个 Bot API 方法；HTTP 失败或 ok 为 false 时抛出（信息里不带地址，地址里有 token）。</summary>
    private async Task<T?> CallAsync<T>(string method, object body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Settings.ApiBaseUrl.TrimEnd('/')}/bot{Settings.BotToken}/{method}"))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            Envelope<T>? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<Envelope<T>>(text, Json);
            }
            catch (JsonException)
            {
                envelope = null;
            }

            if (!response.IsSuccessStatusCode || envelope is not { Ok: true })
            {
                throw new TelegramException(
                    $"{method} 失败：HTTP {(int)response.StatusCode} {Shared.Text.TextChunker.Truncate(envelope?.Description ?? "", 200)}",
                    (int)response.StatusCode);
            }

            return envelope.Result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TelegramException($"{method} 超时", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            // 不用 ex.Message：有的实现会把请求地址（含 token）写进去。
            throw new TelegramException($"{method} 请求失败（{ex.HttpRequestError}）", inner: ex);
        }
    }
}
