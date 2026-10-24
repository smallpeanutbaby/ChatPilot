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
/// <param name="options">服务器配置，提供测试通道令牌和微信的分段规则。</param>
public sealed class DevChannel(IOptions<ServerOptions> options) : IChatChannel
{
    /// <summary>携带测试通道令牌的请求头。</summary>
    public const string TokenHeader = "X-ChatPilot-Dev-Token";

    /// <summary>最多保留多少条没取走的通知，超过就丢最旧的，没人来取也不会无限占内存。</summary>
    private const int MaxQueued = 200;

    /// <summary>等 <c>GET /dev/events</c> 取走的主动通知。</summary>
    private readonly ConcurrentQueue<string> _events = new();

    /// <inheritdoc />
    public string Name => "测试通道";

    /// <inheritdoc />
    public bool IsReady => true;

    /// <summary>把通知存进队列（最多保留 200 条），等 <c>GET /dev/events</c> 取走。</summary>
    /// <param name="text">通知内容。</param>
    /// <param name="cancellationToken">不使用，只是接口要求。</param>
    /// <returns>已完成的任务，入队不会等待。</returns>
    public Task SendToOwnerAsync(string text, CancellationToken cancellationToken)
    {
        _events.Enqueue(text);
        while (_events.Count > MaxQueued && _events.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>取走所有排队的通知。</summary>
    /// <returns>按产生顺序排列的通知；没有新通知时为空列表。</returns>
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
    /// <param name="context">当前请求。</param>
    /// <returns>通过时为 null；不是回环地址返回 403；令牌没配置、没带或不对返回 401。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> 为 null。</exception>
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
    /// <param name="reply">完整的回复文字。</param>
    /// <returns>分好的各段，段数不超过微信配置的上限，放不下的部分截掉并在最后一段加提示。</returns>
    public IReadOnlyList<string> Chunk(string reply) =>
        TextChunker.Split(reply, options.Value.WeChat.MaxChunkChars, options.Value.WeChat.MaxChunks);
}
