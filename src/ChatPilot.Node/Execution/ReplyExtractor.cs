using System.Globalization;
using System.Text.RegularExpressions;
using ChatPilot.Shared.Text;

namespace ChatPilot.Node.Execution;

/// <summary>
/// 从终端输出里取出"最后一条回复"、屏幕摘要和最近内容（会话记录读不到时的退路，见 <see cref="TranscriptReader"/>）。
/// </summary>
/// <remarks>
/// 认识两种回复标记：Claude Code 的 <c>⏺</c> 和 Codex 的 <c>•</c>（都在第 0 列，工具调用除外）。
/// 其他 agent 没有统一的标记，就取这一轮用户输入之后的全部输出。
/// </remarks>
public static partial class ReplyExtractor
{
    /// <summary>连这一轮的起点都找不到时，取最后这么多个非空行。</summary>
    private const int FallbackLines = 30;

    /// <summary>缩进达到这么多格的行当作靠右显示的状态提示，不算回复正文。</summary>
    private const int RightAlignedIndent = 30;

    /// <summary>在底部多少行里找输入框。</summary>
    private const int InputBoxSearchLines = 12;

    /// <summary>用提交的文字定位这一轮的起点时，最多比对它最后一行的多少个字符。</summary>
    private const int PromptAnchorChars = 40;

    /// <summary>在屏幕上找提交的文字时，比对它第一行开头的多少个字符（不含空白）。</summary>
    private const int PromptSnippetChars = 12;

    /// <summary>提交的文字第一行少于这么多字符（不含空白）时，判断不了它在不在屏幕上。</summary>
    private const int MinPromptSnippetChars = 4;

    /// <summary>Claude Code 的状态行、加载动画都从第 0 列的这些符号开始；回复正文不会从第 0 列开始写这些符号。</summary>
    private static readonly char[] SpinnerGlyphs = ['✻', '✳', '✶', '✽', '✢', '✺', '·', '*', '∗'];

    /// <summary>用户输入行在第 0 列以这些符号开头：Claude Code 的 <c>❯</c>、Codex 的 <c>›</c>。</summary>
    private static readonly char[] PromptGlyphs = ['❯', '›'];

    /// <summary>
    /// 从终端输出里提取最后一条回复：
    /// 去掉底部输入框 → 找到这一轮的起点（第 0 列的 <c>❯</c> / <c>›</c>，或者回显的 <paramref name="prompt"/>）
    /// → 取其后最后一个文字回复块（<c>⏺</c> / <c>•</c> 开头且不是工具调用）。
    /// 没有回复标记时取起点之后的全部内容；连起点也找不到时退回到最后 30 行。
    /// </summary>
    /// <param name="screen">终端输出（<c>recent-unwrapped</c>）。</param>
    /// <param name="maxChars">最多保留多少字符，超出时保留结尾。</param>
    /// <param name="prompt">这一轮交给 agent 的文字；不知道时为 null。</param>
    /// <returns>提取到的回复，超长时保留结尾、开头加 <c>…</c>；什么也没提取到时为空字符串。</returns>
    public static string FromTerminal(string screen, int maxChars, string? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var lines = SplitLines(screen);
        var content = lines.Take(FindInputBoxStart(lines)).ToList();
        var turnStart = FindTurnStart(content, prompt);
        var body = content.Skip(turnStart + 1).ToList();

        var replyStart = FindLastReplyBlock(body);
        string reply;
        if (replyStart >= 0)
        {
            reply = TextChunker.TrimLines(CollapseBlankLines(ReplyBlockLines(body, replyStart)));
        }
        else if (turnStart >= 0)
        {
            reply = TextChunker.TrimLines(Dedent(CollapseBlankLines(body.Where(l => !IsChrome(l))).ToList()));
        }
        else
        {
            reply = TextChunker.TrimLines(Dedent(body.Where(l => !IsChrome(l)).Where(l => l.Length > 0).TakeLast(FallbackLines).ToList()));
        }

        return TakeTail(reply, maxChars);
    }

