using System.Threading.Channels;

namespace ChatPilot.Server.Chat;

/// <summary>一个能给主人发消息的聊天通道（微信、测试通道）。</summary>
public interface IChatChannel
{
    /// <summary>通道名，用于日志。</summary>
    string Name { get; }

    /// <summary>通道是否可以发消息（例如微信已经绑定）。</summary>
    bool IsReady { get; }

    /// <summary>给主人发一条消息（超长时由通道自己分段）。</summary>
    Task SendToOwnerAsync(string text, CancellationToken cancellationToken);
}

/// <summary>已启用的聊天通道。通道启动时自己登记，避免和指令分发器形成循环依赖。</summary>
public sealed class ChatChannelRegistry
{
    private readonly Lock _lock = new();
    private readonly List<IChatChannel> _channels = [];

    /// <summary>登记一个通道；重复登记会被忽略。</summary>
    public void Register(IChatChannel channel)
    {
        lock (_lock)
        {
            if (!_channels.Contains(channel))
            {
                _channels.Add(channel);
            }
        }
    }

    /// <summary>当前已登记的通道（副本）。</summary>
    public IReadOnlyList<IChatChannel> Snapshot()
    {
        lock (_lock)
        {
            return _channels.ToList();
        }
    }
}

/// <summary>
/// 把主动通知（agent 做完、卡住）发到所有可用的通道。
/// 设备事件经 <see cref="Enqueue"/> 进入一个有界队列，由单个后台任务按顺序发送，不阻塞设备的接收循环。
/// </summary>
public sealed class Notifier : IDisposable
{
    private const int QueueCapacity = 100;

    private readonly ChatChannelRegistry _registry;
    private readonly ILogger<Notifier> _logger;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    /// <summary>创建通知器，并启动按顺序发送队列的后台任务。</summary>
    public Notifier(ChatChannelRegistry registry, ILogger<Notifier> logger)
    {
        _registry = registry;
        _logger = logger;
        _ = Task.Run(DrainAsync);
    }

    /// <summary>排队发送一条通知；队列满时丢弃并记日志。</summary>
    public void Enqueue(string text)
    {
        if (!_queue.Writer.TryWrite(text))
        {
            _logger.LogWarning("通知队列已满 {Capacity} 条，丢弃一条通知", QueueCapacity);
        }
    }

    /// <summary>停止接收新通知，后台任务发完队列里的通知后退出。</summary>
    public void Dispose() => _queue.Writer.TryComplete();

    /// <summary>立即发到所有可用通道。单个通道失败只记日志，不影响其他通道。</summary>
    public async Task BroadcastAsync(string text, CancellationToken cancellationToken)
    {
        var channels = _registry.Snapshot().Where(c => c.IsReady).ToList();
        if (channels.Count == 0)
        {
            _logger.LogWarning("没有可用的聊天通道，通知没有发出");
            return;
        }

        foreach (var channel in channels)
        {
            try
            {
                await channel.SendToOwnerAsync(text, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "通过 {Channel} 发送通知失败", channel.Name);
            }
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var text in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await BroadcastAsync(text, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送通知失败");
            }
        }
    }
}
