using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChatPilot.Shared;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Server.Chat;

/// <summary>
/// 聊天里的目标：<c>序号</c>、<c>名字</c>、<c>pane ID</c>、<c>工作区名</c>，前面可以加 <c>设备/</c> 指定设备。
/// </summary>
/// <param name="Device">指定的设备名；没写 <c>设备/</c> 时为 null。</param>
/// <param name="Value">目标本身。</param>
public sealed record TargetRef(string? Device, string Value)
{
    /// <summary>是不是 <c>/ls</c> 列表里的序号（1–4 位数字）。</summary>
    public bool IsIndex => Value.All(char.IsAsciiDigit) && Value.Length is > 0 and <= 4;

    /// <summary>还原成用户写的形式，例如 <c>mac/tideq</c>。</summary>
    public override string ToString() => Device is null ? Value : $"{Device}/{Value}";

    /// <summary>解析目标；格式不对（有空格、多个斜杠、设备名不合法）返回 null。</summary>
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

/// <summary>解析后的聊天指令。</summary>
public abstract record ChatCommand;

/// <summary><c>/help</c>：显示帮助。</summary>
public sealed record ChatHelp : ChatCommand;

/// <summary><c>/devices</c>：设备列表。</summary>
public sealed record ChatDevices : ChatCommand;

/// <summary><c>/use 设备</c>：切换默认设备。</summary>
/// <param name="Device">设备名。</param>
public sealed record ChatUse(string Device) : ChatCommand;

/// <summary><c>/ls [设备]</c>：列出 agent。</summary>
/// <param name="Device">只列这台设备；为 null 时列出所有设备。</param>
public sealed record ChatList(string? Device) : ChatCommand;

/// <summary><c>/read [目标] [行数]</c>：看最近的输出。</summary>
/// <param name="Target">目标 agent；为 null 时用最近打过交道的。</param>
/// <param name="Lines">行数。</param>
public sealed record ChatRead(TargetRef? Target, int Lines) : ChatCommand;

/// <summary><c>/last [目标]</c>：补看 agent 最近一条回复。</summary>
/// <param name="Target">目标 agent；为 null 时用最近打过交道的。</param>
public sealed record ChatLast(TargetRef? Target) : ChatCommand;

/// <summary><c>@目标 内容</c>：把任务交给 agent。</summary>
/// <param name="Target">目标 agent。</param>
/// <param name="Text">任务内容（原样保留换行和符号）。</param>
public sealed record ChatPrompt(TargetRef Target, string Text) : ChatCommand;

/// <summary><c>/re 内容</c>：把任务交给最近打过交道的 agent。</summary>
/// <param name="Text">任务内容（原样保留）。</param>
public sealed record ChatReply(string Text) : ChatCommand;

/// <summary><c>/diff [目标] [文件]</c>：看 agent 工作目录里的 git 改动。</summary>
/// <param name="Target">目标 agent；为 null 时用最近打过交道的。</param>
/// <param name="Path">只看这个文件；为 null 时看概要。</param>
public sealed record ChatDiff(TargetRef? Target, string? Path) : ChatCommand;

/// <summary><c>/stop [目标]</c>：打断 agent，清空排队的指令。</summary>
/// <param name="Target">目标 agent；为 null 时用最近打过交道的。</param>
public sealed record ChatStop(TargetRef? Target) : ChatCommand;

/// <summary><c>/close 目标 [确认]</c>：关闭 agent；第一次只给出确认提示。</summary>
/// <param name="Target">目标 agent。</param>
/// <param name="Confirmed">是否带了"确认"。</param>
public sealed record ChatClose(TargetRef Target, bool Confirmed) : ChatCommand;

/// <summary><c>/watch</c>：列出在盯着哪些 agent。</summary>
public sealed record ChatWatchList : ChatCommand;

/// <summary><c>/watch 目标</c>、<c>/unwatch 目标</c>：开始 / 停止盯一个 agent。</summary>
/// <param name="Target">目标 agent。</param>
/// <param name="Enable">true 开始盯。</param>
public sealed record ChatWatch(TargetRef Target, bool Enable) : ChatCommand;

/// <summary><c>/watch all [设备]</c>、<c>/unwatch all [设备]</c>：开始 / 停止盯一台（或所有在线）设备上的全部 agent。</summary>
/// <param name="Device">设备名；为 null 时表示所有在线设备。</param>
/// <param name="Enable">true 开始盯。</param>
public sealed record ChatWatchAll(string? Device, bool Enable) : ChatCommand;

/// <summary><c>/pending</c>：待办（在等确认、在干活、排队、定时任务）。</summary>
public sealed record ChatPending : ChatCommand;

/// <summary><c>/history [条数]</c>：最近做完的任务。</summary>
/// <param name="Count">显示几条。</param>
public sealed record ChatHistory(int Count) : ChatCommand;

/// <summary><c>/at</c> 的时间：相对现在多久，或者某个时刻（今天已过就是明天）。</summary>
/// <param name="Delay">相对现在的时长；按时刻指定时为 null。</param>
/// <param name="Hour">时刻的小时（0–23）。</param>
/// <param name="Minute">时刻的分钟（0–59）。</param>
/// <param name="Tomorrow">写了"明天"。</param>
public sealed record ScheduleTime(TimeSpan? Delay, int Hour = 0, int Minute = 0, bool Tomorrow = false);

/// <summary><c>/at 时间 内容</c>：定时派活。</summary>
/// <param name="When">什么时候。</param>
/// <param name="Text">到点执行的消息：<c>@目标 内容</c> 或交给管家的文字。</param>
public sealed record ChatAt(ScheduleTime When, string Text) : ChatCommand;

/// <summary><c>/at</c>：列出定时任务。</summary>
public sealed record ChatAtList : ChatCommand;

/// <summary><c>/at del 编号</c>：取消定时任务。</summary>
/// <param name="Id">编号。</param>
public sealed record ChatAtCancel(int Id) : ChatCommand;

/// <summary><c>/wake 设备</c>：通过同一局域网里的其他在线设备唤醒它。</summary>
/// <param name="Device">设备名。</param>
public sealed record ChatWake(string Device) : ChatCommand;

/// <summary><c>/drop</c>：丢掉暂存着、还没发出去的图片。</summary>
public sealed record ChatDropImages : ChatCommand;

/// <summary>不是指令的文字，交给当前设备的管家。</summary>
/// <param name="Text">原文。</param>
public sealed record ChatButlerPrompt(string Text) : ChatCommand;

/// <summary><c>/new [设备] 目录 [类型] [名字]</c>：新开工作区并启动 agent。</summary>
/// <param name="Device">设备名；为 null 时用默认设备。</param>
/// <param name="Directory">工作目录（绝对路径或以 ~ 开头），由节点检查白名单。</param>
/// <param name="Kind">agent 类型；为 null 时用节点的默认类型。</param>
/// <param name="Name">agent 名字；为 null 时由目录名生成。</param>
public sealed record ChatNew(string? Device, string Directory, string? Kind, string? Name) : ChatCommand;

/// <summary><c>/yes [目标]</c> 或 <c>/no [目标]</c>：回应确认框。</summary>
/// <param name="Target">目标 agent；为 null 时用在等确认的那个（只有一个时），否则用最近打过交道的。</param>
/// <param name="Approve">true 表示同意。</param>
public sealed record ChatRespond(TargetRef? Target, bool Approve) : ChatCommand;

/// <summary><c>/keys 目标 按键…</c>：发送白名单内的按键。</summary>
/// <param name="Target">目标 agent。</param>
/// <param name="Keys">按键（已转成小写并通过白名单检查）。</param>
public sealed record ChatKeys(TargetRef Target, IReadOnlyList<string> Keys) : ChatCommand;

/// <summary><c>/name 目标 新名字</c>：给 agent 改名。</summary>
/// <param name="Target">目标 agent。</param>
/// <param name="Name">新名字。</param>
public sealed record ChatRename(TargetRef Target, string Name) : ChatCommand;

/// <summary><c>/butler [设备]</c>：创建管家 agent。</summary>
/// <param name="Device">设备名；为 null 时用默认设备。</param>
public sealed record ChatButler(string? Device) : ChatCommand;

/// <summary><c>/pair [设备名]</c>：生成配对码。</summary>
/// <param name="DeviceName">预先指定的设备名；为 null 时由节点决定。</param>
public sealed record ChatPair(string? DeviceName) : ChatCommand;

/// <summary><c>/unpair 设备</c>：撤销设备。</summary>
/// <param name="Device">设备名。</param>
public sealed record ChatUnpair(string Device) : ChatCommand;

/// <summary>解析失败。</summary>
/// <param name="Message">给用户看的用法提示。</param>
public sealed record ChatInvalid(string Message) : ChatCommand;

/// <summary>把聊天文字解析成指令。纯函数，不访问任何状态。</summary>
public static partial class ChatCommandParser
{
    /// <summary><c>/history</c> 最多显示几条。</summary>
    internal const int MaxHistory = 30;

