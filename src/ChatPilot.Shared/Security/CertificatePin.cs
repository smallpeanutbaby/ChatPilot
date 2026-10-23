using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChatPilot.Shared.Security;

/// <summary>证书指纹：对整张证书（DER）做 SHA-256，格式 <c>sha256:十六进制小写</c>。</summary>
public static class CertificatePin
{
    private const string Prefix = "sha256:";

    public static string Compute(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>把用户输入的指纹规范化：去掉前缀、冒号和空格，转小写。格式不对返回 null。</summary>
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
