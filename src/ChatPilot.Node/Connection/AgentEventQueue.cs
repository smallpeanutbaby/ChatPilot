using System.Threading.Channels;
using ChatPilot.Node.Execution;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Connection;

/// <summary>一条待上报的事件。</summary>
/// <param name="Event">事件。</param>
/// <param name="Delivered">发给服务器之后调用（可以为 null）。</param>
public sealed record QueuedEvent(AgentEvent Event, Action? Delivered);

/// <summary>待上报的 agent 事件。断线期间先存着，重连后补发；最多存 200 条。</summary>
public sealed class AgentEventQueue(ILogger<AgentEventQueue> logger) : IAgentEventSink
{
    private const int Capacity = 200;

    private readonly Channel<QueuedEvent> _channel = Channel.CreateBounded<QueuedEvent>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    /// <summary>连接层从这里取事件发送；只能有一个读取方。</summary>
    public ChannelReader<QueuedEvent> Reader => _channel.Reader;

    /// <inheritdoc />
    public void Publish(AgentEvent agentEvent, Action? delivered = null)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        if (!_channel.Writer.TryWrite(new QueuedEvent(agentEvent, delivered)))
        {
            logger.LogWarning("待上报事件已满 {Capacity} 条，丢弃 {Agent} 的事件", Capacity, agentEvent.Agent.DisplayName);
        }
    }
}