    /// <summary><c>/at</c> 最远能定到多久以后。</summary>
    internal static readonly TimeSpan MaxScheduleDelay = TimeSpan.FromDays(7);

    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r', '　'];

    private static readonly string[] ConfirmWords = ["确认", "confirm", "yes", "y"];

    /// <summary>解析一条聊天消息；不会抛异常，格式不对时返回 <see cref="ChatInvalid"/>。</summary>
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

        // 带自由文字的指令：文字原样保留（换行、引号都不处理），不走下面的分词。
        var head = text[1..];
        var nameEnd = head.IndexOfAny(Whitespace);
        var rawName = (nameEnd < 0 ? head : head[..nameEnd]).ToLowerInvariant();
        var rawRest = nameEnd < 0 ? "" : head[(nameEnd + 1)..].Trim();
        switch (rawName)
        {
            case "re" or "接着":
                return ParseReply(rawRest);
            case "at" or "定时":
                return ParseAt(rawRest);
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
            "last" or "回复" => ParseOptionalTarget(args, "/last [目标]", t => new ChatLast(t)),
            "new" or "新建" => ParseNew(args),
            "yes" or "y" or "ok" or "同意" => ParseOptionalTarget(args, "/yes [目标]", t => new ChatRespond(t, true)),
            "no" or "n" or "拒绝" => ParseOptionalTarget(args, "/no [目标]", t => new ChatRespond(t, false)),
            "keys" or "key" or "按键" => ParseKeys(args),
            "name" or "rename" or "改名" => ParseRename(args),
            "diff" or "改动" => ParseDiff(args),
            "stop" or "停" => ParseOptionalTarget(args, "/stop [目标]", t => new ChatStop(t)),
            "close" or "关闭" => ParseClose(args),
            "watch" or "盯" => ParseWatch(args, enable: true),
            "unwatch" or "不盯" => ParseWatch(args, enable: false),
            "pending" or "todo" or "待办" => args.Count == 0 ? new ChatPending() : Usage("/pending"),
            "history" or "历史" => ParseHistory(args),
            "wake" or "唤醒" => args.Count == 1 && Validation.IsDeviceName(args[0]) ? new ChatWake(args[0]) : Usage("/wake 设备名"),
            "drop" or "丢掉" => args.Count == 0 ? new ChatDropImages() : Usage("/drop"),
            "butler" or "管家" => args.Count <= 1 ? new ChatButler(args.FirstOrDefault()) : Usage("/butler [设备名]"),
            "pair" or "配对" => ParsePair(args),
            "unpair" or "解绑" => args.Count == 1 ? new ChatUnpair(args[0]) : Usage("/unpair 设备名"),
            _ => new ChatInvalid($"不认识的指令 /{tokens[0]}。发 /help 查看用法"),
        };
    }

