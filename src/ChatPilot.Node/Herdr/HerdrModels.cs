using System.Text.Json;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Node.Herdr;

/// <summary>herdr 返回的错误。<see cref="Code"/> 是 herdr 的错误码，例如 <c>agent_not_found</c>、<c>timeout</c>。</summary>
public sealed class HerdrException(string code, string message) : Exception(message)
{
    /// <summary>herdr 的错误码。</summary>
    public string Code { get; } = code;
}

/// <summary>herdr 提供的 agent 会话记录引用：<see cref="Kind"/> 为 <c>path</c> 时是文件路径，为 <c>id</c> 时是会话 ID。</summary>
/// <param name="Kind"><c>path</c> 或 <c>id</c>。</param>
/// <param name="Value">文件路径或会话 ID。</param>
public sealed record HerdrAgentSession(string Kind, string Value);

/// <summary>herdr 里的一个 agent（占据某个 pane 的 Claude Code、Codex 等）。</summary>
/// <param name="PaneId">所在 pane，例如 <c>w1:p2</c>；关闭后不会被复用。</param>
/// <param name="Name">agent 名字；没起名时为 null，agent 退出后自动清除。</param>
/// <param name="Kind">herdr 识别出的类型，例如 <c>claude</c>。</param>
/// <param name="Status">状态，取值见 <see cref="AgentStatus"/>。</param>
/// <param name="WorkspaceId">所在工作区 ID，例如 <c>w1</c>。</param>
/// <param name="Cwd">当前工作目录。</param>
/// <param name="Title">终端标题。</param>
/// <param name="Session">会话记录引用；herdr 的 agent 集成没装时为 null。</param>
/// <param name="StateChangeSeq">状态变化序号，每变一次加一；用来判断提交指令后 agent 有没有开始干活。</param>
public sealed record HerdrAgent(
    string PaneId,
    string? Name,
    string? Kind,
    string Status,
    string? WorkspaceId,
    string? Cwd,
    string? Title,
    HerdrAgentSession? Session,
    long StateChangeSeq = 0)
{
    /// <summary>空闲或已完成，都表示可以接收新指令。</summary>
    public bool IsReady => Status is AgentStatus.Idle or AgentStatus.Done;

    /// <summary>转成协议里的概要信息。</summary>
    public AgentSummary ToSummary(string? workspaceLabel) =>
        new(PaneId, Name, Kind, Status, workspaceLabel, Cwd, Title);

    /// <summary>解析 herdr 的 agent 对象；不是对象或缺少 pane_id 时返回 null，其他字段缺失时尽量容错。</summary>
    public static HerdrAgent? TryParse(JsonElement element)
    {
        if (GetString(element, "pane_id") is not { } paneId)
        {
            return null;
        }

        var session = element.TryGetProperty("agent_session", out var s)
            && GetString(s, "kind") is { } kind && GetString(s, "value") is { } value
                ? new HerdrAgentSession(kind, value)
                : null;
        var seq = element.TryGetProperty("state_change_seq", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var parsed)
            ? parsed
            : 0;

        return new HerdrAgent(
            paneId,
            GetString(element, "name"),
            GetString(element, "agent"),
            GetString(element, "agent_status") ?? AgentStatus.Unknown,
            GetString(element, "workspace_id"),
            GetString(element, "foreground_cwd") ?? GetString(element, "cwd"),
            GetString(element, "terminal_title_stripped"),
            session,
            seq);
    }

    /// <summary>安全地取字符串字段：不是对象、没有这个字段或字段不是字符串时返回 null。</summary>
    internal static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>herdr 工作区。</summary>
/// <param name="WorkspaceId">工作区 ID，例如 <c>w1</c>。</param>
/// <param name="Label">工作区名（新建时用目录名）。</param>
public sealed record HerdrWorkspace(string WorkspaceId, string? Label);

/// <summary>herdr 的 agent 状态取值。</summary>
public static class AgentStatus
{
    /// <summary>空闲，可以接收指令。</summary>
    public const string Idle = "idle";

    /// <summary>正在干活。</summary>
    public const string Working = "working";

    /// <summary>herdr 识别出 agent 正在显示确认框或提问。</summary>
    public const string Blocked = "blocked";

    /// <summary>做完了上一个任务（还没被查看过）。</summary>
    public const string Done = "done";

    /// <summary>有 agent，但 herdr 无法判断状态。</summary>
    public const string Unknown = "unknown";
}
