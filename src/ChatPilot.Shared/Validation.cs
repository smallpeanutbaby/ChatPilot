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
    public static bool IsAgentName(string? value) => value is not null && AgentNameRegex().IsMatch(value);

    /// <summary>herdr 的 pane ID，例如 <c>w1:p2</c>。</summary>
    public static bool IsPaneId(string? value) => value is not null && PaneIdRegex().IsMatch(value);

    /// <summary>节点只接受 pane ID 或 agent 名字作为目标。</summary>
    public static bool IsAgentTarget(string? value) => IsAgentName(value) || IsPaneId(value);

    /// <summary>agent 类型的格式（是否真的可用由节点决定）。</summary>
    public static bool IsAgentKindSyntax(string? value) => value is not null && AgentNameRegex().IsMatch(value);

    /// <summary>设备名：字母（含中文）、数字、下划线、短横线，最长 32 个字符，不能有空格和斜杠。</summary>
    public static bool IsDeviceName(string? value) => value is not null && DeviceNameRegex().IsMatch(value);

    /// <summary>是否在按键白名单 <see cref="AllowedKeys"/> 内。</summary>
    public static bool IsAllowedKey(string? value) => value is not null && AllowedKeys.Contains(value);

    /// <summary>MAC 地址，格式固定为 <c>AA:BB:CC:DD:EE:FF</c>（大写十六进制、冒号分隔）。</summary>
    public static bool IsMacAddress(string? value) => value is not null && MacAddressRegex().IsMatch(value);

    /// <summary>
    /// <c>/diff</c> 的文件路径：相对路径，不能以 <c>-</c> 或 <c>/</c> 开头，不能有 <c>..</c>、盘符、控制字符。
    /// 只是把明显不对的挡在节点之外；git 用 <c>--literal-pathspecs</c> 按字面解释，越不出仓库。
    /// </summary>
    public static bool IsRepoRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value[0] is '-' or '/' or '\\' || value.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return !value.Any(char.IsControl)
            && value.Split('/', '\\').All(segment => segment != "..");
    }

    [GeneratedRegex(@"^[0-9A-F]{2}(:[0-9A-F]{2}){5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AgentNameRegex();

    /// <summary>herdr 的工作区和 pane 编号不只是数字：超过 9 个之后会用字母，例如 <c>wA:pF</c>。</summary>
    [GeneratedRegex(@"^w[0-9A-Za-z]{1,9}:p[0-9A-Za-z]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PaneIdRegex();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNameRegex();
}