    /// <summary>可以不写目标的指令：0 个参数用默认目标，1 个参数是目标。</summary>
    private static ChatCommand ParseOptionalTarget(List<string> args, string usage, Func<TargetRef?, ChatCommand> create) => args.Count switch
    {
        0 => create(null),
        1 when TargetRef.TryParse(args[0]) is { } target => create(target),
        _ => Usage(usage),
    };

    private static ChatCommand ParseReply(string body)
    {
        if (body.Length == 0)
        {
            return Usage("/re 内容（发给最近打过交道的 agent）");
        }

        return body.Length > ProtocolInfo.MaxPromptChars
            ? new ChatInvalid($"内容太长（上限 {ProtocolInfo.MaxPromptChars} 字）")
            : new ChatReply(body);
    }

    /// <summary><c>/diff [目标] [文件]</c>：要看单个文件时必须写目标（不然分不清是目标还是文件）。</summary>
    private static ChatCommand ParseDiff(List<string> args)
    {
        const string usage = "/diff [目标] [文件]（看单个文件时要写目标，例如 /diff tideq src/app.ts）";
        if (args.Count == 0)
        {
            return new ChatDiff(null, null);
        }

        if (args.Count > 2 || TargetRef.TryParse(args[0]) is not { } target)
        {
            return Usage(usage);
        }

        if (args.Count == 2 && !Validation.IsRepoRelativePath(args[1]))
        {
            return new ChatInvalid("文件路径要写相对 agent 工作目录的路径，例如 src/app.ts，不能有 ..");
        }

        return new ChatDiff(target, args.Count == 2 ? args[1] : null);
    }

    private static ChatCommand ParseClose(List<string> args)
    {
        if (args.Count is < 1 or > 2 || TargetRef.TryParse(args[0]) is not { } target)
        {
            return Usage("/close 目标（会先让你确认）");
        }

        if (args.Count == 2 && !ConfirmWords.Contains(args[1].ToLowerInvariant()))
        {
            return Usage("/close 目标 确认");
        }

        return new ChatClose(target, args.Count == 2);
    }

