using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ChatPilot.Server.Options;
using ChatPilot.Shared.Security;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Security;

/// <summary>服务器的自签证书：首次使用时生成并保存到数据目录（权限 600），之后一直复用，指纹不变。</summary>
public sealed class CertificateProvider : IDisposable
{
    private readonly Lazy<X509Certificate2> _certificate;
    private readonly ILogger<CertificateProvider> _logger;

    public CertificateProvider(IOptions<ServerOptions> options, ILogger<CertificateProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        var path = Path.Combine(options.Value.ResolvedDataDir, "server.pfx");
        _certificate = new Lazy<X509Certificate2>(() => LoadOrCreate(path), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public X509Certificate2 Certificate => _certificate.Value;

    public string Fingerprint => CertificatePin.Compute(Certificate);

    public void Dispose()
    {
        if (_certificate.IsValueCreated)
        {
            _certificate.Value.Dispose();
        }
    }

    private X509Certificate2 LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            SecureFile.WriteAllBytes(path, CreatePkcs12());
            _logger.LogInformation("已生成服务器证书 {Path}", path);
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
    }

    private static byte[] CreatePkcs12()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ChatPilot Server", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return certificate.Export(X509ContentType.Pkcs12);
    }
}

/// <summary>/pair 提示里要用的服务器信息。</summary>
public sealed class ServerIdentity(IOptions<ServerOptions> options, CertificateProvider certificates)
{
    public bool TlsEnabled => options.Value.Tls.Enabled;

    public string? Fingerprint => TlsEnabled ? certificates.Fingerprint : null;

    public string? PublicUrl => options.Value.PublicUrl;
}
