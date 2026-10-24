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
public sealed record QueuedPrompt(Guid Id, string PaneId, string? TerminalId, string? Kind, string? Name, string Text, DateTimeOffset EnqueuedAt);

/// <summary>
/// agent 正在干活时发来的指令：按 pane 排队，等它做完一条再发下一条。存在磁盘上（权限 600），节点重启后接着发。
/// </summary>
public sealed class PromptQueue
{
    /// <summary>每个 agent 最多排几条。</summary>
    public const int MaxPerAgent = 10;

    /// <summary>所有 agent 加起来最多排几条。</summary>
    private const int MaxTotal = 50;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<QueuedPrompt> _items;

    /// <param name="path">保存的文件；为 null 时只记在内存里（测试用）。</param>
    /// <param name="logger">日志。</param>
    public PromptQueue(string? path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _items = Load(path, logger);
    }

    /// <summary>节点配置文件旁边的排队记录文件，例如 <c>~/.chatpilot/node.queue.json</c>。</summary>
    public static string PathFor(string configPath) => Path.ChangeExtension(Path.GetFullPath(configPath), ".queue.json");

    /// <summary>有排队指令的 pane。</summary>
    public IReadOnlyList<string> Panes()
    {
        lock (_lock)
        {
            return _items.Select(i => i.PaneId).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>这个 pane 排了几条。</summary>
    public int Count(string paneId)
    {
        lock (_lock)
        {
            return _items.Count(i => i.PaneId == paneId);
        }
    }

    /// <summary>这个 pane 排在最前面的一条。</summary>
    public QueuedPrompt? Peek(string paneId)
    {
        lock (_lock)
        {
            return _items.Find(i => i.PaneId == paneId);
        }
    }

    /// <summary>排到队尾，返回排在第几（从 1 开始）；超过上限时返回 null。</summary>
    public int? Enqueue(HerdrAgent agent, string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);
        lock (_lock)
        {
            var position = _items.Count(i => i.PaneId == agent.PaneId) + 1;
            if (position > MaxPerAgent || _items.Count >= MaxTotal)
            {
                return null;
            }

            _items.Add(new QueuedPrompt(Guid.NewGuid(), agent.PaneId, agent.TerminalId, agent.Kind, agent.Name, text, now));
            Save();
            return position;
        }
    }

    /// <summary>删掉一条（已经被清空的话返回 false）。</summary>
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