    /// <summary><c>/watch</c>、<c>/watch 目标</c>、<c>/watch all [设备]</c>；<c>/unwatch</c> 同理但必须写目标或 all。</summary>
    private static ChatCommand ParseWatch(List<string> args, bool enable)
    {
        var usage = enable ? "/watch [目标 | all [设备]]" : "/unwatch 目标 | all [设备]";
        if (args.Count == 0)
        {
            return enable ? new ChatWatchList() : Usage(usage);
        }

        if (args[0].ToLowerInvariant() is "all" or "全部")
        {
            return args.Count switch
            {
                1 => new ChatWatchAll(null, enable),
                2 when Validation.IsDeviceName(args[1]) => new ChatWatchAll(args[1], enable),
                _ => Usage(usage),
            };
        }

        return args.Count == 1 && TargetRef.TryParse(args[0]) is { } target ? new ChatWatch(target, enable) : Usage(usage);
    }

    private static ChatCommand ParseHistory(List<string> args)
    {
        if (args.Count == 0)
        {
            return new ChatHistory(10);
        }

        return args.Count == 1 && int.TryParse(args[0], out var count) && count is >= 1 and <= MaxHistory
            ? new ChatHistory(count)
            : Usage($"/history [条数]（1–{MaxHistory}）");
    }

    /// <summary>
    /// <c>/at</c>：不带参数列出，<c>/at del 编号</c> 取消，<c>/at 时间 内容</c> 新建。
    /// 时间可以是 <c>23:00</c>（今天已过就是明天）、<c>明天 9:00</c>、<c>30m</c>、<c>2h</c>、<c>1h30m</c>、<c>45分钟</c>、<c>2小时</c>。
    /// </summary>
    private static ChatCommand ParseAt(string rest)
    {
        const string usage = "/at 时间 @目标 内容，例如 /at 23:00 @api 跑全量测试、/at 30m @2 看看测试结果、/at 明天 9:00 整理日报。/at 列出定时任务，/at del 编号 取消";
        if (rest.Length == 0)
        {
            return new ChatAtList();
        }

        var (first, afterFirst) = SplitFirst(rest);
        if (first.ToLowerInvariant() is "del" or "delete" or "rm" or "cancel" or "删除" or "取消")
        {
            return int.TryParse(afterFirst, out var id) && id > 0 ? new ChatAtCancel(id) : Usage("/at del 编号");
        }

        var tomorrow = first.ToLowerInvariant() is "明天" or "tomorrow";
        var (timeToken, payload) = tomorrow ? SplitFirst(afterFirst) : (first, afterFirst);
        if (ParseTime(timeToken, tomorrow) is not { } when)
        {
            return Usage(usage);
        }

        if (payload.Length == 0)
        {
            return Usage(usage);
        }

        return Parse(payload) switch
        {
            ChatPrompt or ChatButlerPrompt => new ChatAt(when, payload),
            ChatInvalid invalid => invalid,
            _ => new ChatInvalid("定时任务的内容只能是 @目标 内容，或者交给管家的文字"),
        };
    }

    /// <summary>解析 <c>/at</c> 的时间；格式不对或超出范围返回 null。</summary>
    internal static ScheduleTime? ParseTime(string token, bool tomorrow)
    {
        var clock = ClockRegex().Match(token);
        if (clock.Success)
        {
            var hour = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
            return hour <= 23 && minute <= 59 ? new ScheduleTime(null, hour, minute, tomorrow) : null;
        }

        var relative = DelayRegex().Match(token);
        if (tomorrow || !relative.Success || (relative.Groups[1].Length == 0 && relative.Groups[2].Length == 0))
        {
            return null;
        }

        var hours = relative.Groups[1].Length > 0 ? int.Parse(relative.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = relative.Groups[2].Length > 0 ? int.Parse(relative.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var delay = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        return delay >= TimeSpan.FromMinutes(1) && delay <= MaxScheduleDelay ? new ScheduleTime(delay) : null;
    }

    /// <summary>按第一个空白切开：第一个词，和后面原样保留的部分。</summary>
    private static (string First, string Remainder) SplitFirst(string text)
    {
        var index = text.IndexOfAny(Whitespace);
        return index < 0 ? (text, "") : (text[..index], text[(index + 1)..].Trim());
    }

    [GeneratedRegex(@"^(\d{1,2})[:：](\d{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"^(?:(\d{1,3})(?:h|小时))?(?:(\d{1,5})(?:m|min|分钟|分))?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DelayRegex();

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
        if (args.Count == 0)
        {
            return new ChatRead(null, ProtocolInfo.DefaultReadLines);
        }

        if (args.Count > 2 || TargetRef.TryParse(args[0]) is not { } target)
        {
            return Usage("/read [目标] [行数]");
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
