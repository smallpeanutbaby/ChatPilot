using System.Net.Security;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Connection;

/// <summary>证书指纹校验：只认指纹一致的证书，不看证书链和域名（服务器用的是自签证书）。</summary>
public static class PinnedTransport
{
    /// <summary>只认指定指纹的证书校验回调。</summary>
    public static RemoteCertificateValidationCallback Callback(string fingerprint) =>
        (_, certificate, _, _) => CertificatePin.Matches(certificate, fingerprint);

    /// <summary>创建 HTTP 处理器；给了指纹时只认这个指纹的证书，没给时（本机 http）使用系统默认校验。</summary>
    public static HttpMessageHandler CreateHttpHandler(string? fingerprint)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (CertificatePin.Normalize(fingerprint) is { } pin)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = Callback(pin);
        }

        return handler;
    }
}
