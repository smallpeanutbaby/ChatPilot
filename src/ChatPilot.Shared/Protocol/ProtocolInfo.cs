namespace ChatPilot.Shared.Protocol;

/// <summary>服务器和节点共用的协议常量。</summary>
public static class ProtocolInfo
{
    /// <summary>协议版本；两边不一致时服务器拒绝连接。</summary>
    public const int Version = 1;

    public const string NodeWebSocketPath = "/ws/node";
    public const string PairPath = "/api/pair";
    public const string HealthPath = "/healthz";

    /// <summary>WebSocket 单帧（一条完整消息）上限：1 MiB。</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>发给 agent 的文字上限（字符数）。</summary>
    public const int MaxPromptChars = 16 * 1024;

    public const int MinReadLines = 1;
    public const int MaxReadLines = 500;
    public const int DefaultReadLines = 60;

    /// <summary>一次最多发送的按键数。</summary>
    public const int MaxKeysPerCommand = 20;
}

/// <summary>节点返回的错误码。</summary>
public static class ErrorCodes
{
    public const string InvalidArgument = "invalid_argument";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string AgentBlocked = "agent_blocked";
    public const string HerdrError = "herdr_error";
    public const string HerdrUnavailable = "herdr_unavailable";
    public const string Timeout = "timeout";
    public const string Offline = "offline";
    public const string Busy = "busy";
    public const string Internal = "internal";
}
