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
        /pending          待办：谁在等确认、谁在干活
        @目标 内容         把任务交给 agent（它在忙就排队）
        /re 内容           交给最近打过交道的 agent
        /read [目标] [行数] 看最近的输出
        /last [目标]       补看最近一条回复
        /diff [目标] [文件] 看 git 改动
        /yes [目标] · /no [目标]  同意 / 拒绝确认框
        /keys 目标 按键…    发送按键（如 down enter）
        /stop [目标]       打断它，清空排队的指令
        /close 目标        关闭 agent（会先让你确认）
        /new [设备] 目录 [类型] [名字]
                          新开工作区并启动 agent
        /name 目标 新名字   给 agent 改名
        /watch [目标|all]  盯着：电脑上派的活做完也通知
        /unwatch 目标|all  不再盯着
        /at 时间 内容       定时派活（/at 23:00 @api 跑测试）
        /history [条数]    最近做完的任务
        /butler [设备]     创建管家 agent
        /wake 设备         唤醒睡着的电脑
        /pair [设备名]     生成配对码
        /unpair 设备       撤销设备

        目标可以是 /ls 里的序号、名字、工作区名或 pane ID，
        前面加「设备/」可指定设备，例如 @mac/tideq。
        不写目标时用最近打过交道的 agent；/yes、/no 优先用在等确认的那个。
        直接发文字（不带 / 或 @）会交给当前设备的管家。
        先发图片再派活，图片会一起交给 agent（/drop 丢掉）。
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
        var message = agentEvent.Kind switch
        {
            AgentEventKind.Completed => $"✅ [{deviceName}] {name} 做完了：\n\n{text}",
            AgentEventKind.Blocked => $"⚠️ [{deviceName}] {name} 在等你确认：\n\n{text}\n\n回复 /yes {target} 同意，/no {target} 拒绝",
            _ => $"❌ [{deviceName}] {name} 出错了：{text}",
        };
        return agentEvent.Queued > 0 && agentEvent.Kind != AgentEventKind.Blocked
            ? $"{message}\n\n📥 还有 {agentEvent.Queued} 条排队的指令，会接着发给它"
            : message;
    }

    /// <summary>给人看的时长：不到 1 分钟、N 分钟、N 小时、N 天。</summary>
    public static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "不到 1 分钟",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} 分钟",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} 小时",
        _ => $"{(int)span.TotalDays} 天",
    };

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
