using System.Globalization;
using System.Text.Json;

namespace ChatPilot.Node.Herdr;

/// <summary>
/// 调用 herdr 命令行。所有参数按数组传给进程，不经过 shell；
/// 自由文本只出现在 prompt 的文字参数里，并且以 <c>-</c> 开头时会加一个前导空格，防止被当成选项。
/// </summary>
public sealed class HerdrClient(IProcessRunner runner, string herdrPath, string? session, TimeSpan defaultTimeout)
{
    public async Task<IReadOnlyList<HerdrAgent>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["agent", "list"], defaultTimeout, cancellationToken).ConfigureAwait(false);
        return result.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array
            ? agents.EnumerateArray().Select(HerdrAgent.Parse).ToList()
            : [];
    }

    public async Task<IReadOnlyList<HerdrWorkspace>> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["workspace", "list"], defaultTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("workspaces", out var workspaces) || workspaces.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<HerdrWorkspace>();
        foreach (var workspace in workspaces.EnumerateArray())
        {
            if (HerdrAgent.GetString(workspace, "workspace_id") is { } id)
            {
                list.Add(new HerdrWorkspace(id, HerdrAgent.GetString(workspace, "label")));
            }
        }

        return list;
    }

    public async Task<HerdrAgent> GetAgentAsync(string target, CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["agent", "get", target], defaultTimeout, cancellationToken).ConfigureAwait(false);
        return HerdrAgent.Parse(Require(result, "agent"));
    }

    /// <summary>目标不存在（或那个 pane 里还没识别出 agent）时返回 null。</summary>
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

    /// <summary>读 agent 终端输出（纯文本）。<paramref name="source"/>：visible / recent / recent-unwrapped。</summary>
    public Task<string> ReadAsync(string target, string source, int lines, CancellationToken cancellationToken) =>
        InvokeTextAsync(
            ["agent", "read", target, "--source", source, "--lines", lines.ToString(CultureInfo.InvariantCulture)],
            defaultTimeout,
            cancellationToken);

    /// <summary>
    /// 把文字交给 agent。<paramref name="wait"/> 不为空时一直等到 agent 停下（idle / done / blocked），
    /// 返回停下时的 agent 状态。
    /// </summary>
    public async Task<HerdrAgent?> PromptAsync(string target, string text, TimeSpan? wait, CancellationToken cancellationToken)
    {
        List<string> args = ["agent", "prompt", target, ProtectText(text)];
        var timeout = defaultTimeout;
        if (wait is { } waitFor)
        {
            args.AddRange(["--wait", "--timeout", ToMilliseconds(waitFor)]);
            timeout = waitFor + TimeSpan.FromSeconds(30);
        }

        var result = await InvokeJsonAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        return result.TryGetProperty("agent", out var agent) ? HerdrAgent.Parse(agent) : null;
    }

    /// <summary>等 agent 进入 <paramref name="until"/> 中的任一状态。</summary>
    public async Task<HerdrAgent?> WaitAsync(string target, IReadOnlyList<string> until, TimeSpan timeout, CancellationToken cancellationToken)
    {
        List<string> args = ["agent", "wait", target];
        foreach (var status in until)
        {
            args.AddRange(["--until", status]);
        }

        args.AddRange(["--timeout", ToMilliseconds(timeout)]);
        var result = await InvokeJsonAsync(args, timeout + TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return result.TryGetProperty("agent", out var agent) ? HerdrAgent.Parse(agent) : null;
    }

    /// <summary>新建工作区（不抢焦点），返回根 pane 的 ID。</summary>
    public async Task<(string WorkspaceId, string PaneId)> CreateWorkspaceAsync(string cwd, string label, CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(
            ["workspace", "create", "--cwd", cwd, "--label", label, "--no-focus"],
            defaultTimeout,
            cancellationToken).ConfigureAwait(false);
        var workspaceId = HerdrAgent.GetString(Require(result, "workspace"), "workspace_id");
        var paneId = HerdrAgent.GetString(Require(result, "root_pane"), "pane_id");
        return workspaceId is not null && paneId is not null
            ? (workspaceId, paneId)
            : throw new HerdrException("bad_response", "herdr 新建工作区后没有返回 pane ID");
    }

    public Task CloseWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["workspace", "close", workspaceId], defaultTimeout, cancellationToken);

    /// <summary>用 herdr 内置方式启动 agent；成功时 agent 已经可以接收指令。</summary>
    public Task StartAgentAsync(string name, string kind, string paneId, TimeSpan timeout, CancellationToken cancellationToken) =>
        InvokeCommandAsync(
            ["agent", "start", name, "--kind", kind, "--pane", paneId, "--timeout", ToMilliseconds(timeout)],
            timeout + TimeSpan.FromSeconds(15),
            cancellationToken);

    /// <summary>在 pane 里运行一条命令（命令来自本机配置）。herdr 成功时不输出任何内容。</summary>
    public Task RunInPaneAsync(string paneId, string command, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["pane", "run", paneId, command], defaultTimeout, cancellationToken);

    public Task SendKeysAsync(string target, IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["agent", "send-keys", target, .. keys], defaultTimeout, cancellationToken);

    public Task RenameAsync(string target, string name, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["agent", "rename", target, name], defaultTimeout, cancellationToken);

    public static bool IsNotFound(string code) => code.EndsWith("not_found", StringComparison.Ordinal);

    /// <summary>herdr 不支持用 <c>--</c> 结束选项，以 <c>-</c> 开头的文字加前导空格，避免被当成选项。</summary>
    internal static string ProtectText(string text) => text.StartsWith('-') ? " " + text : text;

    private static JsonElement Require(JsonElement result, string property) =>
        result.TryGetProperty(property, out var value)
            ? value
            : throw new HerdrException("bad_response", $"herdr 的返回里没有 {property}");

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

    private async Task<JsonElement> InvokeJsonAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                throw ToException(error);
            }

            return document.RootElement.TryGetProperty("result", out var payload)
                ? payload.Clone()
                : throw new HerdrException("bad_response", "herdr 的返回里没有 result");
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

    /// <summary>运行 herdr；退出码不为 0 时解析 stderr 里的 JSON 错误并抛出。</summary>
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
            return document.RootElement.TryGetProperty("error", out var error) ? ToException(error) : null;
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
