using System.Text.Json;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared.Security;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>一条排队等着发给 agent 的指令。</summary>
/// <param name="Id">ID，取出或删除时用它确认是同一条。</param>
/// <param name="PaneId">agent 所在 pane。</param>
/// <param name="TerminalId">终端 ID（herdr 重启后 pane ID 可能被别的终端占用，用它确认）。</param>
/// <param name="Kind">agent 类型。</param>
/// <param name="Name">排队时 agent 的名字（只用于通知里显示）。</param>
/// <param name="Text">要发的文字。</param>
/// <param name="EnqueuedAt">排队时间。</param>
/// <param name="Attachments">这条指令带的、已经存下的图片路径；没发出去就被清掉时删掉这些图片。没有图片时为 null。</param>
public sealed record QueuedPrompt(
    Guid Id, string PaneId, string? TerminalId, string? Kind, string? Name, string Text, DateTimeOffset EnqueuedAt,
    IReadOnlyList<string>? Attachments = null);

/// <summary>
/// agent 正在干活时发来的指令：按 pane 排队，等它做完一条再发下一条。存在磁盘上（权限 600），节点重启后接着发。
/// </summary>
public sealed class PromptQueue
{
    /// <summary>每个 agent 最多排几条。</summary>
    public const int MaxPerAgent = 10;

    /// <summary>所有 agent 加起来最多排几条。</summary>
    private const int MaxTotal = 50;

    /// <summary>读写排队记录文件用的 JSON 设置（Web 默认值，属性名用小驼峰）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>排队记录文件的路径；为 null 时只记在内存里。</summary>
    private readonly string? _path;
    private readonly ILogger _logger;

    /// <summary>保护 <see cref="_items"/> 和写盘：排队、发出和清空可能同时发生。</summary>
    private readonly Lock _lock = new();

    /// <summary>所有 pane 的排队指令，按排队先后排列；同一个 pane 的先后就是发出的顺序。</summary>
    private readonly List<QueuedPrompt> _items;

    /// <summary>创建排队记录，并读出上次保存的指令。</summary>
    /// <param name="path">保存的文件；为 null 时只记在内存里（测试用）。</param>
    /// <param name="logger">日志。</param>
    public PromptQueue(string? path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _items = Load(path, logger);
    }

    /// <summary>节点配置文件旁边的排队记录文件，例如 <c>~/.chatpilot/node.queue.json</c>。</summary>
    /// <param name="configPath">节点配置文件的路径。</param>
    /// <returns>排队记录文件的完整路径。</returns>
    public static string PathFor(string configPath) => Path.ChangeExtension(Path.GetFullPath(configPath), ".queue.json");

    /// <summary>有排队指令的 pane。</summary>
    /// <returns>不重复的 pane ID（副本）；没有排队时为空列表。</returns>
    public IReadOnlyList<string> Panes()
    {
        lock (_lock)
        {
            return _items.Select(i => i.PaneId).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>这个 pane 排了几条。</summary>
    /// <param name="paneId">agent 所在 pane。</param>
    /// <returns>排队的条数；没有时为 0。</returns>
    public int Count(string paneId)
    {
        lock (_lock)
        {
            return _items.Count(i => i.PaneId == paneId);
        }
    }

    /// <summary>这个 pane 排在最前面的一条。</summary>
    /// <param name="paneId">agent 所在 pane。</param>
    /// <returns>排在最前面的指令（不会从队列里取走）；没有排队时为 null。</returns>
    public QueuedPrompt? Peek(string paneId)
    {
        lock (_lock)
        {
            return _items.Find(i => i.PaneId == paneId);
        }
    }

    /// <summary>排到队尾，返回排在第几（从 1 开始）；超过上限时返回 null。</summary>
    /// <param name="agent">要接这条指令的 agent，记下它的 pane、终端 ID、类型和名字。</param>
    /// <param name="text">要发的文字。</param>
    /// <param name="now">排队时间。</param>
    /// <param name="attachments">这条指令带的、已经存下的图片路径；没有图片时为 null。</param>
    /// <returns>在这个 pane 里排第几；这个 agent 排满 <see cref="MaxPerAgent"/> 条或总数到上限时为 null。</returns>
    public int? Enqueue(HerdrAgent agent, string text, DateTimeOffset now, IReadOnlyList<string>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        lock (_lock)
        {
            var position = _items.Count(i => i.PaneId == agent.PaneId) + 1;
            if (position > MaxPerAgent || _items.Count >= MaxTotal)
            {
                return null;
            }

            _items.Add(new QueuedPrompt(Guid.NewGuid(), agent.PaneId, agent.TerminalId, agent.Kind, agent.Name, text, now, attachments is { Count: > 0 } ? attachments : null));
            Save();
            return position;
        }
    }

    /// <summary>删掉一条（已经被清空的话返回 false）。</summary>
    /// <param name="id">要删的指令 ID。</param>
    /// <returns>确实删掉了为 true；已经不在队列里为 false。</returns>
    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            if (_items.RemoveAll(i => i.Id == id) == 0)
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>删掉这个 pane 里符合条件的指令，返回删掉的（按排队顺序）。</summary>
    /// <param name="paneId">agent 所在 pane。</param>
    /// <param name="predicate">要删哪些指令。</param>
    /// <returns>删掉的指令；没有符合条件的为空列表。</returns>
    public IReadOnlyList<QueuedPrompt> RemoveWhere(string paneId, Func<QueuedPrompt, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_lock)
        {
            var removed = _items.Where(i => i.PaneId == paneId && predicate(i)).ToList();
            if (removed.Count > 0)
            {
                _items.RemoveAll(removed.Contains);
                Save();
            }

            return removed;
        }
    }

    /// <summary>清空这个 pane 排的所有指令，返回清掉了几条。</summary>
    /// <param name="paneId">agent 所在 pane。</param>
    /// <returns>清掉的条数；本来就没有排队时为 0。</returns>
    public int Clear(string paneId)
    {
        lock (_lock)
        {
            var removed = _items.RemoveAll(i => i.PaneId == paneId);
            if (removed > 0)
            {
                Save();
            }

            return removed;
        }
    }

    /// <summary>写盘失败只记日志：影响的只是重启后还能不能接着发。</summary>
    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("保存排队的指令失败（节点重启后不会接着发）：{Message}", ex.Message);
        }
    }

    /// <summary>读出上次保存的排队指令；文件坏了只记日志，当作没有排队。</summary>
    /// <param name="path">保存的文件；为 null 时不读。</param>
    /// <param name="logger">日志。</param>
    /// <returns>读到的指令（去掉没有 pane ID 或文字的，最多保留最后 <see cref="MaxTotal"/> 条）；没有文件或读取失败时为空列表。</returns>
    private static List<QueuedPrompt> Load(string? path, ILogger logger)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<QueuedPrompt>>(File.ReadAllText(path), JsonOptions)?
                .Where(i => i is { PaneId.Length: > 0, Text.Length: > 0 })
                .TakeLast(MaxTotal)
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("读取排队的指令失败，忽略：{Message}", ex.Message);
            return [];
        }
    }
}
