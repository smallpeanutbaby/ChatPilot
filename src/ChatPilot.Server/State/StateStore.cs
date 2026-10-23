using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPilot.Shared.Security;

namespace ChatPilot.Server.State;

/// <summary>需要持久化的服务器状态。</summary>
public sealed class ServerState
{
    public WeChatAccount? WeChat { get; set; }

    public List<DeviceRecord> Devices { get; set; } = [];

    /// <summary>当前默认设备（<c>/use</c>）。</summary>
    public string? SelectedDeviceId { get; set; }
}

/// <summary>微信绑定信息。<see cref="OwnerUserId"/> 是扫码的微信号，也是唯一能下指令的人。</summary>
public sealed class WeChatAccount
{
    public required string BotId { get; set; }

    public required string BotToken { get; set; }

    public required string BaseUrl { get; set; }

    public required string OwnerUserId { get; set; }

    /// <summary>长轮询游标，重启后从这里继续。</summary>
    public string? SyncBuf { get; set; }

    /// <summary>主人最近一条消息的 context_token，回复时要带上。</summary>
    public string? ContextToken { get; set; }

    public DateTimeOffset BoundAt { get; set; }
}

/// <summary>已配对的设备。只保存令牌的 SHA-256，不保存明文。</summary>
public sealed record DeviceRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string TokenHash { get; init; }

    public string Platform { get; init; } = "";

    public DateTimeOffset PairedAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }
}

/// <summary>
/// 把 <see cref="ServerState"/> 存成 JSON 文件（权限 600，原子替换）。
/// 所有读写都在锁内进行；读操作通过投影函数拿到不可变的值，不把内部对象泄露出去。
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly ServerState _state;

    public StateStore(string dataDirectory)
    {
        SecureFile.EnsurePrivateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "state.json");
        _state = File.Exists(_path)
            ? JsonSerializer.Deserialize<ServerState>(File.ReadAllText(_path), JsonOptions) ?? new ServerState()
            : new ServerState();
    }

    public string FilePath => _path;

    public T Read<T>(Func<ServerState, T> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_lock)
        {
            return projection(_state);
        }
    }

    public void Update(Action<ServerState> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_lock)
        {
            mutation(_state);
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOptions));
        }
    }

    public IReadOnlyList<DeviceRecord> Devices() => Read(s => s.Devices.ToList());

    public DeviceRecord? FindDevice(string idOrName) =>
        Read(s => s.Devices.FirstOrDefault(d => d.Id == idOrName)
            ?? s.Devices.FirstOrDefault(d => string.Equals(d.Name, idOrName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>按令牌找设备。逐个用定长时间比较，不因匹配位置不同而泄露时间信息。</summary>
    public DeviceRecord? FindDeviceByToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256)
        {
            return null;
        }

        var hash = Encoding.ASCII.GetBytes(HashToken(token));
        return Read(s =>
        {
            DeviceRecord? match = null;
            foreach (var device in s.Devices)
            {
                if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(device.TokenHash)))
                {
                    match = device;
                }
            }

            return match;
        });
    }

    public WeChatAccount? WeChatSnapshot() => Read(s => s.WeChat is null ? null : new WeChatAccount
    {
        BotId = s.WeChat.BotId,
        BotToken = s.WeChat.BotToken,
        BaseUrl = s.WeChat.BaseUrl,
        OwnerUserId = s.WeChat.OwnerUserId,
        SyncBuf = s.WeChat.SyncBuf,
        ContextToken = s.WeChat.ContextToken,
        BoundAt = s.WeChat.BoundAt,
    });

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
