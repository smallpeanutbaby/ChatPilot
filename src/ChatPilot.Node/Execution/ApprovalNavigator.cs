using System.Text.RegularExpressions;

namespace ChatPilot.Node.Execution;

/// <summary>
/// 读懂终端里的确认框（选项列表 + <c>❯</c> 光标），算出选中"同意"类选项要按的键。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>不能简单地按回车：Claude 的"信任此目录"确认框默认选中的是"No, exit"。</item>
/// <item>只做"这一次同意"：优先选纯粹的 Yes，其次选其他同意类选项；带"不再询问 / 全部允许 / 自动接受"
/// 等会扩大权限的选项一律不自动选。只剩这类选项时拒绝，让用户用 /keys 自己决定。</item>
/// </list>
/// </remarks>
public static partial class ApprovalNavigator
{
    /// <summary>选中某个选项的操作计划。</summary>
    /// <param name="Keys">要依次按的键，例如 <c>down</c>、<c>enter</c>。</param>
    /// <param name="OptionText">会被选中的选项文字，回复给用户确认。</param>
    public sealed record Plan(IReadOnlyList<string> Keys, string OptionText);

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

        var cursor = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            var isInputBox = i > 0 && ReplyExtractor.IsSeparator(lines[i - 1]);
            if (trimmed.StartsWith('❯') && trimmed.Length > 1 && trimmed[1..].Trim().Length > 0 && !isInputBox)
            {
                cursor = i;
                break;
            }
        }

        if (cursor < 0)
        {
            error = "屏幕上没有找到确认框";
            return false;
        }

        var marker = lines[cursor].IndexOf('❯', StringComparison.Ordinal);
        var column = marker + 1;
        while (column < lines[cursor].Length && lines[cursor][column] == ' ')
        {
            column++;
        }

        // 光标行和其他选项行的文字起始列相同；更深缩进的是上一个选项折行的部分。
        var options = new List<(int Line, string Text)> { (cursor, lines[cursor][column..].Trim()) };
        for (var i = cursor - 1; i >= 0; i--)
        {
            var kind = Classify(lines[i], column);
            if (kind == LineKind.Other)
            {
                break;
            }

            if (kind == LineKind.Option)
            {
                options.Insert(0, (i, lines[i][column..].Trim()));
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
                options.Add((i, lines[i][column..].Trim()));
            }
        }

        if (options.Count < 2)
        {
            error = "屏幕上没有找到确认框的选项";
            return false;
        }

        var current = options.FindIndex(o => o.Line == cursor);
        var target = PickApproveOption(options.Select(o => o.Text).ToList());
        if (target < 0)
        {
            var listing = string.Join("\n", options.Select((o, i) => NumberPrefixRegex().IsMatch(o.Text) ? o.Text : $"{i + 1}. {o.Text}"));
            error = options.Any(o => ApproveRegex().IsMatch(o.Text))
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
    /// 选出要选中的同意类选项：纯粹的 "Yes" 优先，其次是第一个不扩大权限的同意类选项；都没有返回 -1。
    /// </summary>
    internal static int PickApproveOption(IReadOnlyList<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var candidates = options
            .Select((text, index) => (Text: StripNumber(text), Index: index))
            .Where(o => ApproveRegex().IsMatch(o.Text) && !EscalatingRegex().IsMatch(o.Text))
            .ToList();
        var plain = candidates.FindIndex(o => PlainYesRegex().IsMatch(o.Text));
        return plain >= 0 ? candidates[plain].Index : candidates.Count > 0 ? candidates[0].Index : -1;
    }

    private static string StripNumber(string text) => NumberPrefixRegex().Replace(text, "").Trim();

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

    [GeneratedRegex(@"^(\d+[.)]\s*)?(yes|allow|approve|trust|continue|proceed|是|允许|同意|信任)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApproveRegex();

    /// <summary>会扩大权限的选项：以后不再询问、全部允许、自动接受、跳过权限检查等。</summary>
    [GeneratedRegex(@"don'?t ask|do not ask|always|\ball\b|auto|bypass|skip|every|remember|不再|始终|总是|全部|所有|自动|跳过", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EscalatingRegex();

    [GeneratedRegex(@"^(yes|是|允许|同意)[.。!！]?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainYesRegex();

    [GeneratedRegex(@"^\d+[.)]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPrefixRegex();
}
