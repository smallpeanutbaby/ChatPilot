using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace ChatPilot.Shared;

/// <summary>
/// 两边共用的输入校验规则。
/// 正则一律用 <c>\z</c> 结尾：.NET 里 <c>$</c> 还会匹配末尾换行之前的位置，"name\n" 会被放过。
/// </summary>
public static partial class Validation
{
    /// <summary>herdr 内置支持的 agent 类型（<c>herdr agent start --kind</c> 的取值，对照 herdr 0.9.4）。</summary>
    public static FrozenSet<string> BuiltInAgentKinds { get; } =
        new[]
        {
            "agy", "amp", "claude", "cline", "codex", "copilot", "cursor", "devin", "droid", "gemini", "grok", "hermes",
            "kilo", "kimi", "kiro", "letta", "maki", "mastracode", "muse", "omp", "opencode", "pi", "qodercli", "qwen",
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// 允许远程发送的按键：只够操作确认框、菜单和中断，不包含能输入任意文字的按键组合。
    /// </summary>
    public static FrozenSet<string> AllowedKeys { get; } =
        new[]
        {
            "enter", "esc", "tab", "shift+tab", "up", "down", "left", "right", "backspace", "ctrl+c",
            "y", "n", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>herdr 对 agent 名字的要求。</summary>
    /// <param name="value">要检查的名字。</param>
    /// <returns>是小写字母开头、只含小写字母数字下划线短横线、最长 32 个字符时为 true；null 为 false。</returns>
    public static bool IsAgentName(string? value) => value is not null && AgentNameRegex().IsMatch(value);

    /// <summary>herdr 的 pane ID，例如 <c>w1:p2</c>。</summary>
    /// <param name="value">要检查的字符串。</param>
    /// <returns>符合 pane ID 格式时为 true；null 为 false。</returns>
    public static bool IsPaneId(string? value) => value is not null && PaneIdRegex().IsMatch(value);

    /// <summary>节点只接受 pane ID 或 agent 名字作为目标。</summary>
    /// <param name="value">要检查的目标。</param>
    /// <returns>是 agent 名字或 pane ID 时为 true。</returns>
    public static bool IsAgentTarget(string? value) => IsAgentName(value) || IsPaneId(value);

    /// <summary>agent 类型的格式（是否真的可用由节点决定）。</summary>
    /// <param name="value">要检查的类型名。</param>
    /// <returns>格式和 agent 名字相同时为 true；null 为 false。</returns>
    public static bool IsAgentKindSyntax(string? value) => value is not null && AgentNameRegex().IsMatch(value);

    /// <summary>设备名：字母（含中文）、数字、下划线、短横线，最长 32 个字符，不能有空格和斜杠。</summary>
    /// <param name="value">要检查的设备名。</param>
    /// <returns>符合要求时为 true；null 为 false。</returns>
    public static bool IsDeviceName(string? value) => value is not null && DeviceNameRegex().IsMatch(value);

    /// <summary>是否在按键白名单 <see cref="AllowedKeys"/> 内。</summary>
    /// <param name="value">按键名，区分大小写。</param>
    /// <returns>在白名单内时为 true；null 为 false。</returns>
    public static bool IsAllowedKey(string? value) => value is not null && AllowedKeys.Contains(value);

    /// <summary>MAC 地址，格式固定为 <c>AA:BB:CC:DD:EE:FF</c>（大写十六进制、冒号分隔）。</summary>
    /// <param name="value">要检查的字符串。</param>
    /// <returns>格式完全符合时为 true；小写、短横线分隔或 null 都为 false。</returns>
    public static bool IsMacAddress(string? value) => value is not null && MacAddressRegex().IsMatch(value);

    /// <summary>
    /// <c>/diff</c> 的文件路径：相对路径，不能以 <c>-</c> 或 <c>/</c> 开头，不能有 <c>..</c>、盘符、控制字符。
    /// 只是把明显不对的挡在节点之外；git 用 <c>--literal-pathspecs</c> 按字面解释，越不出仓库。
    /// </summary>
    /// <param name="value">主人给的文件路径。</param>
    /// <returns>可以交给节点时为 true；为空、超过 300 个字符或违反上面任一条时为 false。</returns>
    public static bool IsRepoRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value[0] is '-' or '/' or '\\' || value.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return !value.Any(char.IsControl)
            && value.Split('/', '\\').All(segment => segment != "..");
    }

    /// <summary>MAC 地址：六组大写十六进制，冒号分隔。</summary>
    /// <returns>编译期生成的正则。</returns>
    [GeneratedRegex(@"^[0-9A-F]{2}(:[0-9A-F]{2}){5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressRegex();

    /// <summary>agent 名字（也用于 agent 类型）：小写字母开头，后面是小写字母、数字、下划线或短横线，共 1–32 个字符。</summary>
    /// <returns>编译期生成的正则。</returns>
    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AgentNameRegex();

    /// <summary>herdr 的工作区和 pane 编号不只是数字：超过 9 个之后会用字母，例如 <c>wA:pF</c>。</summary>
    /// <returns>编译期生成的正则。</returns>
    [GeneratedRegex(@"^w[0-9A-Za-z]{1,9}:p[0-9A-Za-z]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PaneIdRegex();

    /// <summary>设备名：字母或数字开头（含中文），后面可以有下划线、短横线，共 1–32 个字符。</summary>
    /// <returns>编译期生成的正则。</returns>
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNameRegex();
}
