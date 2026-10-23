using System.Text;
using ChatPilot.Shared;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Server.Chat;

/// <summary>
/// 聊天里的目标：<c>序号</c>、<c>名字</c>、<c>pane ID</c>、<c>工作区名</c>，前面可以加 <c>设备/</c> 指定设备。
/// </summary>
public sealed record TargetRef(string? Device, string Value)
{
    public bool IsIndex => Value.All(char.IsAsciiDigit) && Value.Length is > 0 and <= 4;

    public override string ToString() => Device is null ? Value : $"{Device}/{Value}";

    public static TargetRef? TryParse(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length is 0 or > 100 || token.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var slash = token.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return new TargetRef(null, token);
        }

        var device = token[..slash];
        var value = token[(slash + 1)..];
        return Validation.IsDeviceName(device) && value.Length > 0 && !value.Contains('/', StringComparison.Ordinal)
            ? new TargetRef(device, value)
            : null;
    }
}

public abstract record ChatCommand;

public sealed record ChatHelp : ChatCommand;

public sealed record ChatDevices : ChatCommand;

public sealed record ChatUse(string Device) : ChatCommand;

public sealed record ChatList(string? Device) : ChatCommand;

public sealed record ChatRead(TargetRef Target, int Lines) : ChatCommand;

public sealed record ChatPrompt(TargetRef Target, string Text) : ChatCommand;

/// <summary>不是指令的文字，交给当前设备的管家。</summary>
public sealed record ChatButlerPrompt(string Text) : ChatCommand;

public sealed record ChatNew(string? Device, string Directory, string? Kind, string? Name) : ChatCommand;

public sealed record ChatRespond(TargetRef Target, bool Approve) : ChatCommand;

public sealed record ChatKeys(TargetRef Target, IReadOnlyList<string> Keys) : ChatCommand;

public sealed record ChatRename(TargetRef Target, string Name) : ChatCommand;

public sealed record ChatButler(string? Device) : ChatCommand;

public sealed record ChatPair(string? DeviceName) : ChatCommand;

public sealed record ChatUnpair(string Device) : ChatCommand;

/// <summary>解析失败；<see cref="Message"/> 是给用户看的用法提示。</summary>
public sealed record ChatInvalid(string Message) : ChatCommand;

/// <summary>把聊天文字解析成指令。纯函数，不访问任何状态。</summary>
public static class ChatCommandParser
{
    public static ChatCommand Parse(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            return new ChatInvalid("消息是空的。发 /help 查看用法");
        }

        // 中文输入法常打出全角符号。
        if (text[0] == '／')
        {
            text = "/" + text[1..];
        }
        else if (text[0] == '＠')
        {
            text = "@" + text[1..];
        }

        if (text[0] == '@')
        {
            return ParsePrompt(text[1..]);
        }

        if (text[0] != '/')
        {
            return text.Length > ProtocolInfo.MaxPromptChars
                ? new ChatInvalid($"内容太长（上限 {ProtocolInfo.MaxPromptChars} 字）")
                : new ChatButlerPrompt(text);
        }

        if (!TryTokenize(text[1..], out var tokens))
        {
            return new ChatInvalid("引号没有配对");
        }

        if (tokens.Count == 0)
        {
            return new ChatHelp();
        }

