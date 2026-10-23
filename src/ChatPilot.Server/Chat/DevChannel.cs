using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChatPilot.Server.Options;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Chat;

public sealed record DevChatRequest(string? Text);

public sealed record DevChatResponse(IReadOnlyList<string> Replies);

public sealed record DevEventsResponse(IReadOnlyList<string> Events);

/// <summary>
/// 本机测试通道：用 HTTP 模拟主人发消息，用来在没有微信的情况下验收。
/// 默认关闭；开启后只接受回环地址，并且必须带 <c>X-ChatPilot-Dev-Token</c>。
/// </summary>
public sealed class DevChannel(IOptions<ServerOptions> options) : IChatChannel
{
    public const string TokenHeader = "X-ChatPilot-Dev-Token";
    private const int MaxQueued = 200;

    private readonly ConcurrentQueue<string> _events = new();

    public string Name => "测试通道";

    public bool IsReady => true;

    public Task SendToOwnerAsync(string text, CancellationToken cancellationToken)
    {
        _events.Enqueue(text);
        while (_events.Count > MaxQueued && _events.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

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

    public IReadOnlyList<string> Chunk(string reply) =>
        TextChunker.Split(reply, options.Value.WeChat.MaxChunkChars, options.Value.WeChat.MaxChunks);
}
