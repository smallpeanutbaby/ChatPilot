using System.Buffers;
using System.Net.WebSockets;

namespace ChatPilot.Shared.Protocol;

/// <summary>对方违反协议（消息太大、格式不对）。<see cref="CloseStatus"/> 是关闭连接时用的状态码。</summary>
/// <param name="message">错误说明。</param>
/// <param name="closeStatus">关闭连接时应当使用的状态码。</param>
/// <param name="payload">无法解析的原始消息；消息不完整或不是文本时为 null。</param>
public sealed class WireProtocolException(string message, WebSocketCloseStatus closeStatus, byte[]? payload = null) : Exception(message)
{
    /// <summary>关闭连接时应当使用的状态码。</summary>
    public WebSocketCloseStatus CloseStatus { get; } = closeStatus;

    /// <summary>无法解析的原始消息（只在消息完整但内容不合法时提供），用于判断是不是一条"不认识的指令"。</summary>
    public byte[]? Payload { get; } = payload;
}

/// <summary>按"一条 WebSocket 消息 = 一个 JSON"的约定收发，并限制单条消息大小。</summary>
public static class WebSocketMessaging
{
    /// <summary>收一条消息；对方正常关闭时返回 null。</summary>
    /// <param name="socket">要读的连接。</param>
    /// <param name="maxBytes">单条消息的字节上限，超过就当作违反协议。</param>
    /// <param name="cancellationToken">取消接收。</param>
    /// <returns>解析好的消息；对方发来关闭帧时为 null。</returns>
    /// <exception cref="WireProtocolException">消息超过上限、不是文本或不是合法的协议消息。</exception>
    public static async Task<WireMessage?> ReceiveAsync(WebSocket socket, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var payload = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new WireProtocolException("只接受文本消息", WebSocketCloseStatus.InvalidMessageType);
                }

                if (payload.Length + result.Count > maxBytes)
                {
                    throw new WireProtocolException($"消息超过 {maxBytes} 字节上限", WebSocketCloseStatus.MessageTooBig);
                }

                payload.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            if (!ProtocolJson.TryDeserialize(payload.GetBuffer().AsSpan(0, (int)payload.Length), out var message))
            {
                throw new WireProtocolException("无法解析的协议消息", WebSocketCloseStatus.InvalidPayloadData, payload.ToArray());
            }

            return message;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>发一条消息。同一个连接上的发送必须串行，所以要传入同一个 <paramref name="sendLock"/>。</summary>
    /// <param name="socket">要发送的连接。</param>
    /// <param name="sendLock">这个连接专用的发送锁。</param>
    /// <param name="message">要发送的消息。</param>
    /// <param name="cancellationToken">取消等锁和发送。</param>
    /// <exception cref="InvalidOperationException">序列化后超过 <see cref="ProtocolInfo.MaxFrameBytes"/>。</exception>
    public static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, WireMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(sendLock);
        var bytes = ProtocolJson.SerializeToUtf8(message);
        if (bytes.Length > ProtocolInfo.MaxFrameBytes)
        {
            throw new InvalidOperationException($"要发送的消息超过 {ProtocolInfo.MaxFrameBytes} 字节上限");
        }

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>尽力关闭连接，不抛异常。</summary>
    /// <param name="socket">要关闭的连接；已经不在打开状态时什么也不做。</param>
    /// <param name="status">关闭状态码。</param>
    /// <param name="reason">关闭原因，对方据此决定重连还是退出（见 <see cref="CloseReasons"/>）。</param>
    public static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // 连接已经断了，关不关都一样。
        }
    }
}
