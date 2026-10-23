using System.Globalization;
using System.Text.Json;

namespace ChatPilot.Node.Herdr;

/// <summary>
/// 调用 herdr 命令行。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>所有参数按数组传给进程，不经过 shell。</item>
/// <item>自由文本只出现在 prompt 的文字参数里；herdr 不支持用 <c>--</c> 结束选项，所以以 <c>-</c> 开头的文字前面加一个空格。</item>
/// <item>herdr 的返回按"结构不对就当作 bad_response"处理，不会因为字段类型意外而抛出其他异常。</item>
/// </list>
/// </remarks>
public sealed class HerdrClient(IProcessRunner runner, string herdrPath, string? session, TimeSpan defaultTimeout)
{
    /// <summary>列出所有 agent。结构不完整的条目会被跳过。</summary>
    public async Task<IReadOnlyList<HerdrAgent>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["agent", "list"], defaultTimeout, cancellationToken).ConfigureAwait(false);
        return ArrayProperty(result, "agents")
            .Select(HerdrAgent.TryParse)
            .OfType<HerdrAgent>()
            .ToList();
    }

    /// <summary>列出所有工作区（用来给 agent 配上工作区名）。</summary>
    public async Task<IReadOnlyList<HerdrWorkspace>> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["workspace", "list"], defaultTimeout, cancellationToken).ConfigureAwait(false);
        var list = new List<HerdrWorkspace>();
        foreach (var workspace in ArrayProperty(result, "workspaces"))
        {
            if (HerdrAgent.GetString(workspace, "workspace_id") is { } id)
            {
                list.Add(new HerdrWorkspace(id, HerdrAgent.GetString(workspace, "label")));
            }
        }

        return list;
    }

    /// <summary>按名字或 pane ID 取一个 agent。</summary>
    /// <exception cref="HerdrException">找不到（code 以 not_found 结尾）或 herdr 出错。</exception>
    public async Task<HerdrAgent> GetAgentAsync(string target, CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["agent", "get", target], defaultTimeout, cancellationToken).ConfigureAwait(false);
        return HerdrAgent.TryParse(RequireObject(result, "agent"))
            ?? throw new HerdrException("bad_response", "herdr 返回的 agent 不完整");
    }

    /// <summary>同 <see cref="GetAgentAsync"/>，但目标不存在（或那个 pane 里还没识别出 agent）时返回 null。</summary>
    public async Task<HerdrAgent?> TryGetAgentAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            return await GetAgentAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (HerdrException ex) when (IsNotFound(ex.Code))
        {
            return null;
        }
    }

    /// <summary>读 agent 终端输出（纯文本）。</summary>
    /// <param name="target">pane ID 或 agent 名字。</param>
    /// <param name="lines">最多读几行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="source"><c>visible</c>（当前屏幕）、<c>recent</c> 或 <c>recent-unwrapped</c>（最近的输出，折行已合并）。</param>
    public Task<string> ReadAsync(string target, string source, int lines, CancellationToken cancellationToken) =>
        InvokeTextAsync(
            ["agent", "read", target, "--source", source, "--lines", lines.ToString(CultureInfo.InvariantCulture)],
            defaultTimeout,
            cancellationToken);

    /// <summary>
    /// 把文字交给 agent，提交完就返回（不等它做完）。
    /// 不用 herdr 的 <c>--wait</c>：那样文字会作为进程参数挂好几个小时，同机其他用户用 <c>ps</c> 就能看到。
    /// </summary>
    public async Task<HerdrAgent?> PromptAsync(string target, string text, CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["agent", "prompt", target, ProtectText(text)], defaultTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ObjectProperty(result, "agent") is { } agent ? HerdrAgent.TryParse(agent) : null;
    }

    /// <summary>等 agent 进入 <paramref name="until"/> 中的任一状态；已经处在这些状态时立即返回。</summary>
    /// <exception cref="HerdrException">超时时 code 为 <c>timeout</c>。</exception>
    public async Task<HerdrAgent?> WaitAsync(string target, IReadOnlyList<string> until, TimeSpan timeout, CancellationToken cancellationToken)
    {
        List<string> args = ["agent", "wait", target];
        foreach (var status in until)
        {
            args.AddRange(["--until", status]);
        }

        args.AddRange(["--timeout", ToMilliseconds(timeout)]);
        var result = await InvokeJsonAsync(args, timeout + TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return ObjectProperty(result, "agent") is { } agent ? HerdrAgent.TryParse(agent) : null;
    }

    /// <summary>新建工作区（不抢焦点），返回工作区 ID 和根 pane 的 ID。</summary>
    public async Task<(string WorkspaceId, string PaneId)> CreateWorkspaceAsync(string cwd, string label, CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(
            ["workspace", "create", "--cwd", cwd, "--label", label, "--no-focus"],
            defaultTimeout,
            cancellationToken).ConfigureAwait(false);
        var workspaceId = HerdrAgent.GetString(RequireObject(result, "workspace"), "workspace_id");
        var paneId = HerdrAgent.GetString(RequireObject(result, "root_pane"), "pane_id");
        return workspaceId is not null && paneId is not null
            ? (workspaceId, paneId)
            : throw new HerdrException("bad_response", "herdr 新建工作区后没有返回 pane ID");
    }

    /// <summary>关闭工作区（启动失败时清理用）。</summary>
    public Task CloseWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["workspace", "close", workspaceId], defaultTimeout, cancellationToken);

    /// <summary>用 herdr 内置方式启动 agent；成功时 agent 已经可以接收指令。</summary>
    /// <exception cref="HerdrException">启动时卡在确认框时 code 为 <c>agent_not_ready</c>（agent 已经在了）。</exception>
    public Task StartAgentAsync(string name, string kind, string paneId, TimeSpan timeout, CancellationToken cancellationToken) =>
        InvokeCommandAsync(
            ["agent", "start", name, "--kind", kind, "--pane", paneId, "--timeout", ToMilliseconds(timeout)],
            timeout + TimeSpan.FromSeconds(5),
            cancellationToken);

    /// <summary>在 pane 里运行一条命令（命令来自本机配置，不接受远程输入）。herdr 成功时不输出任何内容。</summary>
    public Task RunInPaneAsync(string paneId, string command, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["pane", "run", paneId, command], defaultTimeout, cancellationToken);

    /// <summary>给 agent 发送按键（调用方负责校验白名单）。</summary>
    public Task SendKeysAsync(string target, IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["agent", "send-keys", target, .. keys], defaultTimeout, cancellationToken);

    /// <summary>给 agent 改名（调用方负责校验名字格式）。</summary>
    public Task RenameAsync(string target, string name, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["agent", "rename", target, name], defaultTimeout, cancellationToken);

    /// <summary>herdr 的"找不到"类错误码都以 <c>not_found</c> 结尾（agent_not_found、pane_not_found…）。</summary>
    public static bool IsNotFound(string code) => code.EndsWith("not_found", StringComparison.Ordinal);

    /// <summary>以 <c>-</c> 开头的文字加一个前导空格，避免被 herdr 当成选项。</summary>
    internal static string ProtectText(string text) => text.StartsWith('-') ? " " + text : text;

    private static JsonElement? ObjectProperty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement RequireObject(JsonElement element, string property) =>
        ObjectProperty(element, property) ?? throw new HerdrException("bad_response", $"herdr 的返回里没有 {property}");

    private static IEnumerable<JsonElement> ArrayProperty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string ToMilliseconds(TimeSpan value) =>
        ((long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    private List<string> BuildArguments(IEnumerable<string> args)
    {
        var all = new List<string>();
        if (!string.IsNullOrEmpty(session))
        {
            all.AddRange(["--session", session]);
        }

        all.AddRange(args);
        return all;
    }

    /// <summary>运行并解析 <c>{"result": {...}}</c>；返回的 result 一定是对象。</summary>
    private async Task<JsonElement> InvokeJsonAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                throw ToException(error);
            }

            return ObjectProperty(root, "result")?.Clone()
                ?? throw new HerdrException("bad_response", "herdr 的返回里没有 result");
        }
        catch (JsonException)
        {
            throw new HerdrException("bad_response", "herdr 返回的不是 JSON：" + Shorten(result.StandardOutput));
        }
    }

    /// <summary>只关心成功与否的命令：退出码为 0 且输出里没有 error 就算成功，输出可以为空。</summary>
    private async Task InvokeCommandAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        if (TryParseError(result.StandardOutput) is { } error)
        {
            throw error;
        }
    }

    private async Task<string> InvokeTextAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    /// <summary>运行 herdr；退出码不为 0 时解析 stderr（或 stdout）里的 JSON 错误并抛出。</summary>
    private async Task<ProcessResult> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(herdrPath, BuildArguments(args), timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            throw new HerdrException("herdr_unavailable", ex.Message);
        }
        catch (TimeoutException)
        {
            throw new HerdrException("timeout", $"herdr 调用超时（{timeout.TotalSeconds:0} 秒）");
        }

        if (result.ExitCode == 0)
        {
            return result;
        }

        foreach (var output in new[] { result.StandardError, result.StandardOutput })
        {
            if (TryParseError(output) is { } parsed)
            {
                throw parsed;
            }
        }

        throw new HerdrException("herdr_error", $"herdr 退出码 {result.ExitCode}：{Shorten(result.StandardError)}");
    }

    private static HerdrException? TryParseError(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) ? ToException(error) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HerdrException ToException(JsonElement error) =>
        new(HerdrAgent.GetString(error, "code") ?? "herdr_error", HerdrAgent.GetString(error, "message") ?? "herdr 返回了错误");

    private static string Shorten(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";
}
