namespace ChatPilot.Server.Chat;

/// <summary>一个能给主人发消息的聊天通道（微信、测试通道）。</summary>
public interface IChatChannel
{
    string Name { get; }

    /// <summary>通道是否可以发消息（例如微信已经绑定）。</summary>
    bool IsReady { get; }

    Task SendToOwnerAsync(string text, CancellationToken cancellationToken);
}

/// <summary>已启用的聊天通道。通道启动时自己登记，避免和指令分发器形成循环依赖。</summary>
public sealed class ChatChannelRegistry
{
    private readonly Lock _lock = new();
    private readonly List<IChatChannel> _channels = [];

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

    public IReadOnlyList<IChatChannel> Snapshot()
    {
        lock (_lock)
        {
            return _channels.ToList();
        }
    }
}

/// <summary>把主动通知（agent 做完、卡住）发到所有可用的通道。</summary>
public sealed class Notifier(ChatChannelRegistry registry, ILogger<Notifier> logger)
{
    public async Task BroadcastAsync(string text, CancellationToken cancellationToken)
    {
        var channels = registry.Snapshot().Where(c => c.IsReady).ToList();
        if (channels.Count == 0)
        {
            logger.LogWarning("没有可用的聊天通道，通知没有发出");
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
                logger.LogWarning(ex, "通过 {Channel} 发送通知失败", channel.Name);
            }
        }
    }
}
