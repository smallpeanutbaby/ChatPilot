using System.Text.Json;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Security;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>
/// "盯着" agent：不是通过 ChatPilot 派的活（比如在电脑前开的长任务），做完或卡在确认框时也通知。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>有盯着的 agent 时才轮询 <c>herdr agent list</c>，按状态变化判断：进入确认框、从工作中回到空闲。</item>
/// <item>已经在被 <see cref="PromptTracker"/> 跟踪的 agent 不重复通知；两次轮询之间跟踪方上报过的，也不再通知。</item>
/// <item>盯哪些 agent 按终端 ID 记在磁盘上（权限 600），节点重启后继续盯。</item>
/// </list>
/// </remarks>
public sealed class AgentWatcher : IAsyncDisposable
{
    /// <summary>单独盯着的 agent 最多记多少个。</summary>
    private const int MaxWatched = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HerdrClient _herdr;
    private readonly ReplyService _replies;
    private readonly IAgentEventSink _sink;
    private readonly PromptTracker _tracker;
    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, Observed> _last = new(StringComparer.Ordinal);
    private WatchSettings _settings;
    private Task? _loop;

    /// <summary>上次轮询时看到的状态。</summary>
    private sealed record Observed(string Status, long Seq, string? TerminalId);

    /// <summary>保存到磁盘的设置。</summary>
    /// <param name="All">盯着所有 agent。</param>
    /// <param name="Keys">单独盯着的 agent（终端 ID，没有时用 <c>pane:</c> 加 pane ID）。</param>
    private sealed record WatchSettings(bool All, List<string> Keys);

    /// <param name="herdr">herdr 客户端。</param>
    /// <param name="replies">取回复和屏幕摘要。</param>
    /// <param name="sink">事件上报给谁（连接层）。</param>
    /// <param name="tracker">正在跟踪的任务，用来去重。</param>
    /// <param name="path">保存设置的文件；为 null 时只记在内存里（测试用）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="interval">轮询间隔（默认 5 秒）。</param>
    public AgentWatcher(
        HerdrClient herdr,
        ReplyService replies,
        IAgentEventSink sink,
        PromptTracker tracker,
        string? path,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _herdr = herdr;
        _replies = replies;
        _sink = sink;
        _tracker = tracker;
        _path = path;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(5);
        _settings = Load(path, logger);
    }

    /// <summary>节点配置文件旁边的设置文件，例如 <c>~/.chatpilot/node.watch.json</c>。</summary>
    public static string PathFor(string configPath) => Path.ChangeExtension(Path.GetFullPath(configPath), ".watch.json");

    /// <summary>是否盯着这台电脑上的所有 agent。</summary>
    public bool WatchAll
    {
        get
        {
            lock (_lock)
            {
                return _settings.All;
            }
        }
    }

    /// <summary>是否盯着这个 agent（单独盯着，或者盯着全部）。</summary>
    public bool IsWatched(HerdrAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        lock (_lock)
        {
            return _settings.All || _settings.Keys.Contains(KeyOf(agent));
        }
    }

