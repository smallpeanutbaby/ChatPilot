using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Text;

namespace ChatPilot.Server.Chat;

/// <summary>把结果整理成适合在微信里看的纯文本（微信不渲染 Markdown）。</summary>
public static class ReplyFormatter
{
    /// <summary>主动通知里正文的上限，留出余量让分段后不超过 4 条。</summary>
    private const int MaxEventTextChars = 14000;

    /// <summary><c>/help</c> 的内容。</summary>
    public const string Help = """
        📖 ChatPilot 指令

        /devices          设备列表
        /use 设备          切换默认设备
        /ls [设备]         列出 agent（带序号）
        @目标 内容         把任务交给 agent
        /read 目标 [行数]   看最近的输出
        /last 目标         补看最近一条回复
        /new [设备] 目录 [类型] [名字]
                          新开工作区并启动 agent
        /yes 目标 · /no 目标  同意 / 拒绝确认框
        /keys 目标 按键…    发送按键（如 down enter）
        /name 目标 新名字   给 agent 改名
        /butler [设备]     创建管家 agent
        /pair [设备名]     生成配对码
        /unpair 设备       撤销设备

        目标可以是 /ls 里的序号、名字、工作区名或 pane ID，
        前面加「设备/」可指定设备，例如 @mac/tideq。
        直接发文字（不带 / 或 @）会交给当前设备的管家。
        """;

    /// <summary>agent 状态对应的图标。</summary>
    public static string StatusIcon(string status) => status switch
    {
        "working" => "⏳",
        "idle" => "💤",
        "done" => "✅",
        "blocked" => "⚠️",
        _ => "❔",
    };

    /// <summary>agent 状态的中文说明。</summary>
    public static string StatusText(string status) => status switch
    {
        "working" => "工作中",
        "idle" => "空闲",
        "done" => "完成",
        "blocked" => "等你确认",
        _ => "未知",
    };

    /// <summary>agent 事件通知。</summary>
    public static string AgentEvent(string deviceName, AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        var name = agentEvent.Agent.DisplayName;
        var target = $"{deviceName}/{agentEvent.Agent.Name ?? agentEvent.Agent.PaneId}";
        var text = TextChunker.Truncate(agentEvent.Text ?? "", MaxEventTextChars);
        return agentEvent.Kind switch
        {
            AgentEventKind.Completed => $"✅ [{deviceName}] {name} 做完了：\n\n{text}",
            AgentEventKind.Blocked => $"⚠️ [{deviceName}] {name} 在等你确认：\n\n{text}\n\n回复 /yes {target} 同意，/no {target} 拒绝",
            _ => $"❌ [{deviceName}] {name} 出错了：{text}",
        };
    }

    /// <summary><c>/last</c> 的回复：和做完时的通知用同样的长度上限。</summary>
    public static string LastReply(string label, string text) =>
        $"💬 {label} 最近的回复：\n\n{TextChunker.Truncate(text, MaxEventTextChars)}";

    /// <summary>失败结果的回复文字。</summary>
    public static string Failure(CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return "❌ " + (outcome.Error?.Message ?? "失败了");
    }
}
