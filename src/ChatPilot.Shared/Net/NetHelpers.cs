using System.Net;
using System.Net.Sockets;

namespace ChatPilot.Shared.Net;

/// <summary>读 HTTP 响应体时限制大小（下载图片用）。</summary>
public static class BoundedContent
{
    /// <summary>
    /// 读完响应体；声明的长度或实际读到的字节超过 <paramref name="maxBytes"/> 时立即停下、返回 null（调用方决定报什么错）。
    /// </summary>
    /// <param name="content">要读的响应体。</param>
    /// <param name="maxBytes">允许的最大字节数。</param>
    /// <param name="cancellationToken">取消读取。</param>
    /// <returns>响应体的全部字节；超过上限时为 null。</returns>
    public static async Task<byte[]?> ReadBoundedAsync(this HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>IPv4 地址和网段的换算。</summary>
public static class Ipv4
{
    /// <summary>前缀长度对应的子网掩码，例如 24 → 255.255.255.0。</summary>
    /// <param name="prefixLength">子网前缀长度（0–32）。</param>
    /// <returns>掩码的整数形式（大端序）；前缀为 0 时为 0。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="prefixLength"/> 不在 0–32 之间。</exception>
    public static uint Mask(int prefixLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(prefixLength, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, 32);
        return prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
    }

    /// <summary>地址按大端序转成整数。</summary>
    /// <param name="address">IPv4 地址。</param>
    /// <returns>地址的整数形式，第一段在最高字节。</returns>
    /// <exception cref="ArgumentException"><paramref name="address"/> 不是 IPv4 地址。</exception>
    public static uint ToUInt32(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("不是 IPv4 地址", nameof(address));
        }

        var b = address.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    /// <summary>整数转回地址。</summary>
    /// <param name="value">大端序的整数形式，见 <see cref="ToUInt32"/>。</param>
    /// <returns>对应的 IPv4 地址。</returns>
    public static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    /// <summary>地址所在网段的网络号，例如 192.168.1.23/24 → 192.168.1.0。</summary>
    /// <param name="address">网段里的任一 IPv4 地址。</param>
    /// <param name="prefixLength">子网前缀长度（0–32）。</param>
    /// <returns>网络号的整数形式，用来比较两个地址是否在同一网段。</returns>
    public static uint Network(IPAddress address, int prefixLength) => ToUInt32(address) & Mask(prefixLength);

    /// <summary>网段的广播地址，例如 192.168.1.23/24 → 192.168.1.255。</summary>
    /// <param name="address">网段里的任一 IPv4 地址。</param>
    /// <param name="prefixLength">子网前缀长度（0–32）。</param>
    /// <returns>这个网段的广播地址。</returns>
    public static IPAddress Broadcast(IPAddress address, int prefixLength) =>
        FromUInt32(ToUInt32(address) | ~Mask(prefixLength));
}
