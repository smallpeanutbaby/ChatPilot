using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChatPilot.Shared.Security;

/// <summary>证书指纹：对整张证书（DER）做 SHA-256，格式 <c>sha256:十六进制小写</c>。</summary>
public static class CertificatePin
{
    /// <summary>指纹的前缀，表明哈希算法；规范化后的指纹一律带上它。</summary>
    private const string Prefix = "sha256:";

    /// <summary>计算证书指纹。</summary>
    /// <param name="certificate">要计算的证书。</param>
    /// <returns><c>sha256:</c> 加 64 位十六进制小写的指纹。</returns>
    public static string Compute(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>把用户输入的指纹规范化：去掉前缀、冒号和空格，转小写。格式不对返回 null。</summary>
    /// <param name="fingerprint">用户输入的指纹，可以带或不带 <c>sha256:</c> 前缀。</param>
    /// <returns>和 <see cref="Compute"/> 同样格式的指纹；为空或不是 64 位十六进制时为 null。</returns>
    public static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        var value = fingerprint.Trim();
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Prefix.Length..];
        }

        value = value.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? Prefix + value : null;
    }

    /// <summary>用定长时间比较证书和期望的指纹。</summary>
    /// <param name="certificate">对方出示的证书。</param>
    /// <param name="expectedFingerprint">期望的指纹（任意可被 <see cref="Normalize"/> 接受的写法）。</param>
    /// <returns>指纹一致时为 true；证书为 null 或期望的指纹格式不对时为 false。</returns>
    public static bool Matches(X509Certificate? certificate, string? expectedFingerprint)
    {
        var expected = Normalize(expectedFingerprint);
        if (certificate is null || expected is null)
        {
            return false;
        }

        var actual = Compute(certificate);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(expected));
    }
}
