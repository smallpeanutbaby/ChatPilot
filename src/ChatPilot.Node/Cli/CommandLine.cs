namespace ChatPilot.Node.Cli;

/// <summary>极简命令行解析：<c>命令 --选项 值 --开关</c>，同名选项可以重复。</summary>
public sealed class CommandLine
{
    /// <summary>开关类选项：后面不跟值，出现就记为 <c>true</c>。</summary>
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal) { "force", "help", "check", "no-restart" };

    /// <summary>选项名（不带 <c>--</c>）到它出现过的所有值，按出现顺序。</summary>
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    /// <summary>第一个参数（命令名），没有时为 null。</summary>
    public string? Command { get; private init; }

    /// <summary>解析时发现的问题，给用户看。</summary>
    public List<string> Errors { get; } = [];

    /// <summary>解析命令行参数。</summary>
    /// <param name="args">命令行参数；第一个不以 <c>-</c> 开头时当作命令名。</param>
    /// <returns>解析结果；格式不对的参数记在 <see cref="Errors"/> 里，不抛异常。</returns>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var command = args.Count > 0 && !args[0].StartsWith('-') ? args[0] : null;
        var result = new CommandLine { Command = command };
        for (var i = command is null ? 0 : 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h")
            {
                arg = "--help";
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                result.Errors.Add($"无法识别的参数：{arg}");
                continue;
            }

            var name = arg[2..];
            if (Switches.Contains(name))
            {
                result.Add(name, "true");
                continue;
            }

            if (i + 1 >= args.Count)
            {
                result.Errors.Add($"--{name} 后面需要一个值");
                continue;
            }

            result.Add(name, args[++i]);
        }

        return result;
    }

    /// <summary>取选项的值；重复出现时取最后一个，没有时返回 null。</summary>
    /// <param name="name">选项名，不带 <c>--</c>。</param>
    /// <returns>最后一次出现的值；没给这个选项时为 null。</returns>
    public string? Get(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;

    /// <summary>取可以重复出现的选项的所有值（例如多个 <c>--root</c>）。</summary>
    /// <param name="name">选项名，不带 <c>--</c>。</param>
    /// <returns>按出现顺序的所有值；没给这个选项时为空列表。</returns>
    public IReadOnlyList<string> GetAll(string name) => _options.TryGetValue(name, out var values) ? values : [];

    /// <summary>是否给了这个选项（开关类选项用）。</summary>
    /// <param name="name">选项名，不带 <c>--</c>。</param>
    /// <returns>至少出现过一次时为 true。</returns>
    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>检查有没有不认识的选项。</summary>
    /// <param name="known">这个命令认识的选项名（不带 <c>--</c>）；<c>help</c> 总是认识的。不认识的会追加到 <see cref="Errors"/>。</param>
    public void RejectUnknown(params string[] known)
    {
        foreach (var name in _options.Keys.Where(k => !known.Contains(k) && k != "help"))
        {
            Errors.Add($"不认识的选项：--{name}");
        }
    }

    /// <summary>记下选项的一个值，保留之前出现过的值。</summary>
    /// <param name="name">选项名，不带 <c>--</c>。</param>
    /// <param name="value">选项的值；开关类选项是 <c>true</c>。</param>
    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            values = [];
            _options[name] = values;
        }

        values.Add(value);
    }
}
