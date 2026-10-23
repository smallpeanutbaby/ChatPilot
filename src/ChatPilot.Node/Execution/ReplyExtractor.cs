using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatPilot.Shared.Text;

namespace ChatPilot.Node.Execution;

/// <summary>从 Claude 的会话记录或终端输出里取出"最后一条回复"。</summary>
public static partial class ReplyExtractor
{
    private const int TranscriptTailBytes = 4 * 1024 * 1024;
    private const int FallbackLines = 30;
    private const int RightAlignedIndent = 30;

    /// <summary>Claude Code 的状态行、加载动画都从第 0 列的这些符号开始；回复正文不会从第 0 列开始写这些符号。</summary>
    private static readonly char[] SpinnerGlyphs = ['✻', '✳', '✶', '✽', '✢', '✺', '·', '*', '∗'];

    /// <summary>
    /// 从终端输出里提取最后一条回复：
    /// 去掉底部输入框 → 找到最后一次用户输入（第 0 列的 <c>❯</c>）→ 取其后最后一个文字回复块（<c>⏺</c> 开头且不是工具调用）。
    /// 识别不出结构时退回到最后 30 行。
    /// </summary>
    public static string FromTerminal(string screen, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var lines = SplitLines(screen);
        var content = lines.Take(FindInputBoxStart(lines)).ToList();

        var start = 0;
        for (var i = content.Count - 1; i >= 0; i--)
        {
            if (IsUserPromptLine(content[i]))
            {
                start = i + 1;
                break;
            }
        }

        var body = content.Skip(start).ToList();
        var replyStart = -1;
        for (var i = body.Count - 1; i >= 0; i--)
        {
            if (body[i].StartsWith("⏺", StringComparison.Ordinal) && !IsToolCall(body[i]))
            {
                replyStart = i;
                break;
            }
        }

        string reply;
        if (replyStart < 0)
        {
            reply = TextChunker.TrimLines(body.Where(l => !IsChrome(l)).Where(l => l.Length > 0).TakeLast(FallbackLines));
        }
        else
        {
            var replyLines = new List<string>();
            foreach (var line in body.Skip(replyStart))
            {
                if (IsChrome(line))
                {
                    continue;
                }

                replyLines.Add(replyLines.Count == 0
                    ? line.TrimStart('⏺').TrimStart()
                    : line.StartsWith("  ", StringComparison.Ordinal) ? line[2..] : line);
            }

            reply = TextChunker.TrimLines(CollapseBlankLines(replyLines));
        }

        return TakeTail(reply, maxChars);
    }

    /// <summary>连续空行只保留一行（Claude 界面里回复和输入框之间常有大片空白）。</summary>
    private static IEnumerable<string> CollapseBlankLines(IEnumerable<string> lines)
    {
        var previousBlank = false;
        foreach (var line in lines)
        {
            var blank = line.Trim().Length == 0;
            if (!blank || !previousBlank)
            {
                yield return blank ? "" : line;
            }

            previousBlank = blank;
        }
    }

    /// <summary>
    /// 从 Claude 的 jsonl 会话记录里取最后一次用户输入之后、助手说的所有文字。
    /// 只读文件末尾 4 MiB；格式不认识时返回 null。
    /// </summary>
    public static string? FromClaudeTranscript(string path, int maxChars)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TranscriptTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
            if (start > 0)
            {
                var firstBreak = text.IndexOf('\n', StringComparison.Ordinal);
                text = firstBreak >= 0 ? text[(firstBreak + 1)..] : "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || IsTrue(root, "isSidechain") || IsTrue(root, "isMeta"))
                {
                    continue;
                }

                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (type == "user" && IsHumanPrompt(message))
                {
                    break;
                }

                if (type == "assistant")
                {
                    var assistantText = AssistantText(message);
                    if (assistantText.Length > 0)
                    {
                        texts.Add(assistantText);
                    }
                }
            }
        }

        if (texts.Count == 0)
        {
            return null;
        }

        texts.Reverse();
        return TakeTail(string.Join("\n\n", texts).Trim(), maxChars);
    }

    /// <summary>屏幕摘要：去掉底部输入框和空行后的最后若干行，用来展示确认框。</summary>
    public static string ScreenExcerpt(string screen, int maxLines = 25)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var lines = SplitLines(screen);
        var content = lines.Take(FindInputBoxStart(lines)).Where(l => l.Trim().Length > 0 && !IsSeparator(l));
        return TextChunker.TrimLines(content.TakeLast(maxLines));
    }

    /// <summary>
    /// 最近的终端内容：去掉底部输入框，连续空行合并成一行，取最后 <paramref name="maxLines"/> 行。
    /// Claude 的界面底部常有大片空白，直接取最后几行往往什么都看不到。
    /// </summary>
    public static string RecentContent(string screen, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var lines = SplitLines(screen);
        var compact = new List<string>();
        foreach (var line in lines.Take(FindInputBoxStart(lines)))
        {
            if (line.Trim().Length == 0 && (compact.Count == 0 || compact[^1].Length == 0))
            {
                continue;
            }

            compact.Add(line.Trim().Length == 0 ? "" : line);
        }

        while (compact.Count > 0 && compact[^1].Length == 0)
        {
            compact.RemoveAt(compact.Count - 1);
        }

        return TextChunker.TrimLines(compact.TakeLast(maxLines));
    }

    internal static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();

    /// <summary>横线（至少 10 个 ─）。</summary>
    internal static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 10 && trimmed.All(c => c == '─' || c == '━');
    }

    /// <summary>底部输入框：横线下一行是 <c>❯</c>。返回输入框上方横线的位置；没有输入框时返回行数。</summary>
    internal static int FindInputBoxStart(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 2; i >= 0; i--)
        {
            if (IsSeparator(lines[i]) && lines[i + 1].TrimStart().StartsWith('❯'))
            {
                return i;
            }
        }

        return lines.Count;
    }

    private static bool IsUserPromptLine(string line) => line == "❯" || line.StartsWith("❯ ", StringComparison.Ordinal);

    private static bool IsToolCall(string line) =>
        ToolCallRegex().IsMatch(line) || line.Contains("(ctrl+o to expand)", StringComparison.Ordinal);

    /// <summary>
    /// 状态行、提示行等不属于回复正文的界面元素。
    /// 缩进很深的行是靠右显示的状态提示（例如"✔ Update installed"），回复正文只缩进 2 格。
    /// </summary>
    private static bool IsChrome(string line) =>
        (line.Length > 0 && SpinnerGlyphs.Contains(line[0]) && line.Length > 1 && line[1] == ' ')
        || line.TrimStart().StartsWith("⎿  Tip:", StringComparison.Ordinal)
        || (line.Length - line.TrimStart().Length >= RightAlignedIndent)
        || IsSeparator(line);

    private static string TakeTail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "…" + text[^(maxChars - 1)..];

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsHumanPrompt(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return false;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return true;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var hasText = false;
        foreach (var block in content.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            if (blockType == "tool_result")
            {
                return false;
            }

            hasText |= blockType == "text";
        }

        return hasText;
    }

    private static string AssistantText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return "";
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Trim() ?? "";
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = content.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "text")
            .Select(b => b.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join("\n\n", parts).Trim();
    }

    [GeneratedRegex(@"^⏺ [A-Z][A-Za-z0-9_]*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ToolCallRegex();
}
