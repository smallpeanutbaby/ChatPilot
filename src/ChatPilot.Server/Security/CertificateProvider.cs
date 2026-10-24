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
    /// <summary>延迟加载的证书：没开 TLS 时不会去读写证书文件；多线程同时访问也只加载一次。</summary>
    private readonly Lazy<X509Certificate2> _certificate;
    private readonly ILogger<CertificateProvider> _logger;

    /// <summary>证书在第一次用到时才加载或生成。</summary>
    /// <param name="options">服务器配置，证书放在其中的数据目录下（<c>server.pfx</c>）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    public CertificateProvider(IOptions<ServerOptions> options, ILogger<CertificateProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        var path = Path.Combine(options.Value.ResolvedDataDir, "server.pfx");
        _certificate = new Lazy<X509Certificate2>(() => LoadOrCreate(path), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>服务器证书（含私钥）。</summary>
    public X509Certificate2 Certificate => _certificate.Value;

    /// <summary>证书指纹，节点配对时要用。</summary>
    public string Fingerprint => CertificatePin.Compute(Certificate);

    /// <summary>释放证书。</summary>
    public void Dispose()
    {
        if (_certificate.IsValueCreated)
        {
            _certificate.Value.Dispose();
        }
    }

    /// <summary>读取已保存的证书；文件不存在时先生成一张并以只有本人可读的权限写盘。</summary>
    /// <param name="path">证书文件路径。</param>
    /// <returns>含私钥的证书。</returns>
    private X509Certificate2 LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            SecureFile.WriteAllBytes(path, CreatePkcs12());
            _logger.LogInformation("已生成服务器证书 {Path}", path);
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
    }

    /// <summary>
    /// 生成一张 P-256 自签服务器证书，备用名称只有 <c>localhost</c> 和回环地址，有效期 10 年。
    /// 节点靠指纹而不是证书名字验证服务器，所以不需要和真实域名匹配。
    /// </summary>
    /// <returns>不加密码的 PKCS#12 数据（含私钥）。</returns>
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
/// <param name="options">服务器配置。</param>
/// <param name="certificates">服务器证书，用来取指纹。</param>
public sealed class ServerIdentity(IOptions<ServerOptions> options, CertificateProvider certificates)
{
    /// <summary>是否开启了 TLS。</summary>
    public bool TlsEnabled => options.Value.Tls.Enabled;

    /// <summary>证书指纹；没开 TLS 时为 null。</summary>
    public string? Fingerprint => TlsEnabled ? certificates.Fingerprint : null;

    /// <summary>配置的公网地址；没配置时为 null。</summary>
    public string? PublicUrl => options.Value.PublicUrl;
}
