using System.Collections.Concurrent;
using ChatPilot.Node.Execution;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Tests.Support;

/// <summary>记下节点上报的事件。默认立即当作已发到服务器；<see cref="DeliverImmediately"/> 为 false 时模拟断线、发不出去。</summary>
internal sealed class RecordingSink : IAgentEventSink
{
    private readonly BlockingCollection<AgentEvent> _queue = [];

    public ConcurrentQueue<AgentEvent> Received { get; } = new();

    public bool DeliverImmediately { get; set; } = true;

    public void Publish(AgentEvent agentEvent, Action? delivered = null)
    {
        Received.Enqueue(agentEvent);
        _queue.Add(agentEvent);
        if (DeliverImmediately)
        {
            delivered?.Invoke();
        }
    }

    public Task<AgentEvent> WaitAsync() =>
        Task.Run(() => _queue.TryTake(out var item, TimeSpan.FromSeconds(10)) ? item : throw new TimeoutException("没有收到事件"));
}
