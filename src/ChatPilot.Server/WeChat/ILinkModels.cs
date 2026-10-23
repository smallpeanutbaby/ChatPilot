using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatPilot.Server.WeChat;

// 腾讯 iLink 接口的数据结构，字段名和含义参照腾讯开源的 openclaw-weixin 插件（MIT）。

public sealed record QrCodeResponse(string? Qrcode, string? QrcodeImgContent);

public sealed record QrStatusResponse(
    string? Status,
    string? BotToken = null,
    string? IlinkBotId = null,
    string? Baseurl = null,
    string? IlinkUserId = null,
    string? RedirectHost = null);

public sealed record GetUpdatesResponse(
    int? Ret,
    int? Errcode,
    string? Errmsg,
    IReadOnlyList<WeixinMessage>? Msgs,
    string? GetUpdatesBuf,
    long? LongpollingTimeoutMs);

public sealed record WeixinMessage(
    ulong? MessageId,
    string? FromUserId,
    string? ToUserId,
    int? MessageType,
    int? MessageState,
    IReadOnlyList<MessageItem>? ItemList,
    string? ContextToken);

public sealed record MessageItem(int? Type, TextItem? TextItem, VoiceItem? VoiceItem);

public sealed record TextItem(string? Text);

public sealed record VoiceItem(string? Text);

public sealed record SendMessageResponse(int? Ret, string? Errmsg);

public static class ILinkConstants
{
    public const int MessageTypeUser = 1;
    public const int MessageTypeBot = 2;
    public const int MessageStateFinish = 2;
    public const int ItemTypeText = 1;
    public const int ItemTypeVoice = 3;

    /// <summary>bot token 失效。</summary>
    public const int StaleTokenErrorCode = -14;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 消息 ID 是 uint64，用 ulong 接收不丢精度；服务端偶尔用字符串表示数字。
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
