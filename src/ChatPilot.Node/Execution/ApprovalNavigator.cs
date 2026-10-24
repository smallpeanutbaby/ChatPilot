using System.Text.RegularExpressions;

namespace ChatPilot.Node.Execution;

/// <summary>
/// 读懂终端里的确认框（竖排的选项列表 + 选中标记），算出选中"同意"类选项要按的键。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>选中标记因 agent 而异：Claude Code / Gemini / Qwen 的 <c>❯</c>，Codex 的 <c>›</c>，Cursor 的 <c>→</c>，
/// Kimi 的 <c>▶</c>，Hermes 的 <c>▸</c>，Droid 的 <c>&gt;</c>，Gemini 旧版的 <c>●</c>，以及 Grok 的单选框 <c>(●)</c>。</item>
/// <item>不能简单地按回车：Claude 的"信任此目录"确认框默认选中的是"No, exit"。</item>
/// <item>只有选项里同时有"同意"类和"拒绝 / 取消"类，才当作确认框：agent 回复里的编号列表、回显的多行指令不会被误认。</item>
/// <item>只做"这一次同意"：优先选纯粹的 Yes / Allow，其次是"仅这一次"，再其次是其他同意类选项；
/// 带"不再询问 / 全部允许 / 本会话都允许 / 自动执行"等会扩大权限的选项一律不自动选。只剩这类选项时拒绝，让用户用 /keys 自己决定。</item>
/// <item>横排按钮（opencode 的 Allow once / Allow always / Reject）在纯文本里看不出哪个被选中，不自动处理。</item>
/// </list>
/// </remarks>
public static partial class ApprovalNavigator
{
    /// <summary>选中标记只在屏幕底部这么多个非空行里找（确认框总在底部，避免把历史里的用户输入当成选项）。</summary>
    private const int SearchLines = 30;

    /// <summary>选中某个选项的操作计划。</summary>
    /// <param name="Keys">要依次按的键，例如 <c>down</c>、<c>enter</c>。</param>
    /// <param name="OptionText">会被选中的选项文字，回复给用户确认。</param>
    public sealed record Plan(IReadOnlyList<string> Keys, string OptionText);

    /// <summary>行首的这个字符是不是选中标记（后面要跟空格和选项文字）。</summary>
    internal static bool IsCursorGlyph(char c) => c is '❯' or '›' or '→' or '▶' or '▸' or '➤' or '➜' or '●' or '◉' or '>';

    /// <summary>根据屏幕内容算出"同意"要按的键。</summary>
    /// <param name="screen">当前屏幕（<c>herdr agent read --source visible</c>）。</param>
    /// <param name="plan">成功时的操作计划。</param>
    /// <param name="error">失败时给用户看的原因（会列出所有选项）。</param>
    /// <returns>找到了可以安全选中的选项时返回 true。</returns>
    public static bool TryPlanApprove(string screen, out Plan? plan, out string error)
    {
        ArgumentNullException.ThrowIfNull(screen);
        plan = null;
        var lines = ReplyExtractor.SplitLines(screen).Select(StripBox).ToList();

        List<(int Line, string Text)>? options = null;
        var cursor = -1;
        var sawCursor = false;
        var nonEmpty = 0;
        for (var i = lines.Count - 1; i >= 0 && options is null && nonEmpty < SearchLines; i--)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            nonEmpty++;
            if (CursorColumn(lines, i) is not { } column)
            {
                continue;
            }

            sawCursor = true;
            var found = ReadOptions(lines, i, column);
            if (found.Count >= 2 && found.Any(o => DeclineRegex().IsMatch(StripNumber(o.Text))))
            {
                options = found;
                cursor = i;
            }
        }

        if (options is null)
        {
            error = sawCursor
                ? "屏幕上没有找到确认框（要同时有\"同意\"和\"拒绝 / 取消\"两类选项才算）。确实有确认框的话，请用 /keys 手动操作"
                : "屏幕上没有找到确认框";
            return false;
        }

