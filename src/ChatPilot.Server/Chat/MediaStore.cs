using System.Security.Cryptography;
using ChatPilot.Shared.Media;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Server.Chat;

/// <summary>
/// 主人发来的图片：先暂存，等下一条派活的指令一起发出去；发出时为目标设备生成一次性的下载 ID，节点凭设备令牌来取。
/// 只在内存里，<see cref="Lifetime"/> 后自动丢弃。
/// </summary>
public sealed class MediaStore(TimeProvider time)
{
    /// <summary>暂存的图片和下载 ID 的有效期。</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly List<(byte[] Data, DateTimeOffset At)> _pending = [];
    private readonly Dictionary<string, (byte[] Data, string DeviceId, DateTimeOffset Expires)> _issued = new(StringComparer.Ordinal);

    /// <summary>暂存一张图片。</summary>
    /// <returns>暂存后一共有几张；不是图片、太大或已经满了时返回说明文字。</returns>
    public (int Count, string? Error) Stage(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > ProtocolInfo.MaxImageBytes)
        {
            return (0, $"图片太大了（上限 {ProtocolInfo.MaxImageBytes / 1024 / 1024} MB）");
        }

        if (ImageFormat.DetectExtension(data) is null)
        {
            return (0, "只支持 PNG、JPG、GIF、WebP、BMP 图片");
        }

        lock (_lock)
        {
            Prune();
            if (_pending.Count >= ProtocolInfo.MaxAttachments)
            {
                return (0, $"最多先存 {ProtocolInfo.MaxAttachments} 张图片。先派活把它们发出去，或者发 /drop 丢掉");
            }

            _pending.Add((data, time.GetUtcNow()));
            return (_pending.Count, null);
        }
    }

    /// <summary>暂存着、还没发出去的图片数。</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                Prune();
                return _pending.Count;
            }
        }
    }

    /// <summary>为目标设备生成暂存图片的下载 ID（图片仍然暂存，指令发送成功后再调 <see cref="ClearPending"/>）；没有图片时返回 null。</summary>
    public IReadOnlyList<MediaRef>? Issue(string deviceId)
    {
        lock (_lock)
        {
            Prune();
            if (_pending.Count == 0)
            {
                return null;
            }

            var expires = time.GetUtcNow() + Lifetime;
            var refs = new List<MediaRef>();
            foreach (var (data, _) in _pending)
            {
                var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                _issued[id] = (data, deviceId, expires);
                refs.Add(new MediaRef(id, data.Length, Convert.ToHexStringLower(SHA256.HashData(data))));
            }

            return refs;
        }
    }

    /// <summary>丢掉所有暂存的图片，返回丢掉了几张。</summary>
    public int ClearPending()
    {
        lock (_lock)
        {
            var count = _pending.Count;
            _pending.Clear();
            return count;
        }
    }

    /// <summary>节点取图片：ID 存在、没过期、并且是发给这台设备的才返回。下载 ID 只能用一次，取到之后就作废。</summary>
    public byte[]? Fetch(string id, string deviceId)
    {
        lock (_lock)
        {
            Prune();
            if (!_issued.TryGetValue(id, out var item) || item.DeviceId != deviceId)
            {
                return null;
            }

            _issued.Remove(id);
            return item.Data;
        }
    }

    private void Prune()
    {
        var now = time.GetUtcNow();
        _pending.RemoveAll(p => now - p.At > Lifetime);
        foreach (var id in _issued.Where(i => i.Value.Expires < now).Select(i => i.Key).ToList())
        {
            _issued.Remove(id);
        }
    }
}
