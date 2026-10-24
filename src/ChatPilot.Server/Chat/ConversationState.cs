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
    /// <param name="deviceId">agent 所在设备的 ID。</param>
    /// <param name="deviceName">agent 所在设备的名字。</param>
    /// <param name="agent">节点报告的 agent 概要。</param>
    /// <returns>记着设备和 pane 的引用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> 为 null。</exception>
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
/// <remarks>
/// 规则：只有主人自己的操作会改变"最近打过交道的 agent"——主人指定了它（<see cref="Touch"/>），
/// 或者主人从聊天里派给它的活（<see cref="Dispatched"/>）有了结果（做完、出错、等确认）。
/// <c>/watch</c> 盯着的 agent 自己干完活、<c>/at</c> 定时任务派的活，只通知主人，不会悄悄换掉 <c>/stop</c>、<c>/re</c> 的对象。
/// </remarks>
/// <param name="time">时间来源，用来让“在等确认”和“派过活”的记录过期，测试里可以换成假时钟。</param>
public sealed class ConversationState(TimeProvider time)
{
    /// <summary>"在等确认"记多久；超过的当作已经处理掉了。</summary>
    private static readonly TimeSpan BlockedLifetime = TimeSpan.FromHours(6);

    /// <summary>"派过活、还没出结果"记多久：节点最多等 48 小时，再留些余量。</summary>
    private static readonly TimeSpan DispatchLifetime = TimeSpan.FromDays(3);

    /// <summary>最多同时记多少个派过活、还没出结果的 agent；超出时忘掉最早的。</summary>
    private const int MaxDispatches = 64;

    /// <summary>保护 <c>_blocked</c>、<c>_dispatched</c> 和 <c>_recent</c>：聊天消息和设备事件会同时更新它们。</summary>
    private readonly Lock _lock = new();
    /// <summary>在等确认的 agent，按设备和 pane 区分，记着收到通知（或者从指令结果里得知）的时间。</summary>
    private readonly Dictionary<(string DeviceId, string PaneId), (AgentRef Agent, DateTimeOffset At)> _blocked = [];
    /// <summary>主人从聊天里派过活、还没出结果的 agent，见 <see cref="Dispatched"/>。</summary>
    private readonly List<Dispatch> _dispatched = [];
    /// <summary>最近打过交道的 agent，见 <see cref="Recent"/>。</summary>
    private AgentRef? _recent;

