using System.Text.Json;
using ChatPilot.Shared.Security;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>一个正在跟踪、做完要通知的任务。</summary>
/// <param name="Id">任务 ID，删除时用它确认删的是同一个任务。</param>
/// <param name="PaneId">agent 所在 pane。</param>
/// <param name="TerminalId">终端 ID（herdr 重启后 pane ID 可能被别的终端占用，用它确认）。</param>
/// <param name="Kind">agent 类型。</param>
/// <param name="Prompt">交给它的文字；回应确认框之后继续跟踪的任务为 null。</param>
/// <param name="Seq">开始跟踪时的状态序号，之后变了说明它动过。</param>
/// <param name="Since">开始跟踪的时间。</param>
public sealed record TrackedTask(Guid Id, string PaneId, string? TerminalId, string? Kind, string? Prompt, long Seq, DateTimeOffset Since);

/// <summary>
/// 正在跟踪的任务，存在磁盘上（权限 600），节点重启后接着跟踪。每个 pane 最多一条。
/// </summary>
/// <remarks>
/// 任务的结果确实发给服务器之后才删掉记录：节点在发出之前退出（比如断网时重启），重启后还会再查一次、再通知。
/// 所以极少数情况下同一个结果会通知两次，但不会漏。
/// </remarks>
public sealed class TrackedTasks
{
    /// <summary>最多记这么多条（和同时跟踪的上限相当，留些余量）。</summary>
    private const int MaxTasks = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<TrackedTask> _tasks;

    /// <param name="path">保存的文件；为 null 时只记在内存里（测试用）。</param>
    /// <param name="logger">日志。</param>
    public TrackedTasks(string? path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _tasks = Load(path, logger);
    }

    /// <summary>节点配置文件旁边的任务记录文件，例如 <c>~/.chatpilot/node.tasks.json</c>。</summary>
    public static string PathFor(string configPath) => Path.ChangeExtension(Path.GetFullPath(configPath), ".tasks.json");

    /// <summary>所有任务（副本）。</summary>
    public IReadOnlyList<TrackedTask> Snapshot()
    {
        lock (_lock)
        {
            return _tasks.ToList();
        }
    }

    /// <summary>这个 pane 当前的任务。</summary>
    public TrackedTask? Find(string paneId)
    {
        lock (_lock)
        {
            return _tasks.Find(t => t.PaneId == paneId);
        }
    }

    /// <summary>记下一个任务，替换这个 pane 之前的任务。</summary>
    public void Track(TrackedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_lock)
        {
            _tasks.RemoveAll(t => t.PaneId == task.PaneId);
            if (_tasks.Count >= MaxTasks)
            {
                _tasks.RemoveAt(0);
            }

            _tasks.Add(task);
            Save();
        }
    }

    /// <summary>删掉一个任务（已经被新任务替换的话什么也不做）。</summary>
    public void Remove(Guid id)
    {
        lock (_lock)
        {
            if (_tasks.RemoveAll(t => t.Id == id) > 0)
            {
                Save();
            }
        }
    }

    /// <summary>写盘失败只记日志：影响的只是重启后能不能接着跟踪。</summary>
    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(_tasks, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("保存跟踪中的任务失败（节点重启后不会接着跟踪）：{Message}", ex.Message);
        }
    }

    private static List<TrackedTask> Load(string? path, ILogger logger)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<TrackedTask>>(File.ReadAllText(path), JsonOptions)?
                .Where(t => t is { PaneId.Length: > 0 })
                .TakeLast(MaxTasks)
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("读取跟踪中的任务失败，忽略：{Message}", ex.Message);
            return [];
        }
    }
}
