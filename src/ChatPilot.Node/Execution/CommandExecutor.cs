using ChatPilot.Node.Config;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>
/// 执行服务器发来的指令。这是节点的安全边界：每个参数都要先校验，
/// 只能做协议里定义的几种 herdr 操作，不能执行任意命令。
/// </summary>
public sealed class CommandExecutor(
    HerdrClient herdr,
    PathPolicy paths,
    PromptTracker tracker,
    ReplyService replies,
    NodeConfig config,
    ILogger<CommandExecutor> logger)
{
    private const int MaxReadChars = 12000;
    private const int ReadOverscanLines = 80;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>普通指令的总时间预算：服务器最多等 30 秒，节点要在那之前给出结果。</summary>
    internal static readonly TimeSpan CommandBudget = TimeSpan.FromSeconds(28);

    /// <summary>启动 agent 的总时间预算（服务器最多等 120 秒）。</summary>
    private TimeSpan StartBudget => TimeSpan.FromSeconds(config.AgentStartTimeoutSeconds + 20);

    /// <summary>执行一条指令。不会抛出异常（节点退出时的取消除外），所有失败都转成失败结果。</summary>
    public async Task<CommandOutcome> ExecuteAsync(NodeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var budget = command is StartAgentCommand ? StartBudget : CommandBudget;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        var token = deadline.Token;
        try
        {
            return command switch
            {
                ListAgentsCommand => await ListAsync(token).ConfigureAwait(false),
                PromptCommand prompt => await PromptAsync(prompt, token).ConfigureAwait(false),
                ReadCommand read => await ReadAsync(read, token).ConfigureAwait(false),
                StartAgentCommand start => await StartAgentAsync(start, token).ConfigureAwait(false),
                SendKeysCommand keys => await SendKeysAsync(keys, token).ConfigureAwait(false),
                RespondCommand respond => await RespondAsync(respond, token).ConfigureAwait(false),
                RenameAgentCommand rename => await RenameAsync(rename, token).ConfigureAwait(false),
                _ => CommandOutcome.Failure(ErrorCodes.InvalidArgument, "不支持的指令"),
            };
        }
        catch (HerdrException ex)
        {
            logger.LogInformation("herdr 返回错误：{Code} {Message}", ex.Code, ex.Message);
            return MapHerdrError(ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return CommandOutcome.Failure(ErrorCodes.Timeout, $"节点在 {budget.TotalSeconds:0} 秒内没有完成，已放弃");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "执行 {Command} 时出错", command.GetType().Name);
            return CommandOutcome.Failure(ErrorCodes.Internal, "节点内部错误，详情见节点日志");
        }
    }

    /// <summary>把 herdr 的错误码转成协议错误码。</summary>
    public static CommandOutcome MapHerdrError(HerdrException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var code = ex.Code switch
        {
            _ when HerdrClient.IsNotFound(ex.Code) => ErrorCodes.NotFound,
            "agent_blocked" => ErrorCodes.AgentBlocked,
            "timeout" => ErrorCodes.Timeout,
            "herdr_unavailable" => ErrorCodes.HerdrUnavailable,
            _ => ErrorCodes.HerdrError,
        };
        return CommandOutcome.Failure(code, ex.Message);
    }

    private async Task<CommandOutcome> ListAsync(CancellationToken cancellationToken)
    {
        var agents = await herdr.ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        var labels = await WorkspaceLabelsAsync(cancellationToken).ConfigureAwait(false);
        return CommandOutcome.Success(new AgentListData(agents.Select(a => a.ToSummary(LabelOf(labels, a))).ToList()));
    }

    private async Task<CommandOutcome> PromptAsync(PromptCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        if (string.IsNullOrWhiteSpace(command.Text))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "内容不能为空");
        }

        if (command.Text.Length > ProtocolInfo.MaxPromptChars)
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, $"内容太长（上限 {ProtocolInfo.MaxPromptChars} 字）");
        }

        var agent = await herdr.GetAgentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (agent.Kind is null)
        {
            return CommandOutcome.Failure(ErrorCodes.NotFound, "这个 pane 里没有识别出 agent");
        }

        if (agent.Status == AgentStatus.Blocked)
        {
            var screen = await ScreenExcerptOrNoteAsync(agent.PaneId, cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Failure(ErrorCodes.AgentBlocked, "它正在等你确认，先回复 /yes 或 /no：\n\n" + screen);
        }

        return await tracker.SubmitAsync(agent, command.Text, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandOutcome> ReadAsync(ReadCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        if (command.Lines is < ProtocolInfo.MinReadLines or > ProtocolInfo.MaxReadLines)
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, $"行数必须在 {ProtocolInfo.MinReadLines}–{ProtocolInfo.MaxReadLines} 之间");
        }

        // 多取一些：底部的输入框和空行会被去掉。
        var output = await herdr.ReadAsync(command.Target, "recent-unwrapped", command.Lines + ReadOverscanLines, cancellationToken).ConfigureAwait(false);
        var text = ReplyExtractor.RecentContent(output, command.Lines);
        if (text.Length > MaxReadChars)
        {
            text = "…" + text[^(MaxReadChars - 1)..];
        }

        return CommandOutcome.Success(new TextData(text.Length > 0 ? text : "（没有输出）"));
    }

    private async Task<CommandOutcome> StartAgentAsync(StartAgentCommand command, CancellationToken cancellationToken)
    {
        var kind = string.IsNullOrWhiteSpace(command.Kind) ? config.DefaultAgentKind : command.Kind.Trim();
        if (!Validation.IsAgentKindSyntax(kind) || !config.IsKindAvailable(kind))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, $"不支持的类型 {kind}。可用：{string.Join("、", config.AvailableKinds())}");
        }

        if (command.Name is not null && !Validation.IsAgentName(command.Name))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "名字只能用小写字母开头，包含小写字母、数字、下划线、短横线，最长 32 位");
        }

        if (command.InitialPrompt is { Length: > ProtocolInfo.MaxPromptChars })
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "初始指令太长");
        }

        var directory = string.IsNullOrWhiteSpace(command.Directory) ? paths.DefaultDirectory : command.Directory;
        if (!paths.TryResolve(directory, out var resolved, out var pathError))
        {
            return CommandOutcome.Failure(ErrorCodes.Forbidden, pathError);
        }

        var existing = (await herdr.ListAgentsAsync(cancellationToken).ConfigureAwait(false))
            .Select(a => a.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (command.Name is not null && existing.Contains(command.Name))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, $"名字 {command.Name} 已经被占用了");
        }

        var name = command.Name ?? AgentNaming.FromDirectory(resolved, existing);
        var (workspaceId, paneId) = await herdr.CreateWorkspaceAsync(resolved, AgentNaming.WorkspaceLabel(resolved), cancellationToken)
            .ConfigureAwait(false);

        HerdrAgent agent;
        try
        {
            agent = config.Launchers.TryGetValue(kind, out var launcher)
                ? await LaunchWithCommandAsync(launcher, paneId, name, cancellationToken).ConfigureAwait(false)
                : await LaunchBuiltInAsync(kind, paneId, name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 任何失败（包括超时、连接断开导致的取消）都不能留下半启动的工作区。
            await CloseQuietlyAsync(workspaceId).ConfigureAwait(false);
            if (ex is HerdrException herdrError)
            {
                return MapHerdrError(herdrError);
            }

            if (ex is TimeoutException)
            {
                return CommandOutcome.Failure(ErrorCodes.Timeout, ex.Message);
            }

            throw;
        }

        string? screen = null;
        if (agent.Status == AgentStatus.Blocked)
        {
            screen = await ScreenExcerptOrNoteAsync(agent.PaneId, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(command.InitialPrompt))
        {
            tracker.QueueInitialPrompt(agent, command.InitialPrompt);
        }

        var label = AgentNaming.WorkspaceLabel(resolved);
        return CommandOutcome.Success(new AgentStartedData(agent.ToSummary(label), agent.IsReady, screen));
    }

    /// <summary>herdr 内置启动。新终端的 shell 可能还没就绪，失败时重试几次。</summary>
    private async Task<HerdrAgent> LaunchBuiltInAsync(string kind, string paneId, string name, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(config.AgentStartTimeoutSeconds);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await herdr.StartAgentAsync(name, kind, paneId, timeout, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (HerdrException ex) when (ex.Code == "agent_not_ready")
            {
                // 启动时卡在确认框（比如"信任此目录"），agent 已经在了，交给用户处理。
                break;
            }
            catch (HerdrException ex) when (attempt < 4 && ex.Code is not ("timeout" or "herdr_unavailable"))
            {
                logger.LogDebug("启动 agent 第 {Attempt} 次失败（{Code}），稍后重试", attempt, ex.Code);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        return await herdr.GetAgentAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>自定义启动（例如 reclaude）：在终端里运行配置的命令，等 herdr 识别出 agent 后再起名。</summary>
    private async Task<HerdrAgent> LaunchWithCommandAsync(AgentLauncher launcher, string paneId, string name, CancellationToken cancellationToken)
    {
        await herdr.RunInPaneAsync(paneId, launcher.Command, cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.AgentStartTimeoutSeconds);
        HerdrAgent? detected = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            detected = await herdr.TryGetAgentAsync(paneId, cancellationToken).ConfigureAwait(false);
            if (detected is not null && detected.Kind == launcher.DetectAs && detected.Status != AgentStatus.Unknown)
            {
                break;
            }

            detected = null;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (detected is null)
        {
            throw new TimeoutException($"运行 {launcher.Command} 后 {config.AgentStartTimeoutSeconds} 秒内没有识别出 {launcher.DetectAs}，请检查这个命令能否正常启动");
        }

        await herdr.RenameAsync(paneId, name, cancellationToken).ConfigureAwait(false);
        return await herdr.GetAgentAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandOutcome> SendKeysAsync(SendKeysCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        if (command.Keys is null || command.Keys.Count is 0 or > ProtocolInfo.MaxKeysPerCommand)
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, $"一次发送 1–{ProtocolInfo.MaxKeysPerCommand} 个按键");
        }

        var rejected = command.Keys.Where(k => !Validation.IsAllowedKey(k)).ToList();
        if (rejected.Count > 0)
        {
            return CommandOutcome.Failure(
                ErrorCodes.InvalidArgument,
                $"不允许的按键：{string.Join(" ", rejected.Select(k => TextChunker.Truncate(k, 20)))}。允许：{string.Join(" ", Validation.AllowedKeys.Order(StringComparer.Ordinal))}");
        }

        await herdr.SendKeysAsync(command.Target, command.Keys, cancellationToken).ConfigureAwait(false);
        if (command.Keys.Any(IsConfirmKey) && await herdr.TryGetAgentAsync(command.Target, cancellationToken).ConfigureAwait(false) is { } agent)
        {
            // 用 /keys 手动处理了确认框，同样要跟踪到做完再通知。
            tracker.WatchAfterResponse(agent);
        }

        return CommandOutcome.Success(new AckData("已发送按键：" + string.Join(" ", command.Keys)));
    }

    private async Task<CommandOutcome> RespondAsync(RespondCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        // 只在 agent 确实停在确认框时按键：迟到的 /yes 可能把回车按进输入框，迟到的 /no 会用 Esc 打断正在干的活。
        var agent = await herdr.GetAgentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (agent.Status != AgentStatus.Blocked)
        {
            return CommandOutcome.Failure(
                ErrorCodes.InvalidArgument,
                $"它现在没在等确认（{agent.Status}），没有按任何键。确认框还在屏幕上的话，请用 /keys 手动操作");
        }

        if (!command.Approve)
        {
            await herdr.SendKeysAsync(agent.PaneId, ["esc"], cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new AckData("已按 Esc（拒绝 / 取消）"));
        }

        var screen = await herdr.ReadAsync(agent.PaneId, "visible", 80, cancellationToken).ConfigureAwait(false);
        if (!ApprovalNavigator.TryPlanApprove(screen, out var plan, out var error) || plan is null)
        {
            return CommandOutcome.Failure(ErrorCodes.NotFound, error + "\n\n" + ReplyExtractor.ScreenExcerpt(screen, 15));
        }

        await herdr.SendKeysAsync(agent.PaneId, plan.Keys, cancellationToken).ConfigureAwait(false);
        tracker.WatchAfterResponse(agent);
        return CommandOutcome.Success(new AckData($"已选择：{plan.OptionText}"));
    }

    private async Task<CommandOutcome> RenameAsync(RenameAgentCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        if (!Validation.IsAgentName(command.Name))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "名字只能用小写字母开头，包含小写字母、数字、下划线、短横线，最长 32 位");
        }

        await herdr.RenameAsync(command.Target, command.Name, cancellationToken).ConfigureAwait(false);
        return CommandOutcome.Success(new AckData($"已改名为 {command.Name}"));
    }

    /// <summary>可能是在确认框里做选择的按键。</summary>
    private static bool IsConfirmKey(string key) => key is "enter" or "y" || (key.Length == 1 && char.IsAsciiDigit(key[0]));

    /// <summary>屏幕摘要；读取失败时返回提示文字，不影响主流程。</summary>
    private async Task<string> ScreenExcerptOrNoteAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            return await replies.GetScreenExcerptAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (HerdrException ex)
        {
            logger.LogWarning("读取 {Target} 的屏幕失败：{Message}", target, ex.Message);
            return "（读取屏幕失败，请用 /read 查看）";
        }
    }

    private static CommandOutcome? InvalidTarget(string? target) =>
        Validation.IsAgentTarget(target)
            ? null
            : CommandOutcome.Failure(ErrorCodes.InvalidArgument, "目标必须是 agent 名字或 pane ID");

    private async Task<Dictionary<string, string?>> WorkspaceLabelsAsync(CancellationToken cancellationToken) =>
        (await herdr.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(w => w.WorkspaceId, w => w.Label, StringComparer.Ordinal);

    private static string? LabelOf(Dictionary<string, string?> labels, HerdrAgent agent) =>
        agent.WorkspaceId is not null && labels.TryGetValue(agent.WorkspaceId, out var label) ? label : null;

    private async Task CloseQuietlyAsync(string workspaceId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await herdr.CloseWorkspaceAsync(workspaceId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HerdrException or OperationCanceledException)
        {
            logger.LogWarning("启动失败后关闭工作区 {Workspace} 也失败了：{Message}", workspaceId, ex.Message);
        }
    }
}