    /// <summary>屏幕摘要：去掉底部输入框和空行后的最后若干行，用来展示确认框。</summary>
    /// <param name="screen">终端屏幕内容。</param>
    /// <param name="maxLines">最多保留多少行。</param>
    /// <returns>摘要文字（横线也去掉了）；屏幕上没有内容时为空字符串。</returns>
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
    /// <param name="screen">终端输出。</param>
    /// <param name="maxLines">最多保留多少行。</param>
    /// <returns>首尾空行去掉后的最近内容；没有内容时为空字符串。</returns>
    public static string RecentContent(string screen, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var lines = SplitLines(screen);
        var compact = CollapseBlankLines(lines.Take(FindInputBoxStart(lines))).SkipWhile(l => l.Length == 0).ToList();
        while (compact.Count > 0 && compact[^1].Length == 0)
        {
            compact.RemoveAt(compact.Count - 1);
        }

        return TextChunker.TrimLines(compact.TakeLast(maxLines));
    }

    /// <summary>
    /// 屏幕上可能有提交的这段文字吗（回显在对话里，或者还停在输入框里）。
    /// 用来判断一条没引起任何反应的指令是不是被吞了：只有确定找不到才返回 false，拿不准一律返回 true。
    /// 比较时去掉所有空白和边框竖线，这样折行、缩进和边框都不影响。
    /// </summary>
    /// <param name="screen">当前屏幕。</param>
    /// <param name="prompt">提交的文字。</param>
    /// <returns>确定屏幕上找不到这段文字时为 false；找到了或拿不准为 true。</returns>
    internal static bool MayShowPrompt(string screen, string prompt)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(prompt);
        var firstLine = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(Compact)
            .FirstOrDefault(l => l.Length > 0);

        // 太短的文字（比如"继续"）在屏幕上别处也可能出现，判断不了；长文字可能被折叠成 [Pasted text …] 显示。
        if (firstLine is null || firstLine.Length < MinPromptSnippetChars || PastedPlaceholderRegex().IsMatch(screen))
        {
            return true;
        }

