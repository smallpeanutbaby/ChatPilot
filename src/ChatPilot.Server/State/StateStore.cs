using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPilot.Shared.Security;

namespace ChatPilot.Server.State;

/// <summary>需要持久化的服务器状态。</summary>
public sealed class ServerState
{
    /// <summary>微信绑定信息；还没绑定时为 null。</summary>
    public WeChatAccount? WeChat { get; set; }

    /// <summary>已配对的设备。</summary>
    public List<DeviceRecord> Devices { get; set; } = [];

    /// <summary>当前默认设备（<c>/use</c>）。</summary>
    public string? SelectedDeviceId { get; set; }
}

/// <summary>微信绑定信息。<see cref="OwnerUserId"/> 是扫码的微信号，也是唯一能下指令的人。</summary>
public sealed class WeChatAccount
{
    /// <summary>bot ID。</summary>
    public required string BotId { get; set; }

    /// <summary>bot token，调用 iLink 接口时鉴权用（明文保存，状态文件权限 600）。</summary>
    public required string BotToken { get; set; }

    /// <summary>iLink 接口地址。</summary>
    public required string BaseUrl { get; set; }

    /// <summary>主人的微信用户 ID。</summary>
    public required string OwnerUserId { get; set; }

    /// <summary>长轮询游标，重启后从这里继续。</summary>
    public string? SyncBuf { get; set; }

    /// <summary>主人最近一条消息的 context_token，回复时要带上。</summary>
    public string? ContextToken { get; set; }

    /// <summary>绑定时间。</summary>
    public DateTimeOffset BoundAt { get; set; }
}

/// <summary>已配对的设备。只保存令牌的 SHA-256，不保存明文。</summary>
public sealed record DeviceRecord
{
    /// <summary>设备 ID，例如 <c>d_1a2b3c4d5e6f</c>。</summary>
    public required string Id { get; init; }

    /// <summary>设备名（不区分大小写唯一）。</summary>
    public required string Name { get; init; }

    /// <summary>设备令牌的 SHA-256（十六进制小写）。</summary>
    public required string TokenHash { get; init; }

    /// <summary>配对时节点报告的操作系统。</summary>
    public string Platform { get; init; } = "";

    /// <summary>配对时间。</summary>
    public DateTimeOffset PairedAt { get; init; }

    /// <summary>最后一次上线或下线的时间。</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>已经通知过主人"这台设备离线了"、还没通知"恢复"。</summary>
    public bool OfflineNotified { get; init; }
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
    private ServerState _state;

    /// <summary>从数据目录加载状态；文件不存在时从空状态开始。</summary>
    /// <exception cref="InvalidDataException">状态文件已损坏。</exception>
    public StateStore(string dataDirectory)
    {
        SecureFile.EnsurePrivateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "state.json");
        if (!File.Exists(_path))
        {
            _state = new ServerState();
            return;
        }

        try
        {
            _state = JsonSerializer.Deserialize<ServerState>(File.ReadAllText(_path), JsonOptions) ?? new ServerState();
        }
        catch (JsonException ex)
        {
            // 不悄悄重置：重置会丢掉微信绑定和所有设备，让管理员决定怎么处理。
            throw new InvalidDataException($"状态文件 {_path} 已损坏，请从备份恢复或删除后重新绑定微信、重新配对设备", ex);
        }
    }

    /// <summary>状态文件路径。</summary>
    public string FilePath => _path;

    /// <summary>在锁内读取。投影函数应当只返回值或副本，不要把内部对象带出去。</summary>
    public T Read<T>(Func<ServerState, T> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_lock)
        {
            return projection(_state);
        }
    }

    /// <summary>
    /// 事务式修改：在副本上修改并写盘，成功后才替换内存里的状态。
    /// 写盘失败时抛出异常，内存状态保持不变，不会出现"内存改了、文件没改"的情况。
    /// </summary>
    public void Update(Action<ServerState> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_lock)
        {
            var copy = Clone(_state);
            mutation(copy);
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(copy, JsonOptions));
            _state = copy;
        }
    }

    /// <summary>
    /// 尽力修改：内存里一定生效，写盘失败只返回 false，不抛异常。
    /// 用于不关键的数据（最后在线时间、同步游标），以及必须立即生效的撤销设备。
    /// </summary>
    public bool UpdateBestEffort(Action<ServerState> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_lock)
        {
            mutation(_state);
            try
            {
                SecureFile.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOptions));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static ServerState Clone(ServerState state) =>
        JsonSerializer.Deserialize<ServerState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions) ?? new ServerState();

    /// <summary>所有设备（副本）。</summary>
    public IReadOnlyList<DeviceRecord> Devices() => Read(s => s.Devices.ToList());

    /// <summary>按设备 ID 或设备名（不区分大小写）找设备。</summary>
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

    /// <summary>微信绑定信息的副本；还没绑定时为 null。</summary>
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

    /// <summary>设备令牌的 SHA-256（十六进制小写）。</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
