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
/// <param name="runner">启动 herdr 进程的方式（测试里换成假的）。</param>
/// <param name="herdrPath">herdr 可执行文件。</param>
/// <param name="session">herdr 命名会话；为空时用默认会话。</param>
/// <param name="defaultTimeout">单次调用的超时（<see cref="WaitAsync"/> 和 <see cref="StartAgentAsync"/> 另算）。</param>
public sealed class HerdrClient(IProcessRunner runner, string herdrPath, string? session, TimeSpan defaultTimeout)
{
    /// <summary>列出所有 agent。结构不完整的条目会被跳过。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>所有能解析的 agent；返回里没有 <c>agents</c> 数组时为空列表。</returns>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误。</exception>
    public async Task<IReadOnlyList<HerdrAgent>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["agent", "list"], defaultTimeout, cancellationToken).ConfigureAwait(false);
        return ArrayProperty(result, "agents")
            .Select(HerdrAgent.TryParse)
            .OfType<HerdrAgent>()
            .ToList();
    }

    /// <summary>列出所有工作区（用来给 agent 配上工作区名）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>有 <c>workspace_id</c> 的工作区；返回里没有 <c>workspaces</c> 数组时为空列表。</returns>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误。</exception>
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
    /// <param name="target">pane ID 或 agent 名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到的 agent。</returns>
    /// <exception cref="HerdrException">找不到（code 以 not_found 结尾）或 herdr 出错。</exception>
    public async Task<HerdrAgent> GetAgentAsync(string target, CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["agent", "get", target], defaultTimeout, cancellationToken).ConfigureAwait(false);
        return HerdrAgent.TryParse(RequireObject(result, "agent"))
            ?? throw new HerdrException("bad_response", "herdr 返回的 agent 不完整");
    }

    /// <summary>同 <see cref="GetAgentAsync"/>，但目标不存在（或那个 pane 里还没识别出 agent）时返回 null。</summary>
    /// <param name="target">pane ID 或 agent 名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到的 agent；herdr 返回 <c>*not_found</c> 错误时为 null。</returns>
    /// <exception cref="HerdrException">除"找不到"以外的 herdr 错误。</exception>
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
    /// <returns>herdr 原样输出的终端文字。</returns>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误（例如找不到目标）。</exception>
    public Task<string> ReadAsync(string target, string source, int lines, CancellationToken cancellationToken) =>
        InvokeTextAsync(
            ["agent", "read", target, "--source", source, "--lines", lines.ToString(CultureInfo.InvariantCulture)],
            defaultTimeout,
            cancellationToken);

    /// <summary>
    /// 把文字交给 agent，提交完就返回（不等它做完）。
    /// 不用 herdr 的 <c>--wait</c>：那样文字会作为进程参数挂好几个小时，同机其他用户用 <c>ps</c> 就能看到。
    /// </summary>
    /// <param name="target">pane ID 或 agent 名字。</param>
    /// <param name="text">要交给 agent 的文字；以 <c>-</c> 开头时会加一个前导空格，见 <see cref="ProtectText"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>herdr 返回的提交后的 agent；返回里没有 agent 或不完整时为 null。</returns>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误（例如找不到目标）。</exception>
    public async Task<HerdrAgent?> PromptAsync(string target, string text, CancellationToken cancellationToken)
    {
        var result = await InvokeJsonAsync(["agent", "prompt", target, ProtectText(text)], defaultTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ObjectProperty(result, "agent") is { } agent ? HerdrAgent.TryParse(agent) : null;
    }

    /// <summary>等 agent 进入 <paramref name="until"/> 中的任一状态；已经处在这些状态时立即返回。</summary>
    /// <param name="target">pane ID 或 agent 名字。</param>
    /// <param name="until">要等的状态，取值见 <see cref="AgentStatus"/>。</param>
    /// <param name="timeout">交给 herdr 的等待时间；进程本身的超时再多给 30 秒。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进入目标状态时的 agent；返回里没有 agent 或不完整时为 null。</returns>
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
    /// <param name="cwd">工作目录（调用方已经按目录白名单检查过）。</param>
    /// <param name="label">工作区名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新工作区的 ID 和它的根 pane ID。</returns>
    /// <exception cref="HerdrException">herdr 出错，或返回里缺工作区 ID / pane ID（code 为 <c>bad_response</c>）。</exception>
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
    /// <param name="workspaceId">工作区 ID，例如 <c>w1</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误。</exception>
    public Task CloseWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["workspace", "close", workspaceId], defaultTimeout, cancellationToken);

    /// <summary>关闭一个 pane（里面的 agent 进程随之结束）。</summary>
    /// <param name="paneId">pane ID，例如 <c>w1:p2</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误（例如 pane 不存在）。</exception>
    public Task ClosePaneAsync(string paneId, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["pane", "close", paneId], defaultTimeout, cancellationToken);

    /// <summary>用 herdr 内置方式启动 agent；成功时 agent 已经可以接收指令。</summary>
    /// <param name="name">agent 名字。</param>
    /// <param name="kind">herdr 内置的 agent 类型，见 <see cref="Shared.Validation.BuiltInAgentKinds"/>。</param>
    /// <param name="paneId">在哪个 pane 里启动（通常是新工作区的根 pane）。</param>
    /// <param name="timeout">交给 herdr 的就绪等待时间；进程本身的超时再多给 5 秒。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HerdrException">启动时卡在确认框时 code 为 <c>agent_not_ready</c>（agent 已经在了）。</exception>
    public Task StartAgentAsync(string name, string kind, string paneId, TimeSpan timeout, CancellationToken cancellationToken) =>
        InvokeCommandAsync(
            ["agent", "start", name, "--kind", kind, "--pane", paneId, "--timeout", ToMilliseconds(timeout)],
            timeout + TimeSpan.FromSeconds(5),
            cancellationToken);

    /// <summary>在 pane 里运行一条命令（命令来自本机配置，不接受远程输入）。herdr 成功时不输出任何内容。</summary>
    /// <param name="paneId">pane ID。</param>
    /// <param name="command">要运行的命令，例如 <see cref="Config.AgentLauncher.Command"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误。</exception>
    public Task RunInPaneAsync(string paneId, string command, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["pane", "run", paneId, command], defaultTimeout, cancellationToken);

    /// <summary>给 agent 发送按键（调用方负责校验白名单）。</summary>
    /// <param name="target">pane ID 或 agent 名字。</param>
    /// <param name="keys">按键，按顺序发送，见 <see cref="Shared.Validation.AllowedKeys"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误（例如找不到目标）。</exception>
    public Task SendKeysAsync(string target, IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["agent", "send-keys", target, .. keys], defaultTimeout, cancellationToken);

    /// <summary>给 agent 改名（调用方负责校验名字格式）。</summary>
    /// <param name="target">pane ID 或 agent 名字。</param>
    /// <param name="name">新名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HerdrException">herdr 不可用、超时或返回了错误（例如找不到目标）。</exception>
    public Task RenameAsync(string target, string name, CancellationToken cancellationToken) =>
        InvokeCommandAsync(["agent", "rename", target, name], defaultTimeout, cancellationToken);

    /// <summary>herdr 的"找不到"类错误码都以 <c>not_found</c> 结尾（agent_not_found、pane_not_found…）。</summary>
    /// <param name="code">herdr 的错误码，见 <see cref="HerdrException.Code"/>。</param>
    /// <returns>以 <c>not_found</c> 结尾时为 true。</returns>
    public static bool IsNotFound(string code) => code.EndsWith("not_found", StringComparison.Ordinal);

    /// <summary>以 <c>-</c> 开头的文字加一个前导空格，避免被 herdr 当成选项。</summary>
    /// <param name="text">要交给 agent 的文字。</param>
    /// <returns>不以 <c>-</c> 开头时原样返回。</returns>
    internal static string ProtectText(string text) => text.StartsWith('-') ? " " + text : text;

    /// <summary>安全地取对象类型的字段。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="property">字段名。</param>
    /// <returns>字段的值；父元素不是对象、没有这个字段或字段不是对象时为 null。</returns>
    private static JsonElement? ObjectProperty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>同 <see cref="ObjectProperty"/>，但字段必须存在。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="property">字段名。</param>
    /// <returns>字段的值。</returns>
    /// <exception cref="HerdrException">没有这个对象字段，code 为 <c>bad_response</c>。</exception>
    private static JsonElement RequireObject(JsonElement element, string property) =>
        ObjectProperty(element, property) ?? throw new HerdrException("bad_response", $"herdr 的返回里没有 {property}");

    /// <summary>安全地取数组类型字段的各项。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="property">字段名。</param>
    /// <returns>数组的各项；父元素不是对象、没有这个字段或字段不是数组时为空。</returns>
    private static IEnumerable<JsonElement> ArrayProperty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    /// <summary>herdr 的 <c>--timeout</c> 参数以毫秒为单位。</summary>
    /// <param name="value">时长。</param>
    /// <returns>整数毫秒数的文本（小数部分舍去）。</returns>
    private static string ToMilliseconds(TimeSpan value) =>
        ((long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>配置了命名会话时在最前面加上 <c>--session 名字</c>。</summary>
    /// <param name="args">herdr 子命令和参数。</param>
    /// <returns>完整的参数列表。</returns>
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
    /// <param name="args">herdr 子命令和参数。</param>
    /// <param name="timeout">进程超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns><c>result</c> 对象的副本（不依赖已释放的 <see cref="JsonDocument"/>）。</returns>
    /// <exception cref="HerdrException">herdr 返回了 <c>error</c>，或输出不是 JSON、没有 <c>result</c>（code 为 <c>bad_response</c>）。</exception>
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
    /// <param name="args">herdr 子命令和参数。</param>
    /// <param name="timeout">进程超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HerdrException">退出码不为 0，或输出里有 <c>error</c>。</exception>
    private async Task InvokeCommandAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        if (TryParseError(result.StandardOutput) is { } error)
        {
            throw error;
        }
    }

    /// <summary>输出是纯文本的命令（例如 <c>agent read</c>）：退出码为 0 就原样返回标准输出。</summary>
    /// <param name="args">herdr 子命令和参数。</param>
    /// <param name="timeout">进程超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>herdr 的标准输出。</returns>
    /// <exception cref="HerdrException">herdr 不可用、超时或退出码不为 0。</exception>
    private async Task<string> InvokeTextAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    /// <summary>运行 herdr；退出码不为 0 时解析 stderr（或 stdout）里的 JSON 错误并抛出。</summary>
    /// <param name="args">herdr 子命令和参数（会话参数由 <see cref="BuildArguments"/> 补上）。</param>
    /// <param name="timeout">进程超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退出码为 0 的运行结果。</returns>
    /// <exception cref="HerdrException">
    /// 启动不了（<c>herdr_unavailable</c>）、超时（<c>timeout</c>）、输出太长没读完（<c>output_too_large</c>）、
    /// herdr 返回的 JSON 错误（它自己的错误码），或退出码不为 0 又没有 JSON 错误（<c>herdr_error</c>）。
    /// </exception>
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

        if (result.Truncated)
        {
            // 只读到前面一部分：JSON 不完整，终端内容也缺了结尾，不能当成正常结果。
            throw new HerdrException("output_too_large", "herdr 的输出太长，没有读完，已放弃");
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

    /// <summary>从 herdr 的输出里找 <c>{"error": {...}}</c>。</summary>
    /// <param name="output">标准输出或标准错误。</param>
    /// <returns>对应的异常（只构造、不抛出）；输出为空、不是 JSON 或没有 <c>error</c> 时为 null。</returns>
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

    /// <summary>把 herdr 的 <c>error</c> 对象转成异常；缺字段时用通用的错误码和说明。</summary>
    /// <param name="error"><c>error</c> 字段的值。</param>
    /// <returns>code 默认为 <c>herdr_error</c> 的异常。</returns>
    private static HerdrException ToException(JsonElement error) =>
        new(HerdrAgent.GetString(error, "code") ?? "herdr_error", HerdrAgent.GetString(error, "message") ?? "herdr 返回了错误");

    /// <summary>把 herdr 的输出截短后放进错误说明，避免一大段输出刷屏。</summary>
    /// <param name="text">原始输出。</param>
    /// <returns>去掉首尾空白的文字；超过 300 个字符时只留前 300 个并加 <c>…</c>。</returns>
    private static string Shorten(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";
}