        var snippet = firstLine.Length <= PromptSnippetChars ? firstLine : firstLine[..PromptSnippetChars];
        return Compact(screen).Contains(snippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// 去掉空白、边框竖线，以及终端回读时常丢掉的格式字符和组合符号（emoji 变体选择符 U+FE0F、连接符 U+200D 等），
    /// 免得同一段文字因为这些看不见的字符被判成"不在屏幕上"。
    /// </summary>
    /// <param name="text">要比较的文字。</param>
    /// <returns>只剩可见字符的文字。</returns>
    private static string Compact(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ('│' or '┃')
            && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)));

    /// <summary>按行拆分（兼容 CRLF），并去掉每行末尾的空白。</summary>
    /// <param name="text">终端输出。</param>
    /// <returns>各行（保留空行）。</returns>
    internal static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();

    /// <summary>横线（至少 10 个 ─）。</summary>
    /// <param name="line">一行终端输出。</param>
    /// <returns>去掉首尾空白后全是 <c>─</c> 或 <c>━</c> 且至少 10 个时为 true。</returns>
    internal static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 10 && trimmed.All(c => c == '─' || c == '━');
    }

    /// <summary>
    /// 底部输入框的起始行；没有输入框时返回行数。认识几种样子：
    /// <list type="number">
    /// <item>Claude Code：横线下一行是 <c>❯</c>。</item>
    /// <item>Codex：底部第 0 列 <c>›</c> 开头的输入行，下面只有状态栏（选项列表里的 <c>› 1. Yes</c> 不算）。</item>
    /// <item>Gemini / Qwen 等：底部圆角边框里是 <c>&gt;</c> 开头的输入行。</item>
    /// <item>pi 等：底部两条横线夹着输入区。</item>
    /// </list>
    /// </summary>
    /// <param name="lines">按 <see cref="SplitLines"/> 拆好的各行。</param>
    /// <returns>输入框起始行的下标（之前的行才是内容）；认不出输入框时为 <c>lines.Count</c>。</returns>
    internal static int FindInputBoxStart(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 2; i >= 0; i--)
        {
            if (IsSeparator(lines[i]) && lines[i + 1].TrimStart() is { Length: > 0 } next && PromptGlyphs.Contains(next[0]))
            {
                return i;
            }
        }

        var last = lines.Count - 1;
        while (last >= 0 && lines[last].Length == 0)
        {
            last--;
        }

        var windowStart = Math.Max(0, last - InputBoxSearchLines + 1);
        for (var i = last; i >= windowStart; i--)
        {
            // 选项列表（› 1. Yes / 下面跟着 2. …）是确认框，不能当成输入框截掉。
            if (lines[i].StartsWith('›') && !LooksLikeOption(lines[i][1..])
                && !lines.Skip(i + 1).Take(last - i).Where(l => l.Trim().Length > 0).Take(3).Any(LooksLikeOption))
            {
                return i;
            }

            // 只有屏幕上最后一个圆角框才可能是输入框：确认框挡住输入框时，上方回显指令的框也长这样，不能把下面的确认框截掉。
            if (lines[i].TrimStart().StartsWith('╭') && i + 1 <= last && BoxedPromptRegex().IsMatch(lines[i + 1])
                && !lines.Skip(i + 2).Take(last - i - 1).Any(l => l.TrimStart().StartsWith('╭') || LooksLikeOption(WithoutBoxEdge(l))))
            {
                return i;
            }
        }

        // 两条横线之间最多三行、而且不是选项，才是输入区；否则可能是夹在横线之间的确认框。
        var separators = Enumerable.Range(windowStart, last - windowStart + 1).Where(i => IsSeparator(lines[i])).ToList();
        if (separators.Count >= 2 && separators[^1] - separators[^2] <= 4
            && !lines.Skip(separators[^2] + 1).Take(separators[^1] - separators[^2] - 1).Any(LooksLikeOption))
        {
            return separators[^2];
        }

        return lines.Count;
    }

    /// <summary>圆角框的上下边：<c>╭────╮</c>、<c>╰────╯</c>。</summary>
    /// <param name="line">一行终端输出。</param>
    /// <returns>是圆角框的上边或下边为 true。</returns>
    internal static bool IsBoxBorder(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed[0] is '╭' or '╰' && trimmed[^1] is '╮' or '╯' && trimmed[1..^1].All(c => c == '─');
    }

    /// <summary>去掉行首的边框竖线（确认框常画在框里），再判断是不是选项。</summary>
    /// <param name="line">一行终端输出。</param>
    /// <returns>去掉行首空白和竖线后的内容。</returns>
    private static string WithoutBoxEdge(string line) => line.TrimStart().TrimStart('│', '┃');

    /// <summary>像选项列表里的一行：编号开头，或者以选中标记开头。</summary>
    /// <param name="line">一行终端输出。</param>
    /// <returns>像选项为 true。</returns>
    private static bool LooksLikeOption(string line)
    {
        var trimmed = line.TrimStart();
        return NumberedOptionRegex().IsMatch(trimmed) || (trimmed.Length > 0 && ApprovalNavigator.IsCursorGlyph(trimmed[0]));
    }

    /// <summary>
    /// 这一轮的起点（最后一次用户输入所在行）：先找第 0 列的 <c>❯</c> / <c>›</c>；
    /// 找不到时找以提交文字最后一行结尾的行（agent 会回显用户输入）。都找不到返回 -1。
    /// </summary>
    /// <param name="content">去掉输入框后的各行。</param>
    /// <param name="prompt">这一轮交给 agent 的文字；不知道时为 null。</param>
    /// <returns>起点所在行的下标；找不到时为 -1。</returns>
    private static int FindTurnStart(IReadOnlyList<string> content, string? prompt)
    {
        for (var i = content.Count - 1; i >= 0; i--)
        {
            if (IsUserPromptLine(content[i]))
            {
                return i;
            }
        }

        if (PromptAnchor(prompt) is not { } anchor)
        {
            return -1;
        }

        for (var i = content.Count - 1; i >= 0; i--)
        {
            if (content[i].TrimEnd('│', '┃', ' ').EndsWith(anchor, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>提交文字最后一个非空行的结尾部分（太短的不用，容易误匹配）。</summary>
    /// <param name="prompt">提交的文字；可以为 null。</param>
    /// <returns>最后一行的最后 <see cref="PromptAnchorChars"/> 个字符；没有文字或最后一行不到 2 个字符时为 null。</returns>
    private static string? PromptAnchor(string? prompt)
    {
        var lastLine = prompt?.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        if (lastLine is null || lastLine.Length < 2)
        {
            return null;
        }

        return lastLine.Length <= PromptAnchorChars ? lastLine : lastLine[^PromptAnchorChars..];
    }

    /// <summary>最后一个文字回复块的起始行：<c>⏺</c>（Claude Code）或 <c>•</c>（Codex）开头且不是工具调用；没有返回 -1。</summary>
    /// <param name="body">这一轮起点之后的各行。</param>
    /// <returns>起始行的下标；没有时为 -1。</returns>
    private static int FindLastReplyBlock(IReadOnlyList<string> body)
    {
        for (var i = body.Count - 1; i >= 0; i--)
        {
            if (body[i].StartsWith("⏺", StringComparison.Ordinal) && !IsClaudeToolCall(body[i]))
            {
                return i;
            }

            if (body[i].StartsWith("• ", StringComparison.Ordinal) && !IsCodexToolBlock(body, i))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>回复块的正文：去掉开头的标记和两格缩进，跳过界面元素，遇到下一个块（工具调用等）或 Codex 的本轮结束信息停止。</summary>
    /// <param name="body">这一轮起点之后的各行。</param>
    /// <param name="start">回复块起始行的下标。</param>
    /// <returns>回复块的各行正文。</returns>
    private static IEnumerable<string> ReplyBlockLines(IReadOnlyList<string> body, int start)
    {
        yield return body[start].TrimStart('⏺', '•').TrimStart();
        foreach (var line in body.Skip(start + 1))
        {
            if (IsCodexTurnFooter(line) || (line.Length > 0 && line[0] is '⏺' or '•' or '■'))
            {
                yield break;
            }

            if (!IsChrome(line))
            {
                yield return line.StartsWith("  ", StringComparison.Ordinal) ? line[2..] : line;
            }
        }
    }

    /// <summary>连续空行只保留一行（Claude 界面里回复和输入框之间常有大片空白）。</summary>
    /// <param name="lines">各行。</param>
    /// <returns>合并空行后的各行；只有空白的行变成空字符串。</returns>
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

    /// <summary>去掉所有非空行共同的缩进（pi 等把正文整体缩进一格）。</summary>
    /// <param name="lines">各行。</param>
    /// <returns>去掉共同缩进后的各行。</returns>
    private static IEnumerable<string> Dedent(IReadOnlyList<string> lines)
    {
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart());
    }

    /// <summary>用户输入行：第 0 列是 <see cref="PromptGlyphs"/> 里的符号，后面是空格或什么都没有。</summary>
    /// <param name="line">一行终端输出。</param>
    /// <returns>是用户输入行为 true。</returns>
    private static bool IsUserPromptLine(string line) =>
        line.Length > 0 && PromptGlyphs.Contains(line[0]) && (line.Length == 1 || line[1] == ' ');

    /// <summary>Claude Code 的工具调用行：<c>⏺ Bash(…)</c> 这种工具名加括号，或者带折叠提示 <c>(ctrl+o to expand)</c>。</summary>
    /// <param name="line">以 <c>⏺</c> 开头的一行。</param>
    /// <returns>是工具调用为 true。</returns>
    private static bool IsClaudeToolCall(string line) =>
        ClaudeToolCallRegex().IsMatch(line) || line.Contains("(ctrl+o to expand)", StringComparison.Ordinal);

    /// <summary>
    /// Codex 的工具调用块（<c>• Ran …</c>、<c>• Edited …</c>、<c>• Explored</c> 等）：
    /// 首行是这类动作，或者块里有 <c>└</c> 输出、折叠提示、diff 行。普通回复的后续行只缩进两格。
    /// </summary>
    /// <param name="body">这一轮起点之后的各行。</param>
    /// <param name="start">以 <c>• </c> 开头的块首行的下标。</param>
    /// <returns>是工具调用块为 true。</returns>
    private static bool IsCodexToolBlock(IReadOnlyList<string> body, int start)
    {
        if (CodexToolHeaderRegex().IsMatch(body[start]))
        {
            return true;
        }

        foreach (var line in body.Skip(start + 1))
        {
            if (line.Length > 0 && line[0] != ' ')
            {
                break;
            }

            if (line.StartsWith("  └", StringComparison.Ordinal)
                || line.Contains("(ctrl+t to expand)", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("+ Show details", StringComparison.Ordinal)
                || CodexDiffLineRegex().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Codex 一轮结束后的信息：<c>Worked for 3m 2s</c>、<c>─ Worked for …</c>、<c>↳ Recap: …</c>。</summary>
    /// <param name="line">一行终端输出。</param>
    /// <returns>是本轮结束信息为 true。</returns>
    private static bool IsCodexTurnFooter(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("Worked for ", StringComparison.Ordinal)
            || trimmed.StartsWith("─ Worked for ", StringComparison.Ordinal)
            || trimmed.StartsWith("↳ ", StringComparison.Ordinal);
    }

    /// <summary>
    /// 状态行、提示行等不属于回复正文的界面元素。
    /// 缩进很深的行是靠右显示的状态提示（例如"✔ Update installed"），回复正文只缩进 2 格。
    /// </summary>
    /// <param name="line">一行终端输出。</param>
    /// <returns>是界面元素为 true。</returns>
    private static bool IsChrome(string line) =>
        IsBoxBorder(line)
        || (line.Length > 1 && SpinnerGlyphs.Contains(line[0]) && line[1] == ' ')
        || (line.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is >= '⠀' and <= '⣿')
        || line.TrimStart().StartsWith("⎿  Tip:", StringComparison.Ordinal)
        || (line.Length - line.TrimStart().Length >= RightAlignedIndent)
        || IsSeparator(line);

    /// <summary>超长时保留结尾部分，开头加 <c>…</c>。</summary>
    /// <param name="text">完整的回复。</param>
    /// <param name="maxChars">最多多少字（含 <c>…</c>）。</param>
    /// <returns>不超过 <paramref name="maxChars"/> 字的文字。</returns>
    private static string TakeTail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "…" + text[^(maxChars - 1)..];

    /// <summary>Claude Code 的工具调用行：<c>⏺</c> 后面是大写字母开头的工具名和左括号，例如 <c>⏺ Bash(</c>。</summary>
    /// <returns>匹配工具调用行开头的正则。</returns>
    [GeneratedRegex(@"^⏺ [A-Z][A-Za-z0-9_]*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeToolCallRegex();

    /// <summary>Codex 工具调用块的首行动作；也包括进行中的 <c>• Working (12s • esc to interrupt)</c>。</summary>
    /// <returns>匹配工具调用块首行的正则。</returns>
    [GeneratedRegex(@"^• (?:(?:Ran|Running|Explored|Exploring|Called|Calling|Updated Plan)\b|.*\besc to interrupt\))", RegexOptions.CultureInvariant)]
    private static partial Regex CodexToolHeaderRegex();

    /// <summary>Codex 显示的 diff 行，例如 <c>     46 +    code</c>。</summary>
    /// <returns>匹配 diff 行的正则。</returns>
    [GeneratedRegex(@"^\s{3,}\d+ [+-]", RegexOptions.CultureInvariant)]
    private static partial Regex CodexDiffLineRegex();

    /// <summary>选项列表里的一行（<c>1. Yes</c>）。</summary>
    /// <returns>匹配编号选项开头的正则。</returns>
    [GeneratedRegex(@"^\d+[.)]\s", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedOptionRegex();

    /// <summary>长文字粘贴后的折叠显示：Claude Code 的 <c>[Pasted text #1 +20 lines]</c>、Codex 的 <c>[Pasted Content 1234 chars]</c>。</summary>
    /// <returns>匹配折叠显示的正则（不分大小写）。</returns>
    [GeneratedRegex(@"\[Pasted", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PastedPlaceholderRegex();

    /// <summary>圆角边框里的输入行：<c>│ &gt; …</c>（Gemini / Qwen 等）。</summary>
    /// <returns>匹配框内输入行开头的正则。</returns>
    [GeneratedRegex(@"^\s*│\s*[>›❯]\s?", RegexOptions.CultureInvariant)]
    private static partial Regex BoxedPromptRegex();
}
