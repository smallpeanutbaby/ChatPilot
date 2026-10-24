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
/// <param name="ProtocolVersion">节点的协议版本，必须等于 <see cref="ProtocolInfo.Version"/>。</param>
/// <param name="NodeVersion">节点程序版本，例如 <c>0.1.0</c>。</param>
/// <param name="Platform">操作系统描述，例如 <c>macOS 26.5.1 Arm64</c>。</param>
/// <param name="HostName">电脑名。</param>
/// <param name="Networks">这台电脑的有线 / 无线网卡（<c>/wake</c> 唤醒它时用）；旧版节点不发，为 null。</param>
public sealed record HelloMessage(
    int ProtocolVersion,
    string NodeVersion,
    string Platform,
    string HostName,
    IReadOnlyList<NetworkInterfaceInfo>? Networks = null) : WireMessage;

/// <summary>一块网卡：MAC 地址和它的 IPv4 网段。</summary>
/// <param name="Mac">MAC 地址，格式 <c>AA:BB:CC:DD:EE:FF</c>，见 <see cref="Validation.IsMacAddress"/>。</param>
/// <param name="Address">IPv4 地址；没有时为 null。</param>
/// <param name="PrefixLength">子网前缀长度（0–32）。</param>
public sealed record NetworkInterfaceInfo(string Mac, string? Address, int PrefixLength);

/// <summary>服务器确认握手成功。</summary>
/// <param name="DeviceId">设备 ID。</param>
/// <param name="DeviceName">设备名（配对时确定，可能和节点请求的不同）。</param>
public sealed record WelcomeMessage(string DeviceId, string DeviceName) : WireMessage;

/// <summary>服务器让节点执行一条指令。</summary>
/// <param name="Id">指令 ID，节点返回结果时原样带回。</param>
/// <param name="Command">要执行的指令。</param>
public sealed record CommandMessage(string Id, NodeCommand Command) : WireMessage;

/// <summary>节点返回指令结果。</summary>
/// <param name="Id">对应 <see cref="CommandMessage.Id"/>。</param>
/// <param name="Outcome">执行结果。</param>
public sealed record ResultMessage(string Id, CommandOutcome Outcome) : WireMessage;

/// <summary>节点主动上报的 agent 事件（做完、卡住、出错）。</summary>
/// <param name="Event">事件内容。</param>
public sealed record AgentEventMessage(AgentEvent Event) : WireMessage;

/// <summary>
/// 节点能执行的指令，用 <c>op</c> 字段区分。
/// 目标一律是 pane ID 或 agent 名字（由服务器把序号、工作区名等解析好再发）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(ListAgentsCommand), "list_agents")]
[JsonDerivedType(typeof(PromptCommand), "prompt")]
[JsonDerivedType(typeof(ReadCommand), "read")]
[JsonDerivedType(typeof(StartAgentCommand), "start_agent")]
[JsonDerivedType(typeof(SendKeysCommand), "send_keys")]
[JsonDerivedType(typeof(RespondCommand), "respond")]
[JsonDerivedType(typeof(RenameAgentCommand), "rename_agent")]
[JsonDerivedType(typeof(LastReplyCommand), "last_reply")]
[JsonDerivedType(typeof(MediaPromptCommand), "prompt_media")]
[JsonDerivedType(typeof(DiffCommand), "diff")]
[JsonDerivedType(typeof(StopAgentCommand), "stop_agent")]
[JsonDerivedType(typeof(CloseAgentCommand), "close_agent")]
[JsonDerivedType(typeof(WatchCommand), "watch")]
[JsonDerivedType(typeof(WakeCommand), "wake")]
public abstract record NodeCommand;

/// <summary>列出节点上所有 agent。</summary>
public sealed record ListAgentsCommand : NodeCommand;

