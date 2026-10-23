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
public sealed class PairingService(StateStore store, TimeProvider time, IOptions<ServerOptions> options)
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 8;
    private const int MaxOutstanding = 5;

    private readonly Lock _lock = new();
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
    internal static string? Normalize(string? code)
    {
        if (code is null || code.Length > 32)
        {
            return null;
        }

        var value = code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return value.Length == CodeLength && value.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) ? value : null;
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));

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

    private void Purge()
    {
        var now = time.GetUtcNow();
        _codes.RemoveAll(c => c.ExpiresAt <= now);
    }
}
