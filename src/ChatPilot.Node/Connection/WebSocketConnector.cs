using System.Net;
using System.Net.WebSockets;

namespace ChatPilot.Node.Connection;

/// <summary>服务器拒绝了设备令牌（被撤销或无效）。重试没有意义，需要重新配对。</summary>
public sealed class NodeUnauthorizedException(string message) : Exception(message);

/// <summary>建立到服务器的 WebSocket 连接（测试里可以换成内存连接）。</summary>
public interface IWebSocketConnector
{
    /// <exception cref="NodeUnauthorizedException">服务器返回 401。</exception>
    Task<WebSocket> ConnectAsync(Uri uri, string token, string? fingerprint, CancellationToken cancellationToken);
}

public sealed class ClientWebSocketConnector : IWebSocketConnector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task<WebSocket> ConnectAsync(Uri uri, string token, string? fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.CollectHttpResponseDetails = true;
        if (uri.Scheme == "wss")
        {
            socket.Options.RemoteCertificateValidationCallback = PinnedTransport.Callback(
                fingerprint ?? throw new InvalidOperationException("wss 连接必须提供证书指纹"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            socket.Dispose();
            throw new NodeUnauthorizedException("服务器拒绝了设备令牌（可能已被 /unpair 撤销），请重新配对");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
