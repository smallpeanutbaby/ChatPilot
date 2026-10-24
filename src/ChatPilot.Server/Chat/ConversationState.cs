using ChatPilot.Shared.Protocol;

namespace ChatPilot.Server.Chat;

/// <summary>对话里记着的一个 agent。</summary>
/// <param name="DeviceId">设备 ID。</param>
/// <param name="DeviceName">设备名。</param>
/// <param name="PaneId">pane ID。</param>
/// <param name="Name">agent 名字；没起名时为 null。</param>
/// <param name="DisplayName">给人看的名字。</param>
public sealed record AgentRef(string DeviceId, string DeviceName, string PaneId, string? Name, string DisplayName)
{
    /// <summary>用来再次找到它的目标，例如 <c>mac/tideq</c>。</summary>
    public TargetRef Target => new(DeviceName, Name ?? PaneId);

    /// <summary>回复里显示的名字，例如 <c>[mac] tideq</c>。</summary>
    public string Label => $"[{DeviceName}] {DisplayName}";

    /// <summary>从 agent 概要生成。</summary>
    public static AgentRef From(string deviceId, string deviceName, AgentSummary agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return new AgentRef(deviceId, deviceName, agent.PaneId, agent.Name, agent.DisplayName);
    }
}

/// <summary>
/// 对话上下文：最近打过交道的 agent（不写目标时默认用它），以及发过"在等你确认"、还没处理的 agent（<c>/yes</c> 不写目标时用）。
/// 只记在内存里，服务器重启后清空。
/// </summary>
public sealed class ConversationState(TimeProvider time)
{
    /// <summary>"在等确认"记多久；超过的当作已经处理掉了。</summary>
    private static readonly TimeSpan BlockedLifetime = TimeSpan.FromHours(6);

    private readonly Lock _lock = new();
    private readonly Dictionary<(string DeviceId, string PaneId), (AgentRef Agent, DateTimeOffset At)> _blocked = [];
    private AgentRef? _recent;

    /// <summary>最近打过交道的 agent：最近给主人发过通知的，或者主人最近指定过的。</summary>
    public AgentRef? Recent
    {
        get
        {
            lock (_lock)
            {
                return _recent;
            }
        }
    }

    /// <summary>主人指定了这个 agent（派活、看输出、回应确认框……）。</summary>
    public void Touch(AgentRef agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        lock (_lock)
        {
            _recent = agent;
        }
    }

    /// <summary>设备上报了事件：记为最近打过交道的；卡在确认框的记下来，做完或出错的划掉。</summary>
    public void OnEvent(string deviceId, string deviceName, AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        var agent = AgentRef.From(deviceId, deviceName, agentEvent.Agent);
        lock (_lock)
        {
            _recent = agent;
            if (agentEvent.Kind == AgentEventKind.Blocked)
            {
                _blocked[(deviceId, agent.PaneId)] = (agent, time.GetUtcNow());
            }
            else
            {
                _blocked.Remove((deviceId, agent.PaneId));
            }
        }
    }

    /// <summary>主人已经回应了这个 agent 的确认框。</summary>
    public void Responded(string deviceId, string paneId)
    {
        lock (_lock)
        {
            _blocked.Remove((deviceId, paneId));
        }
    }

    /// <summary>agent 被关闭了：不再把它当作默认目标，也不再记着它在等确认。</summary>
    public void ForgetAgent(string deviceId, string paneId)
    {
        lock (_lock)
        {
            _blocked.Remove((deviceId, paneId));
            if (_recent is { } recent && recent.DeviceId == deviceId && recent.PaneId == paneId)
            {
                _recent = null;
            }
        }
    }

    /// <summary>设备被撤销：忘掉它的 agent。</summary>
    public void ForgetDevice(string deviceId)
    {
        lock (_lock)
        {
            foreach (var key in _blocked.Keys.Where(k => k.DeviceId == deviceId).ToList())
            {
                _blocked.Remove(key);
            }

            if (_recent?.DeviceId == deviceId)
            {
                _recent = null;
            }
        }
    }

    /// <summary>发过"在等你确认"、还没处理的 agent，最近的在前。</summary>
    public IReadOnlyList<AgentRef> Blocked()
    {
        var cutoff = time.GetUtcNow() - BlockedLifetime;
        lock (_lock)
        {
            foreach (var key in _blocked.Where(b => b.Value.At < cutoff).Select(b => b.Key).ToList())
            {
                _blocked.Remove(key);
            }

            return _blocked.Values.OrderByDescending(b => b.At).Select(b => b.Agent).ToList();
        }
    }
}
