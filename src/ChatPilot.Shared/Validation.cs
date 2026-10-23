using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace ChatPilot.Shared;

/// <summary>
/// 两边共用的输入校验规则。
/// 正则一律用 <c>\z</c> 结尾：.NET 里 <c>$</c> 还会匹配末尾换行之前的位置，"name\n" 会被放过。
/// </summary>
public static partial class Validation
{
    /// <summary>herdr 内置支持的 agent 类型。</summary>
    public static FrozenSet<string> BuiltInAgentKinds { get; } =
        new[] { "claude", "codex", "cursor", "droid", "grok", "kimi", "opencode", "pi", "qwen" }
            .ToFrozenSet(StringComparer.Ordinal);

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

    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AgentNameRegex();

    [GeneratedRegex(@"^w[0-9]{1,9}:p[0-9]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PaneIdRegex();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNameRegex();
}
