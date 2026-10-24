namespace ChatPilot.Shared.Media;

/// <summary>按文件头识别常见图片格式。服务器只接收这些格式，节点按它决定保存的扩展名（不信任任何外来的文件名）。</summary>
public static class ImageFormat
{
    /// <summary>识别出的扩展名（不带点）：png、jpg、gif、webp、bmp；不是这些格式时返回 null。</summary>
    /// <param name="data">文件内容（至少要包含文件头）。</param>
    /// <returns><c>png</c>、<c>jpg</c>、<c>gif</c>、<c>webp</c> 或 <c>bmp</c>；认不出时为 null。</returns>
    public static string? DetectExtension(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "png";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "jpg";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "gif";
        }

        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        return data.Length >= 26 && data.StartsWith("BM"u8) ? "bmp" : null;
    }
}