/// <summary>
/// 把文字交给 agent；节点提交后立即返回，做完后通过 <see cref="AgentEventMessage"/> 上报。
/// agent 正在干活时节点先排队，等它做完再发。
/// </summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
/// <param name="Text">要交给 agent 的文字，不超过 <see cref="ProtocolInfo.MaxPromptChars"/> 个字符。</param>
public sealed record PromptCommand(string Target, string Text) : NodeCommand;

/// <summary>
/// 带图片的 <see cref="PromptCommand"/>：节点先从服务器下载图片，把保存位置附在文字后面再交给 agent。
/// 单独用一种指令而不是给 <see cref="PromptCommand"/> 加字段：旧节点会回"不支持"，而不是悄悄丢掉图片只发文字。
/// </summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
/// <param name="Text">要交给 agent 的文字。</param>
/// <param name="Attachments">图片，1 到 <see cref="ProtocolInfo.MaxAttachments"/> 张。</param>
public sealed record MediaPromptCommand(string Target, string Text, IReadOnlyList<MediaRef> Attachments) : NodeCommand;

/// <summary>服务器上暂存的一张图片，节点凭设备令牌从 <see cref="ProtocolInfo.MediaPath"/> 下载。</summary>
/// <param name="Id">下载 ID（随机生成，只给这条指令的目标设备用）。</param>
/// <param name="Size">字节数，不超过 <see cref="ProtocolInfo.MaxImageBytes"/>。</param>
/// <param name="Sha256">内容的 SHA-256（十六进制小写），节点下载后核对。</param>
public sealed record MediaRef(string Id, long Size, string Sha256);

/// <summary>看 agent 工作目录里的 git 改动（只读）。</summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
/// <param name="Path">只看这个文件的 diff（相对仓库里 agent 的工作目录）；为 null 时看改动概要。</param>
public sealed record DiffCommand(string Target, string? Path) : NodeCommand;

/// <summary>打断 agent 正在干的活（按 Esc），并清空排队的指令。</summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
public sealed record StopAgentCommand(string Target) : NodeCommand;

/// <summary>关闭 agent 所在的 pane（结束 agent 进程）。服务器负责先让主人确认。</summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
public sealed record CloseAgentCommand(string Target) : NodeCommand;

/// <summary>开始或停止"盯着" agent：不是通过 ChatPilot 派的活，做完或卡在确认框时也通知。</summary>
/// <param name="Target">pane ID 或 agent 名字；为 null 时表示这台电脑上的所有 agent。</param>
/// <param name="Enable">true 开始盯，false 停止。</param>
public sealed record WatchCommand(string? Target, bool Enable) : NodeCommand;

/// <summary>在节点所在的局域网里广播 Wake-on-LAN 唤醒包。</summary>
/// <param name="MacAddresses">要唤醒的网卡 MAC 地址，最多 <see cref="ProtocolInfo.MaxWakeAddresses"/> 个。</param>
public sealed record WakeCommand(IReadOnlyList<string> MacAddresses) : NodeCommand;

/// <summary>读 agent 最近的终端输出。</summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
/// <param name="Lines">行数，范围见 <see cref="ProtocolInfo.MinReadLines"/> 和 <see cref="ProtocolInfo.MaxReadLines"/>。</param>
public sealed record ReadCommand(string Target, int Lines) : NodeCommand;

/// <summary>新开工作区并启动 agent。</summary>
/// <param name="Directory">工作目录；为空时用节点的第一个允许目录。必须在节点的目录白名单内。</param>
/// <param name="Kind">agent 类型；为空时用节点的默认类型。</param>
/// <param name="Name">agent 名字；为空时由目录名生成。</param>
/// <param name="InitialPrompt">agent 就绪后自动发送的初始说明（用于创建管家）；为空时不发送。</param>
public sealed record StartAgentCommand(string? Directory, string? Kind, string? Name, string? InitialPrompt) : NodeCommand;

