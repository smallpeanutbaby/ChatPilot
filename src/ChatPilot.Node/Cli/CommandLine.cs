namespace ChatPilot.Node.Cli;

/// <summary>极简命令行解析：<c>命令 --选项 值 --开关</c>，同名选项可以重复。</summary>
public sealed class CommandLine
{
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal) { "force", "help" };

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    public string? Command { get; private init; }

    public List<string> Errors { get; } = [];

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

    public string? Get(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> GetAll(string name) => _options.TryGetValue(name, out var values) ? values : [];

    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>检查有没有不认识的选项。</summary>
    public void RejectUnknown(params string[] known)
    {
        foreach (var name in _options.Keys.Where(k => !known.Contains(k) && k != "help"))
        {
            Errors.Add($"不认识的选项：--{name}");
        }
    }

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
