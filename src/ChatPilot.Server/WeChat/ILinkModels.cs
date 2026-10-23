using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatPilot.Server.WeChat;

// 腾讯 iLink 接口的数据结构，字段名和含义参照腾讯开源的 openclaw-weixin 插件（MIT）。
// 字段都是可空的：接口没有公开文档，按"有就用、没有不报错"处理。

/// <summary><c>get_bot_qrcode</c> 的响应。</summary>
/// <param name="Qrcode">二维码 ID，轮询扫码状态时使用。</param>
/// <param name="QrcodeImgContent">二维码内容（登录链接），用它生成二维码图片。</param>
public sealed record QrCodeResponse(string? Qrcode, string? QrcodeImgContent);

/// <summary><c>get_qrcode_status</c> 的响应。</summary>
/// <param name="Status">扫码状态：wait、scaned、confirmed、expired、scaned_but_redirect、need_verifycode、verify_code_blocked、binded_redirect。</param>
/// <param name="BotToken">确认后下发的 bot token，之后所有接口都用它鉴权。</param>
/// <param name="IlinkBotId">bot ID。</param>
/// <param name="Baseurl">之后应该使用的接口地址。</param>
/// <param name="IlinkUserId">扫码人的微信用户 ID，也就是"主人"。</param>
/// <param name="RedirectHost">需要切换到的轮询服务器（IDC 重定向）。</param>
public sealed record QrStatusResponse(
    string? Status,
    string? BotToken = null,
    string? IlinkBotId = null,
    string? Baseurl = null,
    string? IlinkUserId = null,
    string? RedirectHost = null);

/// <summary><c>getupdates</c>（长轮询收消息）的响应。</summary>
/// <param name="Ret">返回码，0 表示成功。</param>
/// <param name="Errcode">错误码，-14 表示 bot token 失效。</param>
/// <param name="Errmsg">错误说明。</param>
/// <param name="Msgs">新消息。</param>
/// <param name="GetUpdatesBuf">新的同步游标，下次请求时带上。</param>
/// <param name="LongpollingTimeoutMs">服务器建议的下次长轮询超时（毫秒）。</param>
public sealed record GetUpdatesResponse(
    int? Ret,
    int? Errcode,
    string? Errmsg,
    IReadOnlyList<WeixinMessage>? Msgs,
    string? GetUpdatesBuf,
    long? LongpollingTimeoutMs);

/// <summary>一条微信消息。</summary>
/// <param name="MessageId">消息 ID（uint64，用 ulong 接收不丢精度）。</param>
/// <param name="FromUserId">发送人。</param>
/// <param name="ToUserId">接收人。</param>
/// <param name="MessageType">1 是用户发的，2 是 bot 发的，见 <see cref="ILinkConstants"/>。</param>
/// <param name="MessageState">消息状态，2 表示已完成。</param>
/// <param name="ItemList">消息内容（文字、语音、图片……）。</param>
/// <param name="ContextToken">会话上下文令牌，回复这条消息时要带上。</param>
public sealed record WeixinMessage(
    ulong? MessageId,
    string? FromUserId,
    string? ToUserId,
    int? MessageType,
    int? MessageState,
    IReadOnlyList<MessageItem>? ItemList,
    string? ContextToken);

/// <summary>消息里的一项内容。</summary>
/// <param name="Type">1 文字、3 语音，其他类型（图片、文件、视频）暂不处理。</param>
/// <param name="TextItem">文字内容。</param>
/// <param name="VoiceItem">语音内容（带微信自动转写的文字）。</param>
public sealed record MessageItem(int? Type, TextItem? TextItem, VoiceItem? VoiceItem);

/// <summary>文字内容。</summary>
/// <param name="Text">文字。</param>
public sealed record TextItem(string? Text);

/// <summary>语音内容。</summary>
/// <param name="Text">微信自动转写的文字。</param>
public sealed record VoiceItem(string? Text);

/// <summary><c>sendmessage</c> 的响应。</summary>
/// <param name="Ret">返回码，0 表示成功。</param>
/// <param name="Errmsg">错误说明。</param>
public sealed record SendMessageResponse(int? Ret, string? Errmsg);

/// <summary>iLink 协议常量和 JSON 设置。</summary>
public static class ILinkConstants
{
    /// <summary>用户发的消息。</summary>
    public const int MessageTypeUser = 1;

    /// <summary>bot 发的消息。</summary>
    public const int MessageTypeBot = 2;

    /// <summary>消息已完成（不是流式生成中）。</summary>
    public const int MessageStateFinish = 2;

    /// <summary>文字内容。</summary>
    public const int ItemTypeText = 1;

    /// <summary>语音内容。</summary>
    public const int ItemTypeVoice = 3;

    /// <summary>bot token 失效。</summary>
    public const int StaleTokenErrorCode = -14;

    /// <summary>iLink 接口的 JSON 设置：蛇形命名，写出时忽略空值。</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 消息 ID 是 uint64，用 ulong 接收不丢精度；服务端偶尔用字符串表示数字。
        NumberHandling = JsonNumberHandling.AllowReadingFromString,

        // 中文和标点按原样写成 UTF-8，和腾讯官方插件的 JSON.stringify 一致。默认的编码器会转成 \uXXXX，
        // 真机上消息在中文引号（“）处被截断。这里的 JSON 只发给 iLink，不会嵌进网页，不需要 HTML 安全的转义。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
