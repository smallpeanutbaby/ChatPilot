using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using ChatPilot.Server.Options;
using ChatPilot.Server.State;
using ChatPilot.Shared;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Devices;

/// <summary>
/// 配对码：8 位（去掉 0/O/1/I/L 等易混字符，约 40 bit），有效期默认 10 分钟，只能用一次，最多同时有 5 个。
/// </summary>
/// <param name="store">状态文件，配对成功的设备保存在这里。</param>
/// <param name="time">时间来源，用来计算过期，测试里可以换成假时钟。</param>
/// <param name="options">服务器配置，提供配对码的有效期。</param>
public sealed class PairingService(StateStore store, TimeProvider time, IOptions<ServerOptions> options)
{
    /// <summary>配对码可用的字符：去掉了 0/O/1/I/L 这类容易看错、念错的字符。</summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    /// <summary>配对码的位数（不含短横线）。</summary>
    private const int CodeLength = 8;
    /// <summary>最多同时有几个没用掉的配对码；再生成时挤掉最早的那个。</summary>
    private const int MaxOutstanding = 5;

    /// <summary>保护 <c>_codes</c>，也保证同一个配对码不会被并发兑换两次。</summary>
    private readonly Lock _lock = new();
    /// <summary>还没用掉的配对码，最早生成的在前。</summary>
    private readonly List<PendingCode> _codes = [];

    /// <summary>一个还没被使用的配对码。</summary>
    /// <param name="Code">8 位配对码（不含短横线）。</param>
    /// <param name="DeviceName">发 /pair 时预先指定的设备名。</param>
    /// <param name="ExpiresAt">过期时间。</param>
    public sealed record PendingCode(string Code, string? DeviceName, DateTimeOffset ExpiresAt)
    {
        /// <summary>给人看的格式：ABCD-EFGH。</summary>
        public string Display => Code[..4] + "-" + Code[4..];
    }

    /// <summary>兑换配对码的结果。</summary>
    public abstract record RedeemResult;

    /// <summary>配对成功。</summary>
    /// <param name="Device">新设备。</param>
    /// <param name="Token">设备令牌明文，只返回给节点这一次。</param>
    public sealed record Paired(DeviceRecord Device, string Token) : RedeemResult;

    /// <summary>配对码不存在、已用过或已过期。</summary>
    public sealed record InvalidCode : RedeemResult;

    /// <summary>设备名不合法。</summary>
    /// <param name="Message">给用户看的说明。</param>
    public sealed record InvalidName(string Message) : RedeemResult;

    /// <summary>生成配对码；<paramref name="deviceName"/> 可以提前指定设备名。</summary>
    /// <param name="deviceName">配对后使用的设备名，优先于节点自己请求的名字；为 null 时用节点请求的。</param>
    /// <returns>新生成的配对码和它的过期时间。</returns>
    public PendingCode Create(string? deviceName)
    {
        var code = new string(Enumerable.Range(0, CodeLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var pending = new PendingCode(code, deviceName, time.GetUtcNow().AddMinutes(options.Value.PairingCodeMinutes));
        lock (_lock)
        {
            Purge();
            if (_codes.Count >= MaxOutstanding)
            {
                _codes.RemoveAt(0);
            }

            _codes.Add(pending);
        }

        return pending;
    }

    /// <summary>用配对码换设备令牌。配对码不对或过期时不消耗任何东西。</summary>
    /// <param name="code">节点提交的配对码，大小写、短横线、空格都可以。</param>
    /// <param name="requestedName">节点希望使用的设备名；生成配对码时指定过设备名的话以那个为准。</param>
    /// <param name="platform">节点报告的操作系统，截短到 100 个字符保存。</param>
    /// <returns>
    /// <see cref="Paired"/>：配对成功（重名时设备名加 <c>-2</c>、<c>-3</c> 这样的后缀）；
    /// <see cref="InvalidCode"/>：配对码不存在、已用过或已过期；
    /// <see cref="InvalidName"/>：设备名不合法，配对码不会被消耗。
    /// </returns>
    /// <exception cref="IOException">新设备写盘失败；这时配对码已经作废，需要重新生成。</exception>
    public RedeemResult Redeem(string code, string requestedName, string platform)
    {
        var normalized = Normalize(code);
        PendingCode? pending;
        lock (_lock)
        {
            Purge();
            pending = normalized is null ? null : _codes.FirstOrDefault(c => FixedEquals(c.Code, normalized));
            if (pending is null)
            {
                return new InvalidCode();
            }

            var name = pending.DeviceName ?? requestedName;
            if (!Validation.IsDeviceName(name))
            {
                return new InvalidName("设备名只能包含字母、数字、下划线、短横线，最长 32 位");
            }

            _codes.Remove(pending);
            var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            DeviceRecord? created = null;
            store.Update(state =>
            {
                created = new DeviceRecord
                {
                    Id = "d_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
                    Name = UniqueName(name, state.Devices),
                    TokenHash = StateStore.HashToken(token),
                    Platform = TextChunker.Truncate(platform ?? "", 100),
                    PairedAt = time.GetUtcNow(),
                };
                state.Devices.Add(created);
            });
            return new Paired(created ?? throw new InvalidOperationException("设备没有保存成功"), token);
        }
    }

    /// <summary>去掉短横线和空格并转大写；格式不对返回 null。</summary>
    /// <param name="code">用户输入的配对码。</param>
    /// <returns>8 位大写配对码；为 null、太长、位数不对或含有不在字母表里的字符时为 null。</returns>
    internal static string? Normalize(string? code)
    {
        if (code is null || code.Length > 32)
        {
            return null;
        }

        var value = code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return value.Length == CodeLength && value.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) ? value : null;
    }

    /// <summary>定长时间比较两个配对码，不因比较到第几位不同而泄露时间信息。</summary>
    /// <param name="a">一个配对码。</param>
    /// <param name="b">另一个配对码。</param>
    /// <returns>两者完全相同时为 <c>true</c>。</returns>
    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));

    /// <summary>设备名重名（不区分大小写）时依次加 <c>-2</c>、<c>-3</c>……后缀，必要时截短原名，保证不超过 32 位。</summary>
    /// <param name="name">想用的设备名。</param>
    /// <param name="devices">已有的设备。</param>
    /// <returns>不和已有设备重名的设备名。</returns>
    private static string UniqueName(string name, List<DeviceRecord> devices)
    {
        bool Taken(string candidate) => devices.Any(d => string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var suffix = "-" + i.ToString(CultureInfo.InvariantCulture);
            var candidate = (name.Length + suffix.Length > 32 ? name[..(32 - suffix.Length)] : name) + suffix;
            if (!Taken(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>丢掉过期的配对码。调用方必须已经持有锁。</summary>
    private void Purge()
    {
        var now = time.GetUtcNow();
        _codes.RemoveAll(c => c.ExpiresAt <= now);
    }
}
