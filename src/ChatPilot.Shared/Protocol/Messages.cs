using System.Text.Json.Serialization;

namespace ChatPilot.Shared.Protocol;

/// <summary>节点和服务器之间的一条 WebSocket 消息，用 <c>type</c> 字段区分类型。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(CommandMessage), "command")]
[JsonDerivedType(typeof(ResultMessage), "result")]
[JsonDerivedType(typeof(AgentEventMessage), "agent_event")]
public abstract record WireMessage;

/// <summary>节点连上后发的第一条消息。</summary>
public sealed record HelloMessage(int ProtocolVersion, string NodeVersion, string Platform, string HostName) : WireMessage;

/// <summary>服务器确认握手成功。</summary>
public sealed record WelcomeMessage(string DeviceId, string DeviceName) : WireMessage;

/// <summary>服务器让节点执行一条指令。</summary>
public sealed record CommandMessage(string Id, NodeCommand Command) : WireMessage;

/// <summary>节点返回指令结果，<see cref="Id"/> 对应 <see cref="CommandMessage.Id"/>。</summary>
public sealed record ResultMessage(string Id, CommandOutcome Outcome) : WireMessage;

/// <summary>节点主动上报的 agent 事件（做完、卡住、出错）。</summary>
public sealed record AgentEventMessage(AgentEvent Event) : WireMessage;

/// <summary>节点能执行的指令，用 <c>op</c> 字段区分。目标一律是 pane ID 或 agent 名字。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(ListAgentsCommand), "list_agents")]
[JsonDerivedType(typeof(PromptCommand), "prompt")]
[JsonDerivedType(typeof(ReadCommand), "read")]
[JsonDerivedType(typeof(StartAgentCommand), "start_agent")]
[JsonDerivedType(typeof(SendKeysCommand), "send_keys")]
[JsonDerivedType(typeof(RespondCommand), "respond")]
[JsonDerivedType(typeof(RenameAgentCommand), "rename_agent")]
public abstract record NodeCommand;

public sealed record ListAgentsCommand : NodeCommand;

public sealed record PromptCommand(string Target, string Text) : NodeCommand;

public sealed record ReadCommand(string Target, int Lines) : NodeCommand;

/// <summary>
/// 新开工作区并启动 agent。<see cref="Directory"/> 为空时用节点的第一个白名单目录；
/// <see cref="Kind"/> 为空时用节点的默认类型；<see cref="InitialPrompt"/> 会在 agent 就绪后发送。
/// </summary>
public sealed record StartAgentCommand(string? Directory, string? Kind, string? Name, string? InitialPrompt) : NodeCommand;

public sealed record SendKeysCommand(string Target, IReadOnlyList<string> Keys) : NodeCommand;

/// <summary>回应 agent 的确认框：同意时自动选中"Yes"一类的选项，拒绝时按 Esc。</summary>
public sealed record RespondCommand(string Target, bool Approve) : NodeCommand;

public sealed record RenameAgentCommand(string Target, string Name) : NodeCommand;

/// <summary>指令结果：成功时带 <see cref="Data"/>，失败时带 <see cref="Error"/>。</summary>
public sealed record CommandOutcome(bool Ok, CommandError? Error, CommandData? Data)
{
    public static CommandOutcome Success(CommandData data) => new(true, null, data);

    public static CommandOutcome Failure(string code, string message) => new(false, new CommandError(code, message), null);
}

public sealed record CommandError(string Code, string Message);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AgentListData), "agent_list")]
[JsonDerivedType(typeof(TextData), "text")]
[JsonDerivedType(typeof(AgentStartedData), "agent_started")]
[JsonDerivedType(typeof(AckData), "ack")]
public abstract record CommandData;

public sealed record AgentListData(IReadOnlyList<AgentSummary> Agents) : CommandData;

public sealed record TextData(string Text) : CommandData;

/// <summary>agent 已启动。<see cref="Ready"/> 为 false 时说明卡在确认框，<see cref="Screen"/> 是屏幕摘要。</summary>
public sealed record AgentStartedData(AgentSummary Agent, bool Ready, string? Screen) : CommandData;

public sealed record AckData(string Message) : CommandData;

/// <summary>一个 agent 的概要信息。</summary>
public sealed record AgentSummary(
    string PaneId,
    string? Name,
    string? Kind,
    string Status,
    string? WorkspaceLabel,
    string? Cwd,
    string? Title)
{
    /// <summary>给人看的名字：优先 agent 名字，其次工作区名，最后 pane ID。</summary>
    [JsonIgnore]
    public string DisplayName => Name ?? WorkspaceLabel ?? PaneId;
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentEventKind>))]
public enum AgentEventKind
{
    Completed,
    Blocked,
    Failed,
}

/// <summary>agent 事件；<see cref="Text"/> 是回复内容、屏幕摘要或错误说明。</summary>
public sealed record AgentEvent(AgentEventKind Kind, AgentSummary Agent, string? Text);

/// <summary>节点用配对码换设备令牌。</summary>
public sealed record PairRequest(string Code, string DeviceName, string Platform);

public sealed record PairResponse(string DeviceId, string DeviceName, string Token);

public sealed record ErrorResponse(string Error);
