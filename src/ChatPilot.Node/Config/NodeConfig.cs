using System.Text.Json.Serialization;
using ChatPilot.Shared;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Config;

/// <summary>节点配置，保存在 <c>~/.chatpilot/node.json</c>（权限 600）。</summary>
public sealed class NodeConfig
{
    /// <summary>服务器地址，例如 <c>https://example.com:8443</c>；只有本机回环地址才允许用 http。</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>服务器证书指纹（<c>sha256:…</c>），https 时必填。</summary>
    public string? ServerFingerprint { get; set; }

    /// <summary>配对时服务器分配的设备 ID。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>设备名，手机上用它指定设备（例如 <c>@mac/tideq</c>）。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>设备令牌，相当于密码。</summary>
    public string Token { get; set; } = "";

    /// <summary>herdr 可执行文件路径；开机自启时 PATH 很短，建议写绝对路径。</summary>
    public string HerdrPath { get; set; } = "herdr";

    /// <summary>herdr 的命名会话；为空时用默认会话。</summary>
    public string? HerdrSession { get; set; }

    /// <summary>git 可执行文件（<c>/diff</c> 用）；开机自启时 PATH 很短，找不到时写绝对路径。</summary>
    public string GitPath { get; set; } = "git";

    /// <summary><c>/new</c> 只允许在这些目录（含子目录）里打开工作区。</summary>
    public List<string> AllowedRoots { get; set; } = [];

    /// <summary><c>/new</c> 不指定类型时用的 agent 类型。</summary>
    public string DefaultAgentKind { get; set; } = "claude";

    /// <summary>
    /// 自定义启动方式：键是类型名，在新终端里运行 <see cref="AgentLauncher.Command"/>，
    /// 再等 herdr 识别出 <see cref="AgentLauncher.DetectAs"/> 类型的 agent。默认带 reclaude。
    /// </summary>
    public Dictionary<string, AgentLauncher> Launchers { get; set; } = new(StringComparer.Ordinal)
    {
        ["reclaude"] = new AgentLauncher { Command = "reclaude", DetectAs = "claude" },
    };

    /// <summary>额外的 Claude 会话记录目录（<c>…/projects</c>），用于读取完整回复。</summary>
    public List<string> ClaudeProjectRoots { get; set; } = [];

    /// <summary>单次 herdr 调用的超时（秒）。一条指令总共不超过 28 秒，服务器最多等 30 秒。</summary>
    public int CommandTimeoutSeconds { get; set; } = 25;

    /// <summary>等 agent 启动就绪的超时（秒）。整个启动过程不超过它加 20 秒，服务器最多等 120 秒。</summary>
    public int AgentStartTimeoutSeconds { get; set; } = 60;

    /// <summary>等一个任务做完最多等多久（小时）。</summary>
    public int PromptWaitHours { get; set; } = 6;

    /// <summary>回传回复的最大字符数。</summary>
    public int MaxReplyChars { get; set; } = 12000;

    /// <summary>检查配置是否完整、安全；有问题时返回错误说明。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https"))
        {
            errors.Add("ServerUrl 必须是 http:// 或 https:// 开头的地址");
        }
        else if (Connection.ServerEndpoint.CheckTransportSecurity(server, ServerFingerprint) is { } transportError)
        {
            errors.Add(transportError);
        }

        if (string.IsNullOrWhiteSpace(DeviceId) || string.IsNullOrWhiteSpace(Token))
        {
            errors.Add("还没配对：缺少 DeviceId 或 Token");
        }

        if (!Validation.IsDeviceName(DeviceName))
        {
            errors.Add("DeviceName 格式不对");
        }

        if (string.IsNullOrWhiteSpace(HerdrPath))
        {
            errors.Add("HerdrPath 不能为空");
        }
        else if (IsScriptWrapper(HerdrPath))
        {
            // Windows 上启动 .cmd/.bat 会隐式经过 cmd.exe，参数按 cmd 规则解析，文字里的 & | % 可能被当成命令。
            errors.Add("HerdrPath 必须是可执行文件，不能是 .cmd / .bat / .ps1 脚本");
        }

        if (string.IsNullOrWhiteSpace(GitPath) || IsScriptWrapper(GitPath))
        {
            errors.Add("GitPath 必须是可执行文件，不能为空，也不能是 .cmd / .bat / .ps1 脚本");
        }

        if (HerdrSession is not null && !Validation.IsAgentName(HerdrSession))
        {
            errors.Add("HerdrSession 只能包含小写字母、数字、下划线和短横线");
        }

        foreach (var root in AllowedRoots)
        {
            if (!Path.IsPathFullyQualified(Execution.PathPolicy.ExpandHome(root)))
            {
                errors.Add($"AllowedRoots 里的 {root} 不是绝对路径");
            }
        }

        foreach (var (kind, launcher) in Launchers)
        {
            if (!Validation.IsAgentKindSyntax(kind))
            {
                errors.Add($"Launchers 的类型名 {kind} 格式不对");
            }

            if (string.IsNullOrWhiteSpace(launcher.Command) || launcher.Command.Contains('\n', StringComparison.Ordinal) || launcher.Command.Contains('\r', StringComparison.Ordinal))
            {
                errors.Add($"Launchers.{kind}.Command 不能为空，也不能换行");
            }

            if (!Validation.BuiltInAgentKinds.Contains(launcher.DetectAs))
            {
                errors.Add($"Launchers.{kind}.DetectAs 必须是 herdr 认识的类型");
            }
        }

        if (!IsKindAvailable(DefaultAgentKind))
        {
            errors.Add($"DefaultAgentKind {DefaultAgentKind} 不是可用的类型");
        }

        if (CommandTimeoutSeconds is < 5 or > 25)
        {
            errors.Add("CommandTimeoutSeconds 必须在 5–25 之间（一条指令总共要在 28 秒内完成，服务器最多等 30 秒）");
        }

        if (AgentStartTimeoutSeconds is < 10 or > 90)
        {
            errors.Add("AgentStartTimeoutSeconds 必须在 10–90 之间（启动总共要在它加 20 秒内完成，服务器最多等 120 秒）");
        }

        if (PromptWaitHours is < 1 or > 48)
        {
            errors.Add("PromptWaitHours 必须在 1–48 之间");
        }

        if (MaxReplyChars is < 500 or > 16000)
        {
            errors.Add("MaxReplyChars 必须在 500–16000 之间");
        }

        return errors;
    }

    /// <summary>脚本包装器（Windows 上会经过命令解释器），不能作为 herdr 可执行文件。</summary>
    public static bool IsScriptWrapper(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

    /// <summary>这个 agent 类型能不能用：herdr 内置类型，或者配置了启动方式的类型。</summary>
    public bool IsKindAvailable(string kind) => Launchers.ContainsKey(kind) || Validation.BuiltInAgentKinds.Contains(kind);

    /// <summary>可用类型列表，给用户看。</summary>
    public IEnumerable<string> AvailableKinds() =>
        Validation.BuiltInAgentKinds.Concat(Launchers.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>规范化后的证书指纹；没配置或格式不对时为 null。</summary>
    [JsonIgnore]
    public string? NormalizedFingerprint => CertificatePin.Normalize(ServerFingerprint);
}

/// <summary>自定义的 agent 启动方式（例如 reclaude）。</summary>
public sealed class AgentLauncher
{
    /// <summary>在新终端里运行的命令（来自本机配置，不接受远程输入）。</summary>
    public string Command { get; set; } = "";

    /// <summary>herdr 识别出来的 agent 类型。</summary>
    public string DetectAs { get; set; } = "claude";
}
