using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ChatPilot.Shared;
using ChatPilot.Shared.Net;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Node.Connection;

/// <summary>
/// 本机网卡信息（握手时告诉服务器，用来以后唤醒这台电脑），以及在局域网里广播 Wake-on-LAN 唤醒包。
/// </summary>
public static class LocalNetwork
{
    /// <summary>唤醒包发到的 UDP 端口（9 是惯例，7 也常用）。</summary>
    private static readonly int[] WakePorts = [9, 7];

    /// <summary>已启用的有线 / 无线网卡：MAC 地址和 IPv4 网段，最多 <see cref="ProtocolInfo.MaxWakeAddresses"/> 块。读取失败时返回空列表。</summary>
    /// <returns>跳过回环、隧道、PPP 和没有有效 MAC 的网卡；没有 IPv4 地址的网卡 <see cref="NetworkInterfaceInfo.Address"/> 为 null。</returns>
    public static IReadOnlyList<NetworkInterfaceInfo> Describe()
    {
        try
        {
            var result = new List<NetworkInterfaceInfo>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                    || FormatMac(nic.GetPhysicalAddress()) is not { } mac)
                {
                    continue;
                }

                var ipv4 = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address));
                result.Add(new NetworkInterfaceInfo(mac, ipv4?.Address.ToString(), ipv4?.PrefixLength ?? 0));
                if (result.Count >= ProtocolInfo.MaxWakeAddresses)
                {
                    break;
                }
            }

            return result;
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>6 字节、不全为 0 的物理地址格式化成 <c>AA:BB:CC:DD:EE:FF</c>；其他返回 null。</summary>
    /// <param name="address">网卡的物理地址。</param>
    /// <returns>大写、冒号分隔的 MAC 地址；长度不是 6 字节或全为 0 时为 null。</returns>
    internal static string? FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 6 && bytes.Any(b => b != 0)
            ? string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
            : null;
    }

    /// <summary>Wake-on-LAN 唤醒包：6 个 0xFF 加 16 遍 MAC 地址。</summary>
    /// <param name="mac">MAC 地址，格式见 <see cref="Validation.IsMacAddress"/>。</param>
    /// <returns>102 字节的唤醒包。</returns>
    /// <exception cref="ArgumentException"><paramref name="mac"/> 格式不对。</exception>
    internal static byte[] MagicPacket(string mac)
    {
        if (!Validation.IsMacAddress(mac))
        {
            throw new ArgumentException("MAC 地址格式不对", nameof(mac));
        }

        var address = Convert.FromHexString(mac.Replace(":", "", StringComparison.Ordinal));
        var packet = new byte[6 + 16 * 6];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
        {
            address.CopyTo(packet, 6 + i * 6);
        }

        return packet;
    }

    /// <summary>
    /// 向全局广播地址和每块网卡所在网段的广播地址发唤醒包。单个地址发送失败不影响其他地址。
    /// </summary>
    /// <param name="macs">要唤醒的 MAC 地址；每个都发到每个广播地址的 <see cref="WakePorts"/> 端口。</param>
    /// <param name="cancellationToken">取消发送。</param>
    /// <returns>成功发出的包数。</returns>
    /// <exception cref="ArgumentException">某个 MAC 地址格式不对。</exception>
    public static async Task<int> SendWakeAsync(IReadOnlyList<string> macs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macs);
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var info in Describe())
        {
            if (info.Address is { } text && IPAddress.TryParse(text, out var address) && info.PrefixLength is > 0 and < 32)
            {
                targets.Add(Ipv4.Broadcast(address, info.PrefixLength));
            }
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var sent = 0;
        foreach (var mac in macs)
        {
            var packet = MagicPacket(mac);
            foreach (var target in targets)
            {
                foreach (var port in WakePorts)
                {
                    try
                    {
                        await udp.SendAsync(packet, new IPEndPoint(target, port), cancellationToken).ConfigureAwait(false);
                        sent++;
                    }
                    catch (SocketException)
                    {
                        // 某个网段发不出去（比如网卡刚断开），试下一个。
                    }
                }
            }
        }

        return sent;
    }
}
