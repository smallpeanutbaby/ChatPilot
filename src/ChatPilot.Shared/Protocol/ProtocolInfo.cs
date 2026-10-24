namespace ChatPilot.Shared.Protocol;

/// <summary>服务器和节点共用的协议常量。</summary>
public static class ProtocolInfo
{
    /// <summary>协议版本；两边不一致时服务器拒绝连接。</summary>
    public const int Version = 1;

    /// <summary>节点连接服务器的 WebSocket 路径。</summary>
    public const string NodeWebSocketPath = "/ws/node";

    /// <summary>用配对码换设备令牌的 HTTP 路径。</summary>
    public const string PairPath = "/api/pair";

    /// <summary>健康检查路径，返回 <c>ok</c>。</summary>
    public const string HealthPath = "/healthz";

    /// <summary>WebSocket 单条消息上限：1 MiB。</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>发给 agent 的文字上限（字符数）。</summary>
    public const int MaxPromptChars = 16 * 1024;

    /// <summary><c>/read</c> 最少读几行。</summary>
    public const int MinReadLines = 1;

    /// <summary><c>/read</c> 最多读几行。</summary>
    public const int MaxReadLines = 500;

    /// <summary><c>/read</c> 不指定行数时读几行。</summary>
    public const int DefaultReadLines = 60;

    /// <summary>一次最多发送的按键数。</summary>
    public const int MaxKeysPerCommand = 20;

    /// <summary>节点下载随指令发来的图片的路径前缀，后面接 <see cref="MediaRef.Id"/>；要带设备令牌。</summary>
    public const string MediaPath = "/api/media/";

    /// <summary>单张图片的大小上限：10 MiB。</summary>
    public const int MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>一条指令最多带几张图片。</summary>
    public const int MaxAttachments = 4;

    /// <summary>
    /// 节点下载一条指令所带全部图片的时限（秒），不占用发给 agent 的那部分时间。
    /// 服务器等带图片的指令时，要在普通指令的等待时间上再加这么多。
    /// </summary>
    public const int MediaDownloadSeconds = 90;

    /// <summary>一次最多唤醒几个 MAC 地址（一台电脑的网卡数）。</summary>
    public const int MaxWakeAddresses = 8;

    /// <summary>事件里附带的任务文字最多多少字（只用于历史记录的摘要）。</summary>
    public const int MaxEventPromptChars = 200;
}

/// <summary>服务器主动关闭节点连接时使用的原因，节点据此决定是重连还是退出。</summary>
public static class CloseReasons
{
    /// <summary>同一个设备令牌又建立了新连接，旧连接被替换。收到这个的节点说明有另一个实例在运行，应当退出。</summary>
    public const string Replaced = "replaced by a new connection";

    /// <summary>设备已被 /unpair 撤销。</summary>
    public const string Revoked = "device revoked";

    /// <summary>服务器正在关闭，节点应当稍后重连。</summary>
    public const string ShuttingDown = "server shutting down";
}

/// <summary>指令失败时的错误码（<see cref="CommandError.Code"/>）。</summary>
public static class ErrorCodes
{
    /// <summary>参数不合法（目标格式、行数、按键、名字等）。</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>找不到目标（agent、pane 或确认框）。</summary>
    public const string NotFound = "not_found";

    /// <summary>不允许的操作（例如目录不在白名单内）。</summary>
    public const string Forbidden = "forbidden";

    /// <summary>agent 正在等用户确认，不能接收新指令。</summary>
    public const string AgentBlocked = "agent_blocked";

    /// <summary>herdr 返回了其他错误。</summary>
    public const string HerdrError = "herdr_error";

    /// <summary>herdr 无法启动（没装、路径不对或服务没运行）。</summary>
    public const string HerdrUnavailable = "herdr_unavailable";

    /// <summary>超时。</summary>
    public const string Timeout = "timeout";

    /// <summary>设备不在线。</summary>
    public const string Offline = "offline";

    /// <summary>节点正忙（同时执行的指令或跟踪的任务太多）。</summary>
    public const string Busy = "busy";

    /// <summary>内部错误，详情见日志。</summary>
    public const string Internal = "internal";
}
