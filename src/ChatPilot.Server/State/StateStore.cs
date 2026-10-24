using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPilot.Shared.Protocol;
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

    /// <summary>还没执行的定时任务（<c>/at</c>）。</summary>
    public List<ScheduledJob> ScheduledJobs { get; set; } = [];

    /// <summary>下一个定时任务的编号。</summary>
    public int NextJobId { get; set; } = 1;

    /// <summary>Telegram 长轮询的 offset（下一个要取的 update_id），重启后从这里继续，不重复执行指令。</summary>
    public long? TelegramOffset { get; set; }
}

/// <summary>一个定时任务：到点后像主人刚发了这条消息一样执行。</summary>
/// <param name="Id">编号，<c>/at del 编号</c> 取消时用。</param>
/// <param name="DueAt">执行时间（UTC）。</param>
/// <param name="Text">到点要执行的消息（<c>@设备/目标 内容</c> 或交给管家的文字）。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record ScheduledJob(int Id, DateTimeOffset DueAt, string Text, DateTimeOffset CreatedAt);

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

    /// <summary>最近一次上线时节点报告的网卡（<c>/wake</c> 唤醒它时用）；旧版节点不报告，为 null。</summary>
    public IReadOnlyList<NetworkInterfaceInfo>? Networks { get; init; }
}

/// <summary>
/// 把 <see cref="ServerState"/> 存成 JSON 文件（权限 600，原子替换）。
/// 所有读写都在锁内进行；读操作通过投影函数拿到不可变的值，不把内部对象泄露出去。
/// </summary>
public sealed class StateStore
{
    /// <summary>状态文件的序列化设置：驼峰命名、带缩进，方便管理员直接查看。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>保护 <c>_state</c> 和写盘，所有读写都在这把锁里进行。</summary>
    private readonly Lock _lock = new();
    /// <summary><c>state.json</c> 的完整路径。</summary>
    private readonly string _path;
    /// <summary>当前状态。<see cref="Update"/> 成功写盘后整体替换成新对象。</summary>
    private ServerState _state;

    /// <summary>从数据目录加载状态；文件不存在时从空状态开始。</summary>
    /// <param name="dataDirectory">服务器的数据目录；不存在时创建（新建的目录只有本人可访问）。</param>
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
    /// <typeparam name="T">读出的值的类型。</typeparam>
    /// <param name="projection">从当前状态里取出需要的值。</param>
    /// <returns>投影函数的返回值。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> 为 null。</exception>
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
    /// <param name="mutation">对状态副本做的修改；它抛出异常时同样什么都不改。</param>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> 为 null。</exception>
    /// <exception cref="IOException">写盘失败，内存里的状态保持不变。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限写状态文件，内存里的状态保持不变。</exception>
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
    /// <param name="mutation">直接对当前状态做的修改。</param>
    /// <returns>写盘成功为 <c>true</c>；文件写不进去为 <c>false</c>（内存里已经改了）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> 为 null。</exception>
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

    /// <summary>通过一次序列化往返做深拷贝，<see cref="Update"/> 在副本上修改，失败时不影响原状态。</summary>
    /// <param name="state">要复制的状态。</param>
    /// <returns>和原状态互不共享对象的副本。</returns>
    private static ServerState Clone(ServerState state) =>
        JsonSerializer.Deserialize<ServerState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions) ?? new ServerState();

    /// <summary>所有设备（副本）。</summary>
    /// <returns>按配对顺序排列的设备；还没有配对的设备时为空列表。</returns>
    public IReadOnlyList<DeviceRecord> Devices() => Read(s => s.Devices.ToList());

    /// <summary>按设备 ID 或设备名（不区分大小写）找设备。</summary>
    /// <param name="idOrName">设备 ID 或设备名；先按 ID 精确匹配，再按名字匹配。</param>
    /// <returns>找到的设备；没有时为 null。</returns>
    public DeviceRecord? FindDevice(string idOrName) =>
        Read(s => s.Devices.FirstOrDefault(d => d.Id == idOrName)
            ?? s.Devices.FirstOrDefault(d => string.Equals(d.Name, idOrName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>按令牌找设备。逐个用定长时间比较，不因匹配位置不同而泄露时间信息。</summary>
    /// <param name="token">节点出示的设备令牌明文。</param>
    /// <returns>令牌对应的设备；令牌为空、超过 256 个字符或不匹配任何设备时为 null。</returns>
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
    /// <returns>绑定信息的副本，改它不影响保存的状态；还没绑定时为 null。</returns>
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
    /// <param name="token">设备令牌明文。</param>
    /// <returns>64 位十六进制小写的哈希，和 <see cref="DeviceRecord.TokenHash"/> 比较用。</returns>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
