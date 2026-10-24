using System.Text.Json;
using ChatPilot.Shared.Protocol;
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
/// <param name="Result">已经上报、还没确认送到服务器的结果；还在跟踪时为 null。</param>
public sealed record TrackedTask(
    Guid Id, string PaneId, string? TerminalId, string? Kind, string? Prompt, long Seq, DateTimeOffset Since, AgentEvent? Result = null);

/// <summary>
/// 正在跟踪的任务，存在磁盘上（权限 600），节点重启后接着跟踪。每个 pane 最多一条还在跟踪的，
/// 另外可能有几条已经有结果、等着送到服务器的。
/// </summary>
/// <remarks>
/// 任务的结果确实发给服务器之后才删掉记录：节点在发出之前退出（比如断网时重启），重启后把记下的结果原样再发一次。
/// 结果送到之前同一个 pane 又开始了新任务，也不会替换掉这条记录。所以极少数情况下同一个结果会通知两次，但不会漏。
/// </remarks>
public sealed class TrackedTasks
{
    /// <summary>最多记这么多条（和同时跟踪的上限相当，留些余量）。</summary>
    private const int MaxTasks = 64;

    /// <summary>读写记录文件用的 JSON 设置（Web 默认值，属性名用小驼峰）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>记录文件的路径；为 null 时只记在内存里。</summary>
    private readonly string? _path;
    private readonly ILogger _logger;

    /// <summary>保护 <see cref="_tasks"/> 和写盘：多个任务的后台跟踪会同时增删记录。</summary>
    private readonly Lock _lock = new();

    /// <summary>当前跟踪的任务，按记下的先后排列，超出上限时先丢最早的。</summary>
    private readonly List<TrackedTask> _tasks;

    /// <summary>创建任务记录，并读出上次保存的任务。</summary>
    /// <param name="path">保存的文件；为 null 时只记在内存里（测试用）。</param>
    /// <param name="logger">日志。</param>
    public TrackedTasks(string? path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _tasks = Load(path, logger);
    }

    /// <summary>节点配置文件旁边的任务记录文件，例如 <c>~/.chatpilot/node.tasks.json</c>。</summary>
    /// <param name="configPath">节点配置文件的路径。</param>
    /// <returns>任务记录文件的完整路径。</returns>
    public static string PathFor(string configPath) => Path.ChangeExtension(Path.GetFullPath(configPath), ".tasks.json");

    /// <summary>所有任务（副本）。</summary>
    /// <returns>当前所有任务的副本，之后的增删不影响它。</returns>
    public IReadOnlyList<TrackedTask> Snapshot()
    {
        lock (_lock)
        {
            return _tasks.ToList();
        }
    }

    /// <summary>这个 pane 当前还在跟踪的任务（不算已经有结果、等着送到服务器的）。</summary>
    /// <param name="paneId">agent 所在 pane。</param>
    /// <returns>这个 pane 还在跟踪的任务；没有时为 null。</returns>
    public TrackedTask? Find(string paneId)
    {
        lock (_lock)
        {
            return _tasks.FindLast(t => t.PaneId == paneId && t.Result is null);
        }
    }

    /// <summary>记下一个任务，替换这个 pane 之前还在跟踪的任务；已经有结果、还没送到服务器的留着。</summary>
    /// <param name="task">要跟踪的任务。</param>
    public void Track(TrackedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_lock)
        {
            _tasks.RemoveAll(t => t.PaneId == task.PaneId && t.Result is null);
            if (_tasks.Count >= MaxTasks)
            {
                _tasks.RemoveAt(0);
            }

            _tasks.Add(task);
            Save();
        }
    }

    /// <summary>记下任务上报的结果：之后这条记录不会被同一个 pane 的新任务替换，送到服务器之前节点重启的话原样再发。</summary>
    /// <param name="id">任务 ID。</param>
    /// <param name="result">上报的事件。</param>
    /// <returns>记下了为 true；这条记录已经不在（被删掉或替换掉了）为 false。</returns>
    public bool Complete(Guid id, AgentEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return false;
            }

            _tasks[index] = _tasks[index] with { Result = result };
            Save();
            return true;
        }
    }

    /// <summary>删掉一个任务（已经被新任务替换的话什么也不做）。</summary>
    /// <param name="id">要删的任务 ID。</param>
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

    /// <summary>读出上次保存的任务；文件坏了只记日志，当作没有任务。</summary>
    /// <param name="path">保存的文件；为 null 时不读。</param>
    /// <param name="logger">日志。</param>
    /// <returns>读到的任务（去掉没有 pane ID 的，最多保留最后 <see cref="MaxTasks"/> 条）；没有文件或读取失败时为空列表。</returns>
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
