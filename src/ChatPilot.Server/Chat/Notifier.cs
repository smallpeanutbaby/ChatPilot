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
    /// <param name="text">消息内容。</param>
    /// <param name="cancellationToken">取消发送。</param>
    /// <returns>发完（或交给队列）时完成的任务。</returns>
    Task SendToOwnerAsync(string text, CancellationToken cancellationToken);
}

/// <summary>已启用的聊天通道。通道启动时自己登记，避免和指令分发器形成循环依赖。</summary>
public sealed class ChatChannelRegistry
{
    /// <summary>保护 <c>_channels</c>：通道在启动时登记，通知随时在读。</summary>
    private readonly Lock _lock = new();
    /// <summary>已登记的通道，按登记顺序。</summary>
    private readonly List<IChatChannel> _channels = [];

    /// <summary>登记一个通道；重复登记会被忽略。</summary>
    /// <param name="channel">要登记的通道。</param>
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
    /// <returns>按登记顺序排列的通道；还没有通道登记时为空列表。</returns>
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
/// 设备事件经 <see cref="Enqueue"/> 进入每个通道各自的有界队列，每个通道一个后台任务按顺序发送，不阻塞设备的接收循环；
/// 一个通道卡住（例如没配代理时连不上 Telegram）只会让它自己的队列排满，不耽误、也不挤掉另一个通道的通知。
/// </summary>
/// <remarks>
/// 发送失败的日志里只记异常的类型和说明，不记异常对象：Telegram 的异常里可能带着含 bot token 的请求地址（见 docs/SECURITY.md S-21）。
/// </remarks>
public sealed class Notifier : IDisposable
{
    /// <summary>每个通道最多排队多少条还没发出的通知；通道一直发不出去时，超出的直接丢弃，不会无限占内存。</summary>
    private const int QueueCapacity = 100;

    /// <summary>一条通知最多发多久；超过时放弃这条，接着发后面的。</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(2);

    /// <summary>已登记的聊天通道。</summary>
    private readonly ChatChannelRegistry _registry;
    /// <summary>日志。</summary>
    private readonly ILogger<Notifier> _logger;
    /// <summary>保护 <c>_queues</c> 和 <c>_disposed</c>：通知随时在排队，<see cref="Dispose"/> 可能同时发生。</summary>
    private readonly Lock _lock = new();
    /// <summary>每个通道等待发送的通知，按通道对象区分；第一次给这个通道排队时创建，同时启动它的发送任务。</summary>
    private readonly Dictionary<IChatChannel, Channel<string>> _queues = new(ReferenceEqualityComparer.Instance);
    /// <summary>是否已经 <see cref="Dispose"/>，之后不再接收新通知。</summary>
    private bool _disposed;

    /// <summary>创建通知器。各通道的发送任务在第一次给它排队时启动。</summary>
    /// <param name="registry">已登记的聊天通道。</param>
    /// <param name="logger">日志。</param>
    public Notifier(ChatChannelRegistry registry, ILogger<Notifier> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>给每个已登记的通道排队发送一条通知；某个通道的队列满时只丢弃那个通道的这条，并记日志。</summary>
    /// <param name="text">通知内容。</param>
    public void Enqueue(string text)
    {
        var channels = _registry.Snapshot();
        if (!channels.Any(c => c.IsReady))
        {
            _logger.LogWarning("没有可用的聊天通道，通知没有发出");
            return;
        }

        foreach (var channel in channels)
        {
            if (QueueFor(channel) is { } queue && !queue.Writer.TryWrite(text))
            {
                _logger.LogWarning("{Channel} 的通知队列已满 {Capacity} 条，丢弃一条通知", channel.Name, QueueCapacity);
            }
        }
    }

    /// <summary>停止接收新通知，各通道的发送任务发完队列里的通知后退出。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var queue in _queues.Values)
            {
                queue.Writer.TryComplete();
            }
        }
    }

    /// <summary>立即发到所有可用通道（各通道同时发）。单个通道失败只记日志，不影响其他通道。</summary>
    /// <param name="text">通知内容。</param>
    /// <param name="cancellationToken">取消发送；取消不会被吞掉，会抛给调用方。</param>
    /// <returns>所有通道都尝试发过之后完成的任务；没有可用通道时只记日志。</returns>
    public async Task BroadcastAsync(string text, CancellationToken cancellationToken)
    {
        var channels = _registry.Snapshot().Where(c => c.IsReady).ToList();
        if (channels.Count == 0)
        {
            _logger.LogWarning("没有可用的聊天通道，通知没有发出");
            return;
        }

        await Task.WhenAll(channels.Select(c => SendAsync(c, text, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>这个通道的通知队列；还没有时创建，并启动它的发送任务。</summary>
    /// <param name="channel">聊天通道。</param>
    /// <returns>通道的队列；已经 <see cref="Dispose"/> 时为 null。</returns>
    private Channel<string>? QueueFor(IChatChannel channel)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return null;
            }

            if (!_queues.TryGetValue(channel, out var queue))
            {
                queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
                _queues[channel] = queue;
                _ = Task.Run(() => DrainAsync(channel, queue));
            }

            return queue;
        }
    }

    /// <summary>一个通道的发送任务：按顺序取出它队列里的通知逐条发送（通道没就绪时跳过），直到 <see cref="Dispose"/> 后队列发完。</summary>
    /// <param name="channel">聊天通道。</param>
    /// <param name="queue">它的通知队列。</param>
    /// <returns>队列关闭并发完后完成的任务。</returns>
    private async Task DrainAsync(IChatChannel channel, Channel<string> queue)
    {
        await foreach (var text in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (channel.IsReady)
            {
                await SendAsync(channel, text, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>通过一个通道发一条通知，最多等 <see cref="SendTimeout"/>。失败只记异常的类型和说明。</summary>
    /// <param name="channel">聊天通道。</param>
    /// <param name="text">通知内容。</param>
    /// <param name="cancellationToken">取消发送；取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>发出、失败或超时之后完成的任务。</returns>
    private async Task SendAsync(IChatChannel channel, string text, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SendTimeout);
        try
        {
            await channel.SendToOwnerAsync(text, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _logger.LogWarning("通过 {Channel} 发送通知超过 {Minutes} 分钟没有完成，放弃这条", channel.Name, SendTimeout.TotalMinutes);
        }
        catch (Exception ex)
        {
            // 不记异常对象：Telegram 的异常（包括里面套着的异常）可能带着含 bot token 的请求地址。
            _logger.LogWarning("通过 {Channel} 发送通知失败：{Type} {Message}", channel.Name, ex.GetType().Name, ex.Message);
        }
    }
}