/// <summary>给 agent 发送按键（只能是白名单内的按键）。</summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
/// <param name="Keys">按键列表，例如 <c>down</c>、<c>enter</c>，见 <see cref="Validation.AllowedKeys"/>。</param>
public sealed record SendKeysCommand(string Target, IReadOnlyList<string> Keys) : NodeCommand;

/// <summary>回应 agent 的确认框：同意时自动选中不扩大权限的"Yes"类选项，拒绝时按 Esc。</summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
/// <param name="Approve">true 表示同意，false 表示拒绝。</param>
public sealed record RespondCommand(string Target, bool Approve) : NodeCommand;

/// <summary>给 agent 改名。</summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
/// <param name="Name">新名字，必须符合 <see cref="Validation.IsAgentName"/>。</param>
public sealed record RenameAgentCommand(string Target, string Name) : NodeCommand;

/// <summary>取 agent 最近一条回复（和做完时推送的内容一样），用来补看没收到的通知。</summary>
/// <param name="Target">pane ID 或 agent 名字。</param>
public sealed record LastReplyCommand(string Target) : NodeCommand;

/// <summary>指令结果：成功时带 <see cref="Data"/>，失败时带 <see cref="Error"/>。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Error">失败原因；成功时为 null。</param>
/// <param name="Data">结果数据；失败时为 null。</param>
public sealed record CommandOutcome(bool Ok, CommandError? Error, CommandData? Data)
{
    /// <summary>成功结果。</summary>
    /// <param name="data">结果数据。</param>
    /// <returns><see cref="Ok"/> 为 true、带 <paramref name="data"/> 的结果。</returns>
    public static CommandOutcome Success(CommandData data) => new(true, null, data);

    /// <summary>失败结果。</summary>
    /// <param name="code">错误码，见 <see cref="ErrorCodes"/>。</param>
    /// <param name="message">给用户看的说明。</param>
    /// <returns><see cref="Ok"/> 为 false、带错误原因的结果。</returns>
    public static CommandOutcome Failure(string code, string message) => new(false, new CommandError(code, message), null);
}

/// <summary>指令失败的原因。</summary>
/// <param name="Code">错误码，见 <see cref="ErrorCodes"/>。</param>
/// <param name="Message">给用户看的说明。</param>
public sealed record CommandError(string Code, string Message);

/// <summary>指令成功时的结果数据，用 <c>kind</c> 字段区分。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AgentListData), "agent_list")]
[JsonDerivedType(typeof(TextData), "text")]
[JsonDerivedType(typeof(AgentStartedData), "agent_started")]
[JsonDerivedType(typeof(AckData), "ack")]
public abstract record CommandData;

/// <summary><see cref="ListAgentsCommand"/> 的结果。</summary>
/// <param name="Agents">节点上的所有 agent。</param>
/// <param name="WatchAll">是否盯着这台电脑上的所有 agent（见 <see cref="WatchCommand"/>）。</param>
public sealed record AgentListData(IReadOnlyList<AgentSummary> Agents, bool WatchAll = false) : CommandData;

/// <summary>文本结果（例如 <see cref="ReadCommand"/> 读到的终端输出）。</summary>
/// <param name="Text">文本内容。</param>
public sealed record TextData(string Text) : CommandData;

/// <summary><see cref="StartAgentCommand"/> 的结果。</summary>
/// <param name="Agent">启动的 agent。</param>
/// <param name="Ready">是否已经可以接收指令；false 说明卡在确认框（例如"信任此目录"）。</param>
/// <param name="Screen">卡在确认框时的屏幕摘要；就绪时为 null。</param>
public sealed record AgentStartedData(AgentSummary Agent, bool Ready, string? Screen) : CommandData;

/// <summary>操作已完成的确认（例如"已发送"）。</summary>
/// <param name="Message">给用户看的说明。</param>
public sealed record AckData(string Message) : CommandData
{
    /// <summary>
    /// 指令已经交给 agent、没有别的要说明时的 <see cref="Message"/>。服务器看到它就用固定说法回复主人，
    /// 看到别的（排队了、跟踪名额满了……）就把节点的说明转给主人。
    /// </summary>
    public const string Sent = "已发送";
}

