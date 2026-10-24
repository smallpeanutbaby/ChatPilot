using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Connection;

/// <summary>服务器地址的解析和安全检查。</summary>
public static class ServerEndpoint
{
    /// <summary>
    /// https 必须提供证书指纹（自签证书靠指纹校验）；http 只允许本机回环地址（开发用）。
    /// 返回错误说明，没问题时返回 null。
    /// </summary>
    /// <param name="server">服务器地址。</param>
    /// <param name="fingerprint">服务器证书指纹（https 必填）。</param>
    /// <returns>给用户看的错误说明；地址和指纹搭配没问题时为 null。</returns>
    public static string? CheckTransportSecurity(Uri server, string? fingerprint)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Scheme switch
        {
            "https" when CertificatePin.Normalize(fingerprint) is null => "https 服务器必须提供证书指纹（--fingerprint sha256:…）",
            "http" when !IsLoopback(server) => "不加密的 http 只能连本机（127.0.0.1 / localhost），远程服务器请用 https",
            "http" or "https" => null,
            _ => "服务器地址必须是 http:// 或 https://",
        };
    }

    /// <summary>是不是本机地址（127.0.0.1、::1、localhost）。</summary>
    /// <param name="uri">要检查的地址。</param>
    /// <returns>主机是回环地址或 <c>localhost</c>（不区分大小写）时为 true。</returns>
    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>节点连接用的 WebSocket 地址（https 对应 wss，http 对应 ws）。</summary>
    /// <param name="server">服务器地址，可以带子路径。</param>
    /// <returns>指向 <see cref="ProtocolInfo.NodeWebSocketPath"/> 的 ws / wss 地址。</returns>
    public static Uri WebSocketUri(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var builder = new UriBuilder(Combine(server, ProtocolInfo.NodeWebSocketPath))
        {
            Scheme = server.Scheme == "https" ? "wss" : "ws",
        };
        return builder.Uri;
    }

    /// <summary>配对接口地址。</summary>
    /// <param name="server">服务器地址，可以带子路径。</param>
    /// <returns>指向 <see cref="ProtocolInfo.PairPath"/> 的地址。</returns>
    public static Uri PairUri(Uri server) => Combine(server, ProtocolInfo.PairPath);

    /// <summary>健康检查地址。</summary>
    /// <param name="server">服务器地址，可以带子路径。</param>
    /// <returns>指向 <see cref="ProtocolInfo.HealthPath"/> 的地址。</returns>
    public static Uri HealthUri(Uri server) => Combine(server, ProtocolInfo.HealthPath);

    /// <summary>支持服务器挂在反向代理的子路径下，例如 https://example.com/chatpilot。</summary>
    /// <param name="server">服务器地址；保留它的路径，去掉查询串和片段。</param>
    /// <param name="path">以 <c>/</c> 开头的协议路径，接在服务器路径后面。</param>
    /// <returns>拼好的地址。</returns>
    private static Uri Combine(Uri server, string path)
    {
        ArgumentNullException.ThrowIfNull(server);
        var basePath = server.AbsolutePath.TrimEnd('/');
        return new UriBuilder(server) { Path = basePath + path, Query = "", Fragment = "" }.Uri;
    }
}
