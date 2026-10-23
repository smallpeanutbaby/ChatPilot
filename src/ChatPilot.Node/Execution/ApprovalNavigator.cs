using System.Text.RegularExpressions;

namespace ChatPilot.Node.Execution;

/// <summary>
/// 读懂终端里的确认框（选项列表 + <c>❯</c> 光标），算出选中"同意"类选项要按的键。
/// 不能简单地按回车：比如 Claude 的"信任此目录"确认框，默认选中的是"No, exit"。
/// </summary>
public static partial class ApprovalNavigator
{
    public sealed record Plan(IReadOnlyList<string> Keys, string OptionText);

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
        var target = options.FindIndex(o => ApproveRegex().IsMatch(o.Text));
        if (target < 0)
        {
            error = "确认框里没有\"同意\"类的选项，请用 /keys 手动操作";
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
}
