using System.Net;
using System.Net.WebSockets;

namespace ChatPilot.Node.Connection;

/// <summary>服务器拒绝了设备令牌（被撤销或无效）。重试没有意义，需要重新配对。</summary>
/// <param name="message">给用户看的说明。</param>
public sealed class NodeUnauthorizedException(string message) : Exception(message);

/// <summary>建立到服务器的 WebSocket 连接（测试里可以换成内存连接）。</summary>
public interface IWebSocketConnector
{
    /// <summary>连接服务器并带上设备令牌，返回已经打开的连接。</summary>
    /// <param name="uri">WebSocket 地址，见 <see cref="ServerEndpoint.WebSocketUri"/>。</param>
    /// <param name="token">设备令牌。</param>
    /// <param name="fingerprint">服务器证书指纹（wss 必填）。</param>
    /// <param name="cancellationToken">取消连接。</param>
    /// <returns>已经打开的连接，由调用方释放。</returns>
    /// <exception cref="NodeUnauthorizedException">服务器返回 401。</exception>
    Task<WebSocket> ConnectAsync(Uri uri, string token, string? fingerprint, CancellationToken cancellationToken);
}

/// <summary>用 <see cref="ClientWebSocket"/> 连接服务器：带设备令牌、证书指纹校验和心跳超时检测。</summary>
public sealed class ClientWebSocketConnector : IWebSocketConnector
{
    /// <summary>建立连接（含 TLS 握手和 HTTP 升级）最多等多久。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(Uri uri, string token, string? fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        // 定期发 ping 并要求回应；对方 20 秒没回（服务器宕机、网络断了）就判定连接已死，触发重连。
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
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
