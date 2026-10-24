using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatPilot.Shared.Protocol;

/// <summary>
/// 协议统一的 JSON 设置：蛇形命名、严格校验。
/// 所有字段都必须出现（值为 null 的也要显式写出），不可空的字段不能是 null；缺字段的消息直接解析失败。
/// </summary>
public static class ProtocolJson
{
    /// <summary>协议使用的序列化设置（只读，可以共享）。</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>把消息序列化成 JSON 字符串。</summary>
    /// <param name="message">要序列化的消息。</param>
    /// <returns>带 <c>type</c> 字段的 JSON 字符串。</returns>
    public static string Serialize(WireMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>把消息序列化成 UTF-8 字节（WebSocket 发送用）。</summary>
    /// <param name="message">要序列化的消息。</param>
    /// <returns>JSON 的 UTF-8 字节。</returns>
    public static byte[] SerializeToUtf8(WireMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <summary>解析一条消息；格式不对、类型未知时返回 false，不抛异常。</summary>
    /// <param name="utf8">一条完整消息的 UTF-8 字节。</param>
    /// <param name="message">解析出的消息；失败时为 null。</param>
    /// <returns>解析成功时为 true；JSON 不合法、缺字段、类型未知或内容是 <c>null</c> 时为 false。</returns>
    public static bool TryDeserialize(ReadOnlySpan<byte> utf8, out WireMessage? message)
    {
        try
        {
            message = JsonSerializer.Deserialize<WireMessage>(utf8, Options);
            return message is not null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            message = null;
            return false;
        }
    }

    /// <summary>创建 <see cref="Options"/>：蛇形命名、null 字段照样写出、严格按可空注解和必填构造参数校验，然后设为只读。</summary>
    /// <returns>已设为只读的序列化设置。</returns>
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 32,

            // 缺字段、或者不可空的字段（例如 id）是 null 时解析失败，而不是得到一个 null 继续往下走。
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