/// <summary>一个 agent 的概要信息。</summary>
/// <param name="PaneId">所在 pane，例如 <c>w1:p2</c>。</param>
/// <param name="Name">agent 名字；没起名时为 null。</param>
/// <param name="Kind">agent 类型，例如 <c>claude</c>；pane 里没识别出 agent 时为 null。</param>
/// <param name="Status">状态：idle、working、blocked、done、unknown。</param>
/// <param name="WorkspaceLabel">所在工作区的名字。</param>
/// <param name="Cwd">当前工作目录。</param>
/// <param name="Title">终端标题（Claude Code 会把当前任务写在这里）。</param>
/// <param name="Watched">是否被盯着（见 <see cref="WatchCommand"/>）。</param>
/// <param name="Queued">排队等着发给它的指令数。</param>
/// <param name="TaskSince">通过 ChatPilot 交给它、还没做完的任务是什么时候开始的；没有时为 null。</param>
public sealed record AgentSummary(
    string PaneId,
    string? Name,
    string? Kind,
    string Status,
    string? WorkspaceLabel,
    string? Cwd,
    string? Title,
    bool Watched = false,
    int Queued = 0,
    DateTimeOffset? TaskSince = null)
{
    /// <summary>给人看的名字：优先 agent 名字，其次工作区名，最后 pane ID。</summary>
    [JsonIgnore]
    public string DisplayName => Name ?? WorkspaceLabel ?? PaneId;
}

/// <summary>agent 事件的种类。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentEventKind>))]
public enum AgentEventKind
{
    /// <summary>做完了，事件文字是 agent 的最后一条回复。</summary>
    Completed,

    /// <summary>卡在确认框，事件文字是屏幕摘要。</summary>
    Blocked,

    /// <summary>出错或放弃等待，事件文字是原因。</summary>
    Failed,
}

/// <summary>节点上报的 agent 事件。</summary>
/// <param name="Kind">事件种类。</param>
/// <param name="Agent">相关的 agent。</param>
/// <param name="Text">回复内容、屏幕摘要或错误说明，含义见 <see cref="AgentEventKind"/>。</param>
/// <param name="Prompt">这一轮交给它的文字（截短到 <see cref="ProtocolInfo.MaxEventPromptChars"/> 字）；不知道时为 null。</param>
/// <param name="StartedAt">这一轮任务开始的时间；不知道时为 null。</param>
/// <param name="Queued">还有几条排队的指令等着发给它。</param>
public sealed record AgentEvent(
    AgentEventKind Kind,
    AgentSummary Agent,
    string? Text,
    string? Prompt = null,
    DateTimeOffset? StartedAt = null,
    int Queued = 0);

/// <summary>节点用配对码换设备令牌（HTTP <c>POST /api/pair</c> 的请求体）。</summary>
/// <param name="Code">配对码，例如 <c>ABCD-EFGH</c>（大小写、短横线都可以）。</param>
/// <param name="DeviceName">节点希望使用的设备名；发 /pair 时指定了设备名的话以那个为准。</param>
/// <param name="Platform">操作系统描述。</param>
public sealed record PairRequest(string Code, string DeviceName, string Platform);

/// <summary>配对成功的响应。</summary>
/// <param name="DeviceId">设备 ID。</param>
/// <param name="DeviceName">最终的设备名（重名时会加后缀）。</param>
/// <param name="Token">设备令牌，相当于密码，只在这里出现一次；服务器只保存它的哈希。</param>
public sealed record PairResponse(string DeviceId, string DeviceName, string Token);

/// <summary>HTTP 接口的错误响应。</summary>
/// <param name="Error">错误说明。</param>
public sealed record ErrorResponse(string Error);
