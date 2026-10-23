using System.Globalization;
using System.Text;

namespace ChatPilot.Shared.Text;

/// <summary>把长文本切成多段，尽量在换行处切，不会把一个字（含 emoji）切成两半。</summary>
public static class TextChunker
{
    /// <summary>内容被截断时追加在最后一段末尾的提示。</summary>
    public const string TruncatedNotice = "\n…（内容太长，后面的已省略）";

    /// <summary>
    /// 切成每段不超过 <paramref name="maxChunkChars"/> 个字符、最多 <paramref name="maxChunks"/> 段。
    /// 放不下的部分截断并在最后一段末尾加提示；全是空白的段会被跳过。
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int maxChunkChars, int maxChunks)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunkChars, TruncatedNotice.Length + 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunks, 1);

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (chunks.Count == maxChunks - 1 && remaining.Length > maxChunkChars)
            {
                // 最后一段放不下了：截断并加提示。
                var room = maxChunkChars - TruncatedNotice.Length;
                chunks.Add(string.Concat(SafePrefix(remaining, room).TrimEnd(), TruncatedNotice));
                return chunks;
            }

            if (remaining.Length <= maxChunkChars)
            {
                if (remaining.Trim().Length > 0)
                {
                    chunks.Add(remaining.ToString());
                }

                break;
            }

            var window = SafePrefix(remaining, maxChunkChars);
            var cut = window.LastIndexOf('\n');
            if (cut < maxChunkChars / 2)
            {
                cut = window.Length;
            }

            if (cut == 0)
            {
                // 单个字符元素就超过上限（实际不会发生），只能硬切，防止死循环。
                cut = maxChunkChars;
            }

            var chunk = remaining[..cut].TrimEnd();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk.ToString());
            }

            remaining = remaining[cut..].TrimStart('\n');
        }

        return chunks;
    }

    /// <summary>截取最多 <paramref name="maxChars"/> 个 UTF-16 字符，不拆开代理对和组合字符。</summary>
    public static string Truncate(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxChars)
        {
            return text;
        }

        return SafePrefix(text, Math.Max(0, maxChars - 1)).ToString() + "…";
    }

    private static ReadOnlySpan<char> SafePrefix(ReadOnlySpan<char> text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var length = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text.ToString());
        while (enumerator.MoveNext())
        {
            var next = enumerator.ElementIndex + ((string)enumerator.Current).Length;
            if (next > maxChars)
            {
                break;
            }

            length = next;
        }

        return text[..length];
    }

    /// <summary>去掉每行末尾空白和首尾空行。</summary>
    public static string TrimLines(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line.TrimEnd()).Append('\n');
        }

        return builder.ToString().Trim('\n');
    }
}
