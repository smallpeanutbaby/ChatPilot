using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatPilot.Shared.Protocol;

/// <summary>协议统一的 JSON 设置：蛇形命名、忽略空值。</summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(WireMessage message) => JsonSerializer.Serialize(message, Options);

    public static byte[] SerializeToUtf8(WireMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <summary>解析一条消息；格式不对、类型未知时返回 false，不抛异常。</summary>
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

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 32,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