        var name = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToList();
        return name switch
        {
            "help" or "h" or "?" or "帮助" => new ChatHelp(),
            "devices" or "d" or "设备" => args.Count == 0 ? new ChatDevices() : Usage("/devices"),
            "use" or "切换" => args.Count == 1 ? new ChatUse(args[0]) : Usage("/use 设备名"),
            "ls" or "list" or "列表" => args.Count <= 1 ? new ChatList(args.FirstOrDefault()) : Usage("/ls [设备名]"),
            "read" or "r" or "看" => ParseRead(args),
            "new" or "新建" => ParseNew(args),
            "yes" or "y" or "ok" or "同意" => ParseRespond(args, approve: true),
            "no" or "n" or "拒绝" => ParseRespond(args, approve: false),
            "keys" or "key" or "按键" => ParseKeys(args),
            "name" or "rename" or "改名" => ParseRename(args),
            "butler" or "管家" => args.Count <= 1 ? new ChatButler(args.FirstOrDefault()) : Usage("/butler [设备名]"),
            "pair" or "配对" => ParsePair(args),
            "unpair" or "解绑" => args.Count == 1 ? new ChatUnpair(args[0]) : Usage("/unpair 设备名"),
            _ => new ChatInvalid($"不认识的指令 /{tokens[0]}。发 /help 查看用法"),
        };
    }

    private static ChatCommand ParsePrompt(string rest)
    {
        var split = rest.IndexOfAny([' ', '\t', '\n', '\r', '　']);
        if (split <= 0)
        {
            return Usage("@目标 内容（例如 @tideq 跑一下测试）");
        }

        var target = TargetRef.TryParse(rest[..split]);
        var body = rest[(split + 1)..].Trim();
        if (target is null || body.Length == 0)
        {
            return Usage("@目标 内容（例如 @tideq 跑一下测试）");
        }

        return body.Length > ProtocolInfo.MaxPromptChars
            ? new ChatInvalid($"内容太长（上限 {ProtocolInfo.MaxPromptChars} 字）")
            : new ChatPrompt(target, body);
    }

    private static ChatCommand ParseRead(List<string> args)
    {
        if (args.Count is < 1 or > 2 || TargetRef.TryParse(args[0]) is not { } target)
        {
            return Usage("/read 目标 [行数]");
        }

        var lines = ProtocolInfo.DefaultReadLines;
        if (args.Count == 2 && (!int.TryParse(args[1], out lines) || lines is < ProtocolInfo.MinReadLines or > ProtocolInfo.MaxReadLines))
        {
            return new ChatInvalid($"行数必须是 {ProtocolInfo.MinReadLines}–{ProtocolInfo.MaxReadLines} 之间的数字");
        }

        return new ChatRead(target, lines);
    }

    /// <summary><c>/new [设备] 目录 [类型] [名字]</c>：第一个参数不像路径时当作设备名。</summary>
    private static ChatCommand ParseNew(List<string> args)
    {
        const string usage = "/new [设备] 目录 [类型] [名字]（目录要写绝对路径或以 ~ 开头）";
        string? device = null;
        if (args.Count > 0 && !LooksLikePath(args[0]))
        {
            device = args[0];
            args = args.Skip(1).ToList();
        }

        if (args.Count is < 1 or > 3 || !LooksLikePath(args[0]))
        {
            return Usage(usage);
        }

        if (device is not null && !Validation.IsDeviceName(device))
        {
            return new ChatInvalid("设备名格式不对");
        }

        var kind = args.Count >= 2 ? args[1].ToLowerInvariant() : null;
        if (kind is not null && !Validation.IsAgentKindSyntax(kind))
        {
            return new ChatInvalid("类型格式不对，例如 claude、reclaude、codex");
        }

        var name = args.Count == 3 ? args[2] : null;
        if (name is not null && !Validation.IsAgentName(name))
        {
            return new ChatInvalid("名字只能用小写字母开头，包含小写字母、数字、下划线、短横线，最长 32 位");
        }

        return new ChatNew(device, args[0], kind, name);
    }

    private static ChatCommand ParseRespond(List<string> args, bool approve)
    {
        var usage = approve ? "/yes 目标" : "/no 目标";
        return args.Count == 1 && TargetRef.TryParse(args[0]) is { } target ? new ChatRespond(target, approve) : Usage(usage);
    }

    private static ChatCommand ParseKeys(List<string> args)
    {
        if (args.Count < 2 || TargetRef.TryParse(args[0]) is not { } target)
        {
            return Usage("/keys 目标 按键…（例如 /keys tideq down enter）");
        }

        var keys = args.Skip(1).Select(k => k.ToLowerInvariant()).ToList();
        if (keys.Count > ProtocolInfo.MaxKeysPerCommand)
        {
            return new ChatInvalid($"一次最多 {ProtocolInfo.MaxKeysPerCommand} 个按键");
        }

        var rejected = keys.Where(k => !Validation.IsAllowedKey(k)).ToList();
        return rejected.Count == 0
            ? new ChatKeys(target, keys)
            : new ChatInvalid($"不允许的按键：{string.Join(" ", rejected)}。允许：{string.Join(" ", Validation.AllowedKeys.Order(StringComparer.Ordinal))}");
    }

    private static ChatCommand ParseRename(List<string> args)
    {
        if (args.Count != 2 || TargetRef.TryParse(args[0]) is not { } target)
        {
            return Usage("/name 目标 新名字");
        }

        return Validation.IsAgentName(args[1])
            ? new ChatRename(target, args[1])
            : new ChatInvalid("名字只能用小写字母开头，包含小写字母、数字、下划线、短横线，最长 32 位");
    }

    private static ChatCommand ParsePair(List<string> args)
    {
        if (args.Count > 1)
        {
            return Usage("/pair [设备名]");
        }

        var name = args.FirstOrDefault();
        return name is null || Validation.IsDeviceName(name)
            ? new ChatPair(name)
            : new ChatInvalid("设备名只能包含字母、数字、下划线、短横线，最长 32 位");
    }

    /// <summary>Unix 绝对路径、~ 开头、Windows 盘符路径或 UNC 路径。</summary>
    internal static bool LooksLikePath(string token) =>
        token.StartsWith('/') || token == "~" || token.StartsWith("~/", StringComparison.Ordinal) || token.StartsWith("~\\", StringComparison.Ordinal)
        || (token.Length >= 3 && char.IsAsciiLetter(token[0]) && token[1] == ':' && token[2] is '\\' or '/')
        || token.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>按空白切分，支持用 "…" 或 “…” 包住带空格的参数。</summary>
    internal static bool TryTokenize(string text, out List<string> tokens)
    {
        tokens = [];
        var current = new StringBuilder();
        char? quote = null;
        var hasToken = false;
        foreach (var ch in text)
        {
            if (quote is { } open)
            {
                if (ch == ClosingQuote(open))
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '"' or '“')
            {
                quote = ch;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(ch);
                hasToken = true;
            }
        }

        if (quote is not null)
        {
            return false;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return true;
    }

    private static char ClosingQuote(char open) => open == '“' ? '”' : '"';

    private static ChatInvalid Usage(string usage) => new("用法：" + usage);
}
