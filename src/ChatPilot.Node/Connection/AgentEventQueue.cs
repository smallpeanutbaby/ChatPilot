using System.Threading.Channels;
using ChatPilot.Node.Execution;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Connection;

/// <summary>待上报的 agent 事件。断线期间先存着，重连后补发；最多存 200 条。</summary>
public sealed class AgentEventQueue(ILogger<AgentEventQueue> logger) : IAgentEventSink
{
    private const int Capacity = 200;

    private readonly Channel<AgentEvent> _channel = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public ChannelReader<AgentEvent> Reader => _channel.Reader;

    public void Publish(AgentEvent agentEvent)
    {
        if (!_channel.Writer.TryWrite(agentEvent))
        {
            logger.LogWarning("待上报事件已满 {Capacity} 条，丢弃 {Agent} 的事件", Capacity, agentEvent.Agent.DisplayName);
        }
    }
}