        var current = options.FindIndex(o => o.Line == cursor);
        var target = PickApproveOption(options.Select(o => o.Text).ToList());
        if (target < 0)
        {
            var listing = string.Join("\n", options.Select((o, i) => NumberPrefixRegex().IsMatch(o.Text) ? o.Text : $"{i + 1}. {o.Text}"));
            error = options.Any(o => ApproveRegex().IsMatch(StripNumber(o.Text)))
                ? $"同意类的选项都会扩大权限（比如\"不再询问\"），不自动选。选项：\n{listing}\n请用 /keys 自己选（up / down 移动，enter 确认）"
                : $"确认框里没有\"同意\"类的选项，请用 /keys 手动操作。选项：\n{listing}";
            return false;
        }

        var keys = new List<string>();
        var step = target > current ? "down" : "up";
        for (var i = 0; i < Math.Abs(target - current); i++)
        {
            keys.Add(step);
        }

        keys.Add("enter");
        plan = new Plan(keys, options[target].Text);
        error = "";
        return true;
    }

    /// <summary>
    /// 选出要选中的同意类选项，都不扩大权限：纯粹的 Yes / Allow 优先，其次是"仅这一次"，再其次是第一个同意类选项；都没有返回 -1。
    /// </summary>
    internal static int PickApproveOption(IReadOnlyList<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var candidates = options
            .Select((text, index) => (Text: StripHotkeys(StripNumber(text)), Index: index))
            .Where(o => ApproveRegex().IsMatch(o.Text) && !EscalatingRegex().IsMatch(o.Text))
            .ToList();
        var plain = candidates.FindIndex(o => PlainYesRegex().IsMatch(o.Text));
        if (plain >= 0)
        {
            return candidates[plain].Index;
        }

        var once = candidates.FindIndex(o => OnceRegex().IsMatch(o.Text));
        return once >= 0 ? candidates[once].Index : candidates.Count > 0 ? candidates[0].Index : -1;
    }

    /// <summary>
    /// 这一行是选中的选项时，返回选项文字的起始列（其他选项的文字也从这一列开始）；不是返回 null。
    /// 紧跟在横线下面的 <c>❯</c> 行是 Claude 的输入框，不算。
    /// </summary>
    private static int? CursorColumn(IReadOnlyList<string> lines, int index)
    {
        var line = lines[index];
        var indent = line.Length - line.TrimStart().Length;
        var trimmed = line[indent..];

        if (RadioOptionRegex().Match(trimmed) is { Success: true } radio)
        {
            return radio.Groups["mark"].Value is "●" or "◉" ? indent : null;
        }

        var isInputBox = index > 0 && ReplyExtractor.IsSeparator(lines[index - 1]);
        if (trimmed.Length < 3 || !IsCursorGlyph(trimmed[0]) || trimmed[1] != ' ' || trimmed[2..].Trim().Length == 0 || isInputBox)
        {
            return null;
        }

        var column = indent + 1;
        while (column < line.Length && line[column] == ' ')
        {
            column++;
        }

        return column;
    }

    /// <summary>以选中行为中心，向上向下收集同一列开始的选项；更深缩进的是上一个选项折行的部分。</summary>
    private static List<(int Line, string Text)> ReadOptions(IReadOnlyList<string> lines, int cursor, int column)
    {
        var options = new List<(int Line, string Text)> { (cursor, OptionText(lines[cursor], column)) };
        for (var i = cursor - 1; i >= 0; i--)
        {
            var kind = Classify(lines[i], column);
            if (kind == LineKind.Other)
            {
                break;
            }

            if (kind == LineKind.Option)
            {
                options.Insert(0, (i, OptionText(lines[i], column)));
            }
        }

        for (var i = cursor + 1; i < lines.Count; i++)
        {
            var kind = Classify(lines[i], column);
            if (kind == LineKind.Other)
            {
                break;
            }

            if (kind == LineKind.Option)
            {
                options.Add((i, OptionText(lines[i], column)));
            }
        }

        return options;
    }

    /// <summary>选项文字；Grok 单选框的 <c>1 (●)</c> 前缀换成 <c>1.</c>，方便统一处理编号。</summary>
    private static string OptionText(string line, int column) =>
        RadioOptionRegex().Replace(line[column..].Trim(), "${key}. ");

    private static string StripNumber(string text) => NumberPrefixRegex().Replace(text, "").Trim();

    /// <summary>去掉选项末尾的快捷键提示，例如 <c>(y)</c>、<c>(esc or n)</c>、<c>(shift+tab)</c>。</summary>
    private static string StripHotkeys(string text) => HotkeySuffixRegex().Replace(text, "").Trim();

    private enum LineKind
    {
        Option,
        Continuation,
        Other,
    }

    private static LineKind Classify(string line, int column)
    {
        if (line.Length <= column || line[..column].Trim().Length > 0)
        {
            return LineKind.Other;
        }

        return line[column] == ' ' ? LineKind.Continuation : LineKind.Option;
    }

    /// <summary>去掉边框竖线，用空格占位以保持列对齐。</summary>
    private static string StripBox(string line)
    {
        var chars = line.ToCharArray();
        var first = Array.FindIndex(chars, c => c != ' ');
        if (first >= 0 && chars[first] is '│' or '┃')
        {
            chars[first] = ' ';
        }

        return new string(chars).TrimEnd().TrimEnd('│', '┃').TrimEnd();
    }

    [GeneratedRegex(@"^(\d+[.)]\s*)?(yes|allow|approve|accept|trust|continue|proceed|run|是|允许|同意|信任|批准|继续|执行|运行)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApproveRegex();

    /// <summary>
    /// 会扩大权限的选项：以后不再询问、全部允许、整个会话 / 工作区 / 项目都允许、永久或全局生效、自动执行、加白名单、跳过权限检查等。
    /// 这是黑名单，新 agent 的新措辞可能漏掉，所以宁可多拦：拦错了只是让用户用 /keys 自己选。
    /// </summary>
    [GeneratedRegex(@"don'?t ask|do not ask|always|\ball\b|auto|bypass|skip|every|remember|session|conversation|thread|chat|workspace|project|permanent|global|forever|future|persist|allowlist|whitelist|yolo|不再|始终|总是|全部|所有|自动|跳过|会话|对话|聊天|工作区|项目|永久|全局|以后|今后|永远|白名单", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EscalatingRegex();

    /// <summary>拒绝 / 取消类的选项：No、Deny、Reject、Cancel、Skip、Exit、Keep（不删除）、否、拒绝、取消、跳过、退出。</summary>
    [GeneratedRegex(@"^(?:no|deny|reject|decline|cancel|skip|exit|quit|keep)\b|^(?:否|拒绝|取消|跳过|退出|不同意|不允许)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeclineRegex();

    [GeneratedRegex(@"^(yes|allow|approve|accept|是|允许|同意|批准)[.。!！]?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainYesRegex();

    /// <summary>只同意这一次：Allow once、Run (once)、Yes, single permission、仅本次。</summary>
    [GeneratedRegex(@"\bonce\b|single|仅此一次|仅本次|这一次|本次", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnceRegex();

    [GeneratedRegex(@"^\d+[.)]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPrefixRegex();

    /// <summary>Grok 的单选框选项：<c>1 (●) Yes, proceed</c>、<c>2 (○) No, reject</c>。</summary>
    [GeneratedRegex(@"^(?<key>[0-9a-z]{1,2})\s+\((?<mark>[●○◉◯])\)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex RadioOptionRegex();

    [GeneratedRegex(@"(\s*\((?:[a-z]|esc|enter|tab|shift\+tab|esc or [a-z])\))+\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotkeySuffixRegex();
}
