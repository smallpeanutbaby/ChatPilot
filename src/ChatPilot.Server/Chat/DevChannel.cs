using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChatPilot.Server.Options;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Chat;

/// <summary><c>POST /dev/chat</c> 的请求体。</summary>
/// <param name="Text">模拟主人发的一条消息。</param>
public sealed record DevChatRequest(string? Text);

/// <summary><c>POST /dev/chat</c> 的响应。</summary>
/// <param name="Replies">回复（按微信的长度限制分好段）。</param>
public sealed record DevChatResponse(IReadOnlyList<string> Replies);

/// <summary><c>GET /dev/events</c> 的响应。</summary>
/// <param name="Events">上次取过之后新产生的主动通知。</param>
public sealed record DevEventsResponse(IReadOnlyList<string> Events);

/// <summary>
/// 本机测试通道：用 HTTP 模拟主人发消息，用来在没有微信的情况下验收。
/// 默认关闭；开启后只接受回环地址，并且必须带 <c>X-ChatPilot-Dev-Token</c>。
/// </summary>
public sealed class DevChannel(IOptions<ServerOptions> options) : IChatChannel
{
    /// <summary>携带测试通道令牌的请求头。</summary>
    public const string TokenHeader = "X-ChatPilot-Dev-Token";

    private const int MaxQueued = 200;

    private readonly ConcurrentQueue<string> _events = new();

    /// <inheritdoc />
    public string Name => "测试通道";

    /// <inheritdoc />
    public bool IsReady => true;

    /// <summary>把通知存进队列（最多保留 200 条），等 <c>GET /dev/events</c> 取走。</summary>
    public Task SendToOwnerAsync(string text, CancellationToken cancellationToken)
    {
        _events.Enqueue(text);
        while (_events.Count > MaxQueued && _events.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>取走所有排队的通知。</summary>
    public IReadOnlyList<string> Drain()
    {
        var drained = new List<string>();
        while (_events.TryDequeue(out var item))
        {
            drained.Add(item);
        }

        return drained;
    }

    /// <summary>检查请求是否来自本机并带了正确的令牌；不通过时返回对应的错误结果。</summary>
    public IResult? Authorize(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var expected = options.Value.DevChannel.Token ?? "";
        var provided = context.Request.Headers[TokenHeader].ToString();
        var ok = expected.Length > 0 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
        return ok ? null : Results.Unauthorized();
    }

    /// <summary>按微信的规则分段，让测试看到的和手机上收到的一样。</summary>
    public IReadOnlyList<string> Chunk(string reply) =>
        TextChunker.Split(reply, options.Value.WeChat.MaxChunkChars, options.Value.WeChat.MaxChunks);
}
