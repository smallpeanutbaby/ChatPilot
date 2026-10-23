using System.Net.Security;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Connection;

/// <summary>证书指纹校验：只认指纹一致的证书，不看证书链和域名（服务器用的是自签证书）。</summary>
public static class PinnedTransport
{
    public static RemoteCertificateValidationCallback Callback(string fingerprint) =>
        (_, certificate, _, _) => CertificatePin.Matches(certificate, fingerprint);

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