    /// <summary>主人派过活、还没出结果的一个 agent。</summary>
    /// <param name="DeviceId">设备 ID。</param>
    /// <param name="PaneId">pane ID；只知道名字时（交给管家的文字）为 null。</param>
    /// <param name="Name">agent 名字；没起名时为 null。</param>
    /// <param name="At">派活的时间。</param>
    private sealed record Dispatch(string DeviceId, string? PaneId, string? Name, DateTimeOffset At)
    {
        /// <summary>事件里的 agent 是不是它：同一台设备，pane 对得上；只记了名字时名字对得上。</summary>
        /// <param name="deviceId">事件来自的设备。</param>
        /// <param name="agent">事件里的 agent。</param>
        /// <returns>是同一个 agent 时为 <c>true</c>。</returns>
        public bool Matches(string deviceId, AgentSummary agent) =>
            DeviceId == deviceId
            && (PaneId is not null
                ? PaneId == agent.PaneId
                : Name is not null && string.Equals(Name, agent.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>最近打过交道的 agent：主人最近指定过的，或者主人派的活最近一次有了结果的。</summary>
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
    /// <param name="agent">记为最近打过交道的 agent。</param>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> 为 null。</exception>
    public void Touch(AgentRef agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        lock (_lock)
        {
            _recent = agent;
        }
    }

    /// <summary>
    /// 主人刚从聊天里派活给它（<c>@</c>、<c>/re</c>、交给管家、<c>/butler</c> 的初始说明、<c>/yes</c> 之后接着干）：
    /// 它之后的通知会把它记为最近打过交道的。定时任务派的活不算。
    /// </summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <param name="paneId">pane ID；只知道名字时为 null。</param>
    /// <param name="name">agent 名字；<paramref name="paneId"/> 为 null 时用它对上事件。</param>
    public void Dispatched(string deviceId, string? paneId, string? name)
    {
        if (paneId is null && name is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        lock (_lock)
        {
            _dispatched.RemoveAll(d => d.At < now - DispatchLifetime
                || (d.DeviceId == deviceId && d.PaneId == paneId && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)));
            _dispatched.Add(new Dispatch(deviceId, paneId, name, now));
            if (_dispatched.Count > MaxDispatches)
            {
                _dispatched.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// 设备上报了事件：卡在确认框的记下来，做完或出错的划掉。只有主人派的活（见 <see cref="Dispatched"/>）的事件
    /// 才把它记为最近打过交道的；做完或出错、并且没有排队的指令时，这次派活就算结束了。
    /// </summary>
    /// <param name="deviceId">上报事件的设备 ID。</param>
    /// <param name="deviceName">上报事件的设备名。</param>
    /// <param name="agentEvent">节点上报的事件。</param>
    /// <returns>事件属于主人派的活、换了最近打过交道的 agent 时为 <c>true</c>；盯着的 agent、定时任务的事件为 <c>false</c>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agentEvent"/> 为 null。</exception>
    public bool OnEvent(string deviceId, string deviceName, AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        var agent = AgentRef.From(deviceId, deviceName, agentEvent.Agent);
        var now = time.GetUtcNow();
        lock (_lock)
        {
            var dispatch = _dispatched.LastOrDefault(d => d.At >= now - DispatchLifetime && d.Matches(deviceId, agentEvent.Agent));
            if (dispatch is not null)
            {
                _recent = agent;
                if (agentEvent.Kind != AgentEventKind.Blocked && agentEvent.Queued == 0)
                {
                    _dispatched.Remove(dispatch);
                }
            }

            if (agentEvent.Kind == AgentEventKind.Blocked)
            {
                _blocked[(deviceId, agent.PaneId)] = (agent, now);
            }
            else
            {
                _blocked.Remove((deviceId, agent.PaneId));
            }

            return dispatch is not null;
        }
    }

    /// <summary>
    /// 从指令结果里得知它在等确认（派活被拒、<c>/new</c> 启动后卡在确认框、<c>/yes</c> 发现确认框变了）：
    /// 节点让主人"先回复 /yes"，所以把它当作在等确认的候选。
    /// </summary>
    /// <param name="agent">在等确认的 agent。</param>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> 为 null。</exception>
    public void NoteBlocked(AgentRef agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        lock (_lock)
        {
            _blocked[(agent.DeviceId, agent.PaneId)] = (agent, time.GetUtcNow());
        }
    }

    /// <summary>主人已经回应了这个 agent 的确认框。</summary>
    /// <param name="deviceId">agent 所在设备的 ID。</param>
    /// <param name="paneId">agent 所在的 pane。</param>
    public void Responded(string deviceId, string paneId)
    {
        lock (_lock)
        {
            _blocked.Remove((deviceId, paneId));
        }
    }

    /// <summary>agent 被关闭了：不再把它当作默认目标，也不再记着它在等确认、派过活。</summary>
    /// <param name="deviceId">agent 所在设备的 ID。</param>
    /// <param name="paneId">agent 所在的 pane。</param>
    public void ForgetAgent(string deviceId, string paneId)
    {
        lock (_lock)
        {
            _blocked.Remove((deviceId, paneId));
            _dispatched.RemoveAll(d => d.DeviceId == deviceId && d.PaneId == paneId);
            if (_recent is { } recent && recent.DeviceId == deviceId && recent.PaneId == paneId)
            {
                _recent = null;
            }
        }
    }

    /// <summary>设备被撤销：忘掉它的 agent。</summary>
    /// <param name="deviceId">被撤销的设备 ID。</param>
    public void ForgetDevice(string deviceId)
    {
        lock (_lock)
        {
            foreach (var key in _blocked.Keys.Where(k => k.DeviceId == deviceId).ToList())
            {
                _blocked.Remove(key);
            }

            _dispatched.RemoveAll(d => d.DeviceId == deviceId);

            if (_recent?.DeviceId == deviceId)
            {
                _recent = null;
            }
        }
    }

    /// <summary>发过"在等你确认"、还没处理的 agent，最近的在前。</summary>
    /// <returns>没过期的记录（过期的顺便清掉）；没有时为空列表。</returns>
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
