using System.Collections.Concurrent;
using ChatPilot.Node.Herdr;

namespace ChatPilot.Node.Execution;

/// <summary>屏幕上的确认框和给用户看过的比，是什么情况。</summary>
public enum DialogCheck
{
    /// <summary>就是给用户看过的那一个。</summary>
    Shown,

    /// <summary>给用户看过一个，但现在的不是它了。</summary>
    Changed,

    /// <summary>这个 agent 的确认框还没给用户看过（或者看过的已经同意过了）。</summary>
    NotShown,
}

/// <summary>
/// 记住给用户看过的确认框，<c>/yes</c> 只同意看过的那一个。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>通知发出后，原来的确认框可能已经在电脑上处理掉、又弹出了新的。迟到的 /yes 不能去同意用户没看过的新确认框。</item>
/// <item>用两样东西认确认框：herdr 的状态序号（中间干过活就会变），以及 <see cref="ApprovalNavigator.Fingerprint"/>
/// （herdr 没察觉状态变化、确认框直接换成下一个时，内容会变）。两样都对上才算同一个。</item>
/// <item>同意过一次就作废：连发两次 /yes，第二次不会同意紧接着弹出的确认框。</item>
/// <item>只记在内存里，节点重启后清空，那时的 /yes 会先把确认框发给用户看一遍。</item>
/// </list>
/// </remarks>
public sealed class ShownDialogs
{
    /// <summary>最多记这么多个 agent，超出时全部清空（只会让 /yes 多确认一次）。</summary>
    private const int MaxEntries = 256;

    /// <summary>按 pane ID 记的、给用户看过的确认框：当时的状态序号和确认框指纹。</summary>
    private readonly ConcurrentDictionary<string, (long Seq, string Fingerprint)> _shown = new(StringComparer.Ordinal);

    /// <summary>记下要给用户看的确认框。屏幕上没有认得出的确认框时，清掉这个 agent 之前的记录。</summary>
    /// <param name="agent">读屏幕之前取到的 agent 状态（用它的状态序号）。</param>
    /// <param name="screen">要给用户看的屏幕。</param>
    public void Remember(HerdrAgent agent, string screen)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (ApprovalNavigator.Fingerprint(screen) is not { } fingerprint)
        {
            _shown.TryRemove(agent.PaneId, out _);
            return;
        }

        if (_shown.Count >= MaxEntries)
        {
            _shown.Clear();
        }

        _shown[agent.PaneId] = (agent.StateChangeSeq, fingerprint);
    }

    /// <summary>屏幕上的确认框是不是给用户看过的那一个；是的话把记录作废，同一个确认框只能同意一次。</summary>
    /// <param name="agent">读屏幕之前取到的 agent 状态。</param>
    /// <param name="screen">当前屏幕。</param>
    /// <returns>比对结果；只有返回 <see cref="DialogCheck.Shown"/> 时才可以同意。</returns>
    public DialogCheck Consume(HerdrAgent agent, string screen)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!_shown.TryGetValue(agent.PaneId, out var shown))
        {
            return DialogCheck.NotShown;
        }

        if (shown.Seq != agent.StateChangeSeq || ApprovalNavigator.Fingerprint(screen) != shown.Fingerprint)
        {
            return DialogCheck.Changed;
        }

        // 只删和刚才比对的那条：同时到达的两个 /yes 只有一个能成功。
        return _shown.TryRemove(KeyValuePair.Create(agent.PaneId, shown)) ? DialogCheck.Shown : DialogCheck.NotShown;
    }
}
