using System.Text.Json;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Node.Herdr;

/// <summary>herdr 返回的错误，<see cref="Code"/> 是 herdr 的错误码，例如 <c>agent_not_found</c>。</summary>
public sealed class HerdrException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>herdr 会话记录的引用：<c>path</c> 是文件路径，<c>id</c> 是会话 ID。</summary>
public sealed record HerdrAgentSession(string Kind, string Value);

public sealed record HerdrAgent(
    string PaneId,
    string? Name,
    string? Kind,
    string Status,
    string? WorkspaceId,
    string? Cwd,
    string? Title,
    HerdrAgentSession? Session)
{
    public bool IsReady => Status is AgentStatus.Idle or AgentStatus.Done;

    public AgentSummary ToSummary(string? workspaceLabel) =>
        new(PaneId, Name, Kind, Status, workspaceLabel, Cwd, Title);

    /// <summary>解析 herdr 的 agent 对象，缺字段时尽量容错。</summary>
    public static HerdrAgent Parse(JsonElement element)
    {
        var session = element.TryGetProperty("agent_session", out var s) && s.ValueKind == JsonValueKind.Object
            && GetString(s, "kind") is { } kind && GetString(s, "value") is { } value
                ? new HerdrAgentSession(kind, value)
                : null;

        return new HerdrAgent(
            GetString(element, "pane_id") ?? throw new HerdrException("bad_response", "herdr 返回的 agent 缺少 pane_id"),
            GetString(element, "name"),
            GetString(element, "agent"),
            GetString(element, "agent_status") ?? AgentStatus.Unknown,
            GetString(element, "workspace_id"),
            GetString(element, "foreground_cwd") ?? GetString(element, "cwd"),
            GetString(element, "terminal_title_stripped"),
            session);
    }

    internal static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record HerdrWorkspace(string WorkspaceId, string? Label);

/// <summary>herdr 的 agent 状态。</summary>
public static class AgentStatus
{
    public const string Idle = "idle";
    public const string Working = "working";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public const string Unknown = "unknown";
}
