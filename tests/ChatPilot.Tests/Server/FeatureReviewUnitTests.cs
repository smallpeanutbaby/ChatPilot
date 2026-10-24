using ChatPilot.Server.Chat;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace ChatPilot.Tests.Server;

/// <summary>新功能审查发现的问题的单元测试。</summary>
public class FeatureReviewUnitTests
{
    private static readonly AgentSummary Api = new("w1:p1", "api", "claude", "idle", null, null, null);
    private static readonly AgentSummary Web = new("w2:p1", "web", "claude", "idle", null, null, null);
    private static readonly AgentSummary Butler = new("w3:p1", "butler", "claude", "idle", null, null, null);

    [Fact]
    public void Only_events_of_work_the_owner_dispatched_change_the_recent_agent()
    {
        // 审查 #2：盯着的 agent、定时任务的事件不换默认目标；主人派的活有了结果才换。
        var state = new ConversationState(new FakeTimeProvider());
        state.Touch(AgentRef.From("d1", "mac", Api));

        Assert.False(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Web, "做完了")));
        Assert.Equal("api", state.Recent?.Name);

        // 卡在确认框的照样记为在等确认（/yes 的候选），只是不换默认目标。
        Assert.False(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Blocked, Web, "确认？")));
        Assert.Equal("api", state.Recent?.Name);
        Assert.Equal("web", Assert.Single(state.Blocked()).Name);

        // 派过活：等确认、做完都换过去；做完（没有排队的）之后这次派活就结束了。
        state.Dispatched("d1", Web.PaneId, Web.Name);
        Assert.True(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Blocked, Web, "确认？")));
        Assert.Equal("web", state.Recent?.Name);
        state.Touch(AgentRef.From("d1", "mac", Api));
        Assert.True(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Web, "一", Queued: 1)));
        state.Touch(AgentRef.From("d1", "mac", Api));
        Assert.True(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Web, "二")));
        state.Touch(AgentRef.From("d1", "mac", Api));
        Assert.False(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Web, "电脑上派的")));
        Assert.Equal("api", state.Recent?.Name);

        // 交给管家的文字只知道名字；别的设备上同名的 agent 不算。
        state.Dispatched("d1", paneId: null, "butler");
        Assert.False(state.OnEvent("d2", "win", new AgentEvent(AgentEventKind.Completed, Butler, "好")));
        Assert.True(state.OnEvent("d1", "mac", new AgentEvent(AgentEventKind.Completed, Butler, "好")));
        Assert.Equal("butler", state.Recent?.Name);
    }

    [Fact]
    public void Blocked_agents_learned_from_command_results_are_candidates()
    {
        var time = new FakeTimeProvider();
        var state = new ConversationState(time);
        state.NoteBlocked(AgentRef.From("d1", "mac", Api));
        Assert.Equal("api", Assert.Single(state.Blocked()).Name);
        state.ForgetAgent("d1", Api.PaneId);
        Assert.Empty(state.Blocked());

        state.NoteBlocked(AgentRef.From("d1", "mac", Api));
        time.Advance(TimeSpan.FromHours(7));
        Assert.Empty(state.Blocked());
    }
}
