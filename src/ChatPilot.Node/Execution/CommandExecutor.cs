using ChatPilot.Node.Config;
using ChatPilot.Node.Connection;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>
/// 执行服务器发来的指令。这是节点的安全边界：每个参数都要先校验，
/// 只能做协议里定义的几种 herdr 操作（外加只读的 git 查询和局域网唤醒包），不能执行任意命令。
/// </summary>
/// <param name="herdr">herdr 客户端。</param>
/// <param name="paths">/new 的目录白名单。</param>
/// <param name="tracker">任务跟踪和排队。</param>
/// <param name="replies">取回复和屏幕摘要。</param>
/// <param name="config">节点配置。</param>
/// <param name="logger">日志。</param>
/// <param name="watcher">盯梢；为 null 时不支持 /watch。</param>
/// <param name="git">git 查询；为 null 时不支持 /diff。</param>
/// <param name="attachments">图片下载；为 null 时不支持带图片的指令。</param>
/// <param name="wakeSender">广播唤醒包，返回发出的包数；为 null 时用 <see cref="LocalNetwork.SendWakeAsync"/>（测试时替换掉，不真的发包）。</param>
public sealed class CommandExecutor(
    HerdrClient herdr,
    PathPolicy paths,
    PromptTracker tracker,
    ReplyService replies,
    NodeConfig config,
    ILogger<CommandExecutor> logger,
    AgentWatcher? watcher = null,
    GitInspector? git = null,
    AttachmentDownloader? attachments = null,
    Func<IReadOnlyList<string>, CancellationToken, Task<int>>? wakeSender = null)
{
    /// <summary><c>/read</c> 返回的文字上限，超出时保留结尾。</summary>
    private const int MaxReadChars = 12000;

    /// <summary><c>/read</c> 比要求的多读这么多行：底部的输入框和空行会被去掉。</summary>
    private const int ReadOverscanLines = 80;

    /// <summary>自定义启动后轮询 herdr 有没有识别出 agent 的间隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>普通指令的总时间预算：服务器最多等 30 秒，节点要在那之前给出结果。</summary>
    internal static readonly TimeSpan CommandBudget = TimeSpan.FromSeconds(28);

    /// <summary>启动 agent 的总时间预算（服务器最多等 120 秒）。</summary>
    private TimeSpan StartBudget => TimeSpan.FromSeconds(config.AgentStartTimeoutSeconds + 20);

    /// <summary>执行一条指令。不会抛出异常（节点退出时的取消除外），所有失败都转成失败结果。</summary>
    /// <param name="command">服务器发来的指令。</param>
    /// <param name="cancellationToken">节点退出或连接断开时取消；另外按指令类型限时（<see cref="CommandBudget"/> 或启动预算）。</param>
    /// <returns>执行结果；超时、herdr 出错、参数不合法或内部错误都是失败结果。</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 被取消。</exception>
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
                PromptCommand prompt => await PromptAsync(prompt.Target, prompt.Text, null, token).ConfigureAwait(false),
                MediaPromptCommand prompt => await PromptAsync(prompt.Target, prompt.Text, prompt.Attachments ?? [], token).ConfigureAwait(false),
                ReadCommand read => await ReadAsync(read, token).ConfigureAwait(false),
                StartAgentCommand start => await StartAgentAsync(start, token).ConfigureAwait(false),
                SendKeysCommand keys => await SendKeysAsync(keys, token).ConfigureAwait(false),
                RespondCommand respond => await RespondAsync(respond, token).ConfigureAwait(false),
                RenameAgentCommand rename => await RenameAsync(rename, token).ConfigureAwait(false),
                LastReplyCommand last => await LastReplyAsync(last, token).ConfigureAwait(false),
                DiffCommand diff => await DiffAsync(diff, token).ConfigureAwait(false),
                StopAgentCommand stop => await StopAsync(stop, token).ConfigureAwait(false),
                CloseAgentCommand close => await CloseAsync(close, token).ConfigureAwait(false),
                WatchCommand watch => await WatchAsync(watch, token).ConfigureAwait(false),
                WakeCommand wake => await WakeAsync(wake, token).ConfigureAwait(false),
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
    /// <param name="ex">herdr 返回的错误。</param>
    /// <returns>带对应错误码和 herdr 错误信息的失败结果；不认识的错误码归为 herdr 错误。</returns>
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

    /// <summary>列出 agent，附上工作区标签、是否盯着、排队条数和当前任务的开始时间。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>agent 列表，以及是不是在盯着所有 agent。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
    private async Task<CommandOutcome> ListAsync(CancellationToken cancellationToken)
    {
        var agents = await herdr.ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        var labels = await WorkspaceLabelsAsync(cancellationToken).ConfigureAwait(false);
        var summaries = agents
            .Select(a => a.ToSummary(LabelOf(labels, a)) with
            {
                Watched = watcher?.IsWatched(a) ?? false,
                Queued = tracker.QueuedCount(a.PaneId),
                TaskSince = tracker.TaskSince(a.PaneId),
            })
            .ToList();
        return CommandOutcome.Success(new AgentListData(summaries, watcher?.WatchAll ?? false));
    }

    /// <summary>派活（可以带图片）。图片先下载保存，路径附在文字后面；指令没交出去（比如排队满了）就把刚保存的图片删掉。</summary>
    /// <param name="target">agent 名字或 pane ID。</param>
    /// <param name="text">要交给 agent 的文字。</param>
    /// <param name="media">附带的图片；不带图片的指令为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已发送或已排队的确认；参数不合法、没有 agent、在等确认（附屏幕摘要）或图片下载失败时为失败结果。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
    private async Task<CommandOutcome> PromptAsync(string target, string text, IReadOnlyList<MediaRef>? media, CancellationToken cancellationToken)
    {
        if (InvalidTarget(target) is { } invalid)
        {
            return invalid;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "内容不能为空");
        }

        if (text.Length > ProtocolInfo.MaxPromptChars)
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, $"内容太长（上限 {ProtocolInfo.MaxPromptChars} 字）");
        }

        if (media is not null && (media.Count is 0 or > ProtocolInfo.MaxAttachments || attachments is null))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, attachments is null
                ? "这个节点不支持图片"
                : $"一次带 1–{ProtocolInfo.MaxAttachments} 张图片");
        }

        var agent = await herdr.GetAgentAsync(target, cancellationToken).ConfigureAwait(false);
        if (agent.Kind is null)
        {
            return CommandOutcome.Failure(ErrorCodes.NotFound, "这个 pane 里没有识别出 agent");
        }

        if (agent.Status == AgentStatus.Blocked)
        {
            var screen = await ShowDialogOrNoteAsync(agent, cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Failure(ErrorCodes.AgentBlocked, "它正在等你确认，先回复 /yes 或 /no：\n\n" + screen);
        }

        IReadOnlyList<string> saved = [];
        if (media is not null && attachments is not null)
        {
            try
            {
                saved = await attachments.SaveAsync(media, agent.Cwd, cancellationToken).ConfigureAwait(false);
                text = AttachmentDownloader.AppendToPrompt(text, saved);
            }
            catch (AttachmentException ex)
            {
                return CommandOutcome.Failure(ErrorCodes.InvalidArgument, ex.Message);
            }
        }

        var outcome = await tracker.SubmitAsync(agent, text, cancellationToken).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            AttachmentDownloader.DeleteQuietly(saved);
        }

        return outcome;
    }

    /// <summary><c>/diff</c>：agent 工作目录里的 git 改动概要，或者一个文件的 diff。只读。</summary>
    /// <param name="command">指令，带目标和可选的文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>改动文字；参数不合法、不支持 /diff、不知道工作目录或 git 出错时为失败结果。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
    private async Task<CommandOutcome> DiffAsync(DiffCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        if (command.Path is not null && !Validation.IsRepoRelativePath(command.Path))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "文件路径要写相对 agent 工作目录的路径，例如 src/app.ts，不能有 ..");
        }

        if (git is null)
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "这个节点不支持 /diff");
        }

        var agent = await herdr.GetAgentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(agent.Cwd))
        {
            return CommandOutcome.Failure(ErrorCodes.NotFound, "不知道它的工作目录");
        }

        try
        {
            var text = command.Path is null
                ? await git.SummaryAsync(agent.Cwd, cancellationToken).ConfigureAwait(false)
                : await git.FileDiffAsync(agent.Cwd, command.Path, cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new TextData(text));
        }
        catch (GitException ex)
        {
            return CommandOutcome.Failure(ErrorCodes.NotFound, ex.Message);
        }
    }

    /// <summary><c>/stop</c>：清空排队的指令；agent 正在干活时按 Esc 打断。在等确认或空闲时不按键。</summary>
    /// <param name="command">指令，带目标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>说明有没有按键、清掉了几条排队指令；目标不合法时为失败结果。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
    private async Task<CommandOutcome> StopAsync(StopAgentCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        var agent = await herdr.GetAgentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        var cleared = tracker.ClearQueue(agent.PaneId);
        var note = cleared > 0 ? $"，清空了 {cleared} 条排队的指令" : "";
        if (agent.Status == AgentStatus.Working)
        {
            await herdr.SendKeysAsync(agent.PaneId, ["esc"], cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new AckData("已按 Esc 打断它" + note));
        }

        return CommandOutcome.Success(new AckData(agent.Status == AgentStatus.Blocked
            ? $"它在等你确认，没有按键（要拒绝用 /no）{note}"
            : $"它现在没在干活，没有按键{note}"));
    }

    /// <summary>
    /// <c>/close</c>：关闭 agent 所在的 pane，丢掉它排队的指令和任务记录。
    /// 先停掉对它的跟踪再关：关掉之后跟踪方会发现 agent 不见了，不能把这当成任务失败通知主人。
    /// </summary>
    /// <param name="command">指令，带目标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已关闭的确认；目标不合法时为失败结果。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
    private async Task<CommandOutcome> CloseAsync(CloseAgentCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        var agent = await herdr.GetAgentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        tracker.Forget(agent.PaneId);
        await herdr.ClosePaneAsync(agent.PaneId, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("按主人的要求关闭了 {Agent}（{Pane}）", agent.Name ?? agent.PaneId, agent.PaneId);
        return CommandOutcome.Success(new AckData($"已关闭（{agent.PaneId}）"));
    }

    /// <summary><c>/watch</c>、<c>/unwatch</c>：开始或停止盯一个 agent；没有目标时是这台电脑上的所有 agent。</summary>
    /// <param name="command">指令，带可选的目标和开始还是停止。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>给用户看的说明；不支持 /watch、目标不合法或没有 agent 时为失败结果。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
    private async Task<CommandOutcome> WatchAsync(WatchCommand command, CancellationToken cancellationToken)
    {
        if (watcher is null)
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "这个节点不支持 /watch");
        }

        if (command.Target is null)
        {
            return CommandOutcome.Success(new AckData(watcher.SetAll(command.Enable)));
        }

        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        var agent = await herdr.GetAgentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        return agent.Kind is null
            ? CommandOutcome.Failure(ErrorCodes.NotFound, "这个 pane 里没有识别出 agent")
            : CommandOutcome.Success(new AckData(watcher.Set(agent, command.Enable)));
    }

    /// <summary><c>/wake</c>：在这台电脑所在的局域网里广播唤醒包。</summary>
    /// <param name="command">指令，带要唤醒的 MAC 地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发出了几个包；MAC 地址不合法或一个包都没发出去时为失败结果。</returns>
    private async Task<CommandOutcome> WakeAsync(WakeCommand command, CancellationToken cancellationToken)
    {
        if (command.MacAddresses is null || command.MacAddresses.Count is 0 or > ProtocolInfo.MaxWakeAddresses
            || !command.MacAddresses.All(Validation.IsMacAddress))
        {
            return CommandOutcome.Failure(ErrorCodes.InvalidArgument, "MAC 地址不合法");
        }

        var sent = await (wakeSender ?? LocalNetwork.SendWakeAsync)(command.MacAddresses, cancellationToken).ConfigureAwait(false);
        return sent > 0
            ? CommandOutcome.Success(new AckData($"已广播唤醒包（{sent} 个）"))
            : CommandOutcome.Failure(ErrorCodes.Internal, "唤醒包没有发出去（没有可用的网卡）");
    }

    /// <summary><c>/read</c>：最近的终端内容，去掉底部输入框、合并空行，超长时保留结尾。</summary>
    /// <param name="command">指令，带目标和行数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>终端内容（没有输出时是一句说明）；目标或行数不合法时为失败结果。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
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

    /// <summary>
    /// <c>/new</c>：在白名单目录里新建工作区并启动 agent；有初始说明时挂起，等它就绪再发。
    /// 启动失败时关掉刚建的工作区，不留下半启动的终端。
    /// </summary>
    /// <param name="command">指令，带类型、名字、目录和初始说明（都可以省略）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动的 agent、是否已就绪，以及卡在确认框时的屏幕摘要；参数不合法、目录不允许、名字重复、启动失败或超时时为失败结果。</returns>
    /// <exception cref="HerdrException">列 agent 或建工作区时 herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
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
            screen = await ShowDialogOrNoteAsync(agent, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(command.InitialPrompt))
        {
            tracker.QueueInitialPrompt(agent, command.InitialPrompt);
        }

        var label = AgentNaming.WorkspaceLabel(resolved);
        return CommandOutcome.Success(new AgentStartedData(agent.ToSummary(label), agent.IsReady, screen));
    }

    /// <summary>herdr 内置启动。新终端的 shell 可能还没就绪，失败时重试几次。</summary>
    /// <param name="kind">agent 类型。</param>
    /// <param name="paneId">新工作区的 pane。</param>
    /// <param name="name">agent 名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动后的 agent（可能卡在确认框）。</returns>
    /// <exception cref="HerdrException">重试几次后仍然启动失败，或者超时、herdr 不可用。</exception>
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
    /// <param name="launcher">配置的启动命令和要识别成的类型。</param>
    /// <param name="paneId">新工作区的 pane。</param>
    /// <param name="name">agent 名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>起好名的 agent。</returns>
    /// <exception cref="TimeoutException">启动超时内 herdr 没有识别出指定类型的 agent。</exception>
    /// <exception cref="HerdrException">herdr 出错。</exception>
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

    /// <summary><c>/keys</c>：按白名单里的键；可能是在确认框里做了选择时，接着跟踪到做完再通知。</summary>
    /// <param name="command">指令，带目标和按键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按了哪些键；目标不合法、按键数不对或有不允许的按键时为失败结果，一个键都不按。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
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

    /// <summary>
    /// <c>/yes</c>、<c>/no</c>：回应确认框。拒绝按 Esc；同意时选不扩大权限的同意选项（见 <see cref="ApprovalNavigator"/>），并且只同意给用户看过的那个确认框。
    /// 按键后接着跟踪到做完再通知。
    /// </summary>
    /// <param name="command">指令，带目标和同意还是拒绝。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按了什么；没在等确认、认不出确认框、没有能安全选中的选项，或者确认框不是给用户看过的那个时为失败结果，一个键都不按。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
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

        // 只同意给用户看过的那个确认框：通知发出后，原来的确认框可能已经在电脑上处理掉、又弹出了新的。
        var check = replies.Dialogs.Consume(agent, screen);
        if (check != DialogCheck.Shown)
        {
            var reason = check == DialogCheck.Changed
                ? "确认框和发给你的不是同一个了（可能已经在电脑上处理过，又弹出了新的）"
                : "这个确认框还没发给你看过";
            return CommandOutcome.Failure(
                ErrorCodes.AgentBlocked,
                $"{reason}，没有按任何键。现在的确认框：\n\n{replies.ShowDialog(agent, screen)}\n\n看过没问题再回复一次 /yes");
        }

        await herdr.SendKeysAsync(agent.PaneId, plan.Keys, cancellationToken).ConfigureAwait(false);
        tracker.WatchAfterResponse(agent);
        return CommandOutcome.Success(new AckData($"已选择：{plan.OptionText}"));
    }

    /// <summary>
    /// 最近一条回复，取法和做完时推送的一样（能读会话记录就读记录）。微信的通知不保证送达，用它补看。
    /// agent 还没停下时，取到的是目前最新的一条，前面加一句说明。
    /// </summary>
    /// <param name="command">指令，带目标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回复文字；目标不合法或没有 agent 时为失败结果。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
    private async Task<CommandOutcome> LastReplyAsync(LastReplyCommand command, CancellationToken cancellationToken)
    {
        if (InvalidTarget(command.Target) is { } invalid)
        {
            return invalid;
        }

        var agent = await herdr.GetAgentAsync(command.Target, cancellationToken).ConfigureAwait(false);
        if (agent.Kind is null)
        {
            return CommandOutcome.Failure(ErrorCodes.NotFound, "这个 pane 里没有识别出 agent");
        }

        var reply = await replies.GetLastReplyAsync(agent, tracker.LastPrompt(agent.PaneId), cancellationToken).ConfigureAwait(false);
        var note = agent.Status switch
        {
            AgentStatus.Working => "（它还在干活，这是目前最新的一条）\n\n",
            AgentStatus.Blocked => "（它正在等你确认，确认框用 /read 查看）\n\n",
            _ => "",
        };
        return CommandOutcome.Success(new TextData(note + reply));
    }

    /// <summary>给 agent 改名，新名字要符合 herdr 的命名规则。</summary>
    /// <param name="command">指令，带目标和新名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>改名的确认；目标或名字不合法时为失败结果。</returns>
    /// <exception cref="HerdrException">herdr 出错，由 <see cref="ExecuteAsync"/> 转成失败结果。</exception>
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
    /// <param name="key">一个按键。</param>
    /// <returns><c>enter</c>、<c>y</c> 或一位数字为 true。</returns>
    private static bool IsConfirmKey(string key) => key is "enter" or "y" || (key.Length == 1 && char.IsAsciiDigit(key[0]));

    /// <summary>屏幕摘要；读取失败时返回提示文字，不影响主流程。</summary>
    /// <param name="agent">在等确认的 agent。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>屏幕摘要；读取失败时为提示用 /read 查看的文字。</returns>
    private async Task<string> ShowDialogOrNoteAsync(HerdrAgent agent, CancellationToken cancellationToken)
    {
        try
        {
            return await replies.ShowDialogAsync(agent, cancellationToken).ConfigureAwait(false);
        }
        catch (HerdrException ex)
        {
            logger.LogWarning("读取 {Target} 的屏幕失败：{Message}", agent.Name ?? agent.PaneId, ex.Message);
            return "（读取屏幕失败，请用 /read 查看）";
        }
    }

    /// <summary>校验指令里的目标，在交给 herdr 之前拦下不合法的输入。</summary>
    /// <param name="target">服务器发来的目标。</param>
    /// <returns>目标合法时为 null；否则为参数错误的失败结果。</returns>
    private static CommandOutcome? InvalidTarget(string? target) =>
        Validation.IsAgentTarget(target)
            ? null
            : CommandOutcome.Failure(ErrorCodes.InvalidArgument, "目标必须是 agent 名字或 pane ID");

    /// <summary>所有工作区的标签。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>工作区 ID 到标签的映射（标签可能为 null）。</returns>
    /// <exception cref="HerdrException">herdr 出错。</exception>
    private async Task<Dictionary<string, string?>> WorkspaceLabelsAsync(CancellationToken cancellationToken) =>
        (await herdr.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(w => w.WorkspaceId, w => w.Label, StringComparer.Ordinal);

    /// <summary>agent 所在工作区的标签。</summary>
    /// <param name="labels"><see cref="WorkspaceLabelsAsync"/> 取到的标签。</param>
    /// <param name="agent">agent。</param>
    /// <returns>标签；agent 不知道属于哪个工作区、工作区不在列表里或没有标签时为 null。</returns>
    private static string? LabelOf(Dictionary<string, string?> labels, HerdrAgent agent) =>
        agent.WorkspaceId is not null && labels.TryGetValue(agent.WorkspaceId, out var label) ? label : null;

    /// <summary>启动失败后关掉刚建的工作区，最多等 10 秒；关不掉只记日志，不掩盖原来的错误。</summary>
    /// <param name="workspaceId">要关的工作区。</param>
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
