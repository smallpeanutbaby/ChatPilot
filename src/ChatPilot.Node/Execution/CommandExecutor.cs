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

    public async Task<CommandOutcome> ExecuteAsync(NodeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            return command switch
            {
                ListAgentsCommand => await ListAsync(cancellationToken).ConfigureAwait(false),
                PromptCommand prompt => await PromptAsync(prompt, cancellationToken).ConfigureAwait(false),
                ReadCommand read => await ReadAsync(read, cancellationToken).ConfigureAwait(false),
                StartAgentCommand start => await StartAgentAsync(start, cancellationToken).ConfigureAwait(false),
                SendKeysCommand keys => await SendKeysAsync(keys, cancellationToken).ConfigureAwait(false),
                RespondCommand respond => await RespondAsync(respond, cancellationToken).ConfigureAwait(false),
                RenameAgentCommand rename => await RenameAsync(rename, cancellationToken).ConfigureAwait(false),
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
        if (agent.Status == AgentStatus.Blocked)
        {
            var screen = await replies.GetScreenExcerptAsync(agent.PaneId, cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is HerdrException or TimeoutException)
        {
            await CloseQuietlyAsync(workspaceId).ConfigureAwait(false);
            return ex is HerdrException herdrError
                ? MapHerdrError(herdrError)
                : CommandOutcome.Failure(ErrorCodes.Timeout, ex.Message);
        }

        string? screen = null;
        if (agent.Status == AgentStatus.Blocked)
        {
            screen = await replies.GetScreenExcerptAsync(agent.PaneId, cancellationToken).ConfigureAwait(false);
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
        return CommandOutcome.Success(new AckData("已发送按键：" + string.Join(" ", command.Keys)));
    }

    private async Task<CommandOutcome> RespondAsync(RespondCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        if (!command.Approve)
        {
            await herdr.SendKeysAsync(command.Target, ["esc"], cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new AckData("已按 Esc（拒绝 / 取消）"));
        }

        var screen = await herdr.ReadAsync(command.Target, "visible", 80, cancellationToken).ConfigureAwait(false);
        if (!ApprovalNavigator.TryPlanApprove(screen, out var plan, out var error) || plan is null)
        {
            return CommandOutcome.Failure(ErrorCodes.NotFound, error + "\n\n" + ReplyExtractor.ScreenExcerpt(screen, 15));
        }

        await herdr.SendKeysAsync(command.Target, plan.Keys, cancellationToken).ConfigureAwait(false);
        tracker.WatchAfterResponse(command.Target);
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