    /// <summary>开始或停止盯一个 agent，返回给用户看的说明。</summary>
    public string Set(HerdrAgent agent, bool enable)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var name = agent.Name ?? agent.PaneId;
        lock (_lock)
        {
            var key = KeyOf(agent);
            var keys = _settings.Keys.Where(k => k != key).ToList();
            if (enable)
            {
                keys.Add(key);
                if (keys.Count > MaxWatched)
                {
                    keys.RemoveAt(0);
                }
            }

            _settings = _settings with { Keys = keys };
            Save();
            if (enable)
            {
                // 从现在的状态开始算，之前的变化不补发。
                _last[agent.PaneId] = new Observed(agent.Status, agent.StateChangeSeq, agent.TerminalId);
            }

            return enable switch
            {
                true when _settings.All => $"已经在盯着这台电脑上的所有 agent 了（{name} 也记下了）",
                true => $"开始盯着 {name}：它做完或卡在确认框时会通知你（包括在电脑上派的活）",
                false when _settings.All => $"不再单独盯着 {name}，但还在盯着这台电脑上的所有 agent。全部停止用 /unwatch all",
                false => $"不再盯着 {name}",
            };
        }
    }

    /// <summary>开始或停止盯所有 agent；停止时同时清掉单独盯着的。</summary>
    public string SetAll(bool enable)
    {
        lock (_lock)
        {
            _settings = enable ? _settings with { All = true } : new WatchSettings(false, []);
            Save();
            return enable
                ? "开始盯着这台电脑上的所有 agent：做完或卡在确认框时都会通知你"
                : "不再盯着这台电脑上的任何 agent";
        }
    }

    /// <summary>在后台开始轮询。</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stopping.Token));

    /// <summary>停止轮询（最多等 5 秒）。</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // 退出时不再等。
            }
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HerdrException ex)
            {
                _logger.LogDebug("盯梢时查询 herdr 失败：{Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "盯梢出错");
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>轮询一次：比较每个被盯着的 agent 和上次的状态，有该通知的变化就上报。没有盯着任何 agent 时不调用 herdr。</summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_settings.All && _settings.Keys.Count == 0)
            {
                _last.Clear();
                return;
            }
        }

        var agents = await _herdr.ListAgentsAsync(cancellationToken).ConfigureAwait(false);
        var due = new List<(HerdrAgent Agent, AgentEventKind Kind)>();
        lock (_lock)
        {
            foreach (var paneId in _last.Keys.Except(agents.Select(a => a.PaneId), StringComparer.Ordinal).ToList())
            {
                _last.Remove(paneId);
            }

            foreach (var agent in agents)
            {
                var previous = _last.GetValueOrDefault(agent.PaneId);
                _last[agent.PaneId] = new Observed(agent.Status, agent.StateChangeSeq, agent.TerminalId);
                if (previous is null || previous.TerminalId != agent.TerminalId || agent.Kind is null
                    || !(_settings.All || _settings.Keys.Contains(KeyOf(agent))))
                {
                    continue;
                }

                // 跟踪方在上次观察之后上报过，说明这段变化已经通知过了。
                if (Transition(previous, agent) is { } kind
                    && !_tracker.IsTracking(agent.PaneId)
                    && !(_tracker.LastReportedSeq(agent.PaneId) > previous.Seq))
                {
                    due.Add((agent, kind));
                }
            }
        }

        foreach (var (agent, kind) in due)
        {
            await ReportAsync(agent, kind, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 该通知的状态变化：进入确认框；从工作中回到空闲；或者两次轮询之间就做完了（还是空闲，但状态序号至少变了两次）。
    /// </summary>
    internal static AgentEventKind? Transition(string previousStatus, long previousSeq, HerdrAgent current)
    {
        if (current.Status == AgentStatus.Blocked)
        {
            return previousStatus != AgentStatus.Blocked ? AgentEventKind.Blocked : null;
        }

        if (!current.IsReady)
        {
            return null;
        }

        return previousStatus == AgentStatus.Working || current.StateChangeSeq - previousSeq >= 2 ? AgentEventKind.Completed : null;
    }

    private static AgentEventKind? Transition(Observed previous, HerdrAgent current) => Transition(previous.Status, previous.Seq, current);

    private async Task ReportAsync(HerdrAgent agent, AgentEventKind kind, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = kind == AgentEventKind.Blocked
                ? await _replies.ShowDialogAsync(agent, cancellationToken).ConfigureAwait(false)
                : await _replies.GetLastReplyAsync(agent, prompt: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HerdrException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("整理 {Agent} 的结果失败：{Message}", agent.Name ?? agent.PaneId, ex.Message);
            text = kind == AgentEventKind.Blocked ? "（读取屏幕失败，请用 /read 查看）" : "（提取回复失败，请用 /read 查看）";
        }

        _sink.Publish(new AgentEvent(kind, agent.ToSummary(workspaceLabel: null), text, Queued: _tracker.QueuedCount(agent.PaneId)));
    }

    private static string KeyOf(HerdrAgent agent) => agent.TerminalId ?? "pane:" + agent.PaneId;

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(_settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("保存盯梢设置失败（节点重启后不会接着盯）：{Message}", ex.Message);
        }
    }

    private static WatchSettings Load(string? path, ILogger logger)
    {
        if (path is null || !File.Exists(path))
        {
            return new WatchSettings(false, []);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<WatchSettings>(File.ReadAllText(path), JsonOptions);
            return loaded is null
                ? new WatchSettings(false, [])
                : loaded with { Keys = (loaded.Keys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).TakeLast(MaxWatched).ToList() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("读取盯梢设置失败，忽略：{Message}", ex.Message);
            return new WatchSettings(false, []);
        }
    }
}
