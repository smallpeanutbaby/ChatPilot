using System.Text.Json;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Security;
using ChatPilot.Shared.Text;

namespace ChatPilot.Server.Chat;

/// <summary>一条任务记录。</summary>
/// <param name="At">结束的时间。</param>
/// <param name="Device">设备名。</param>
/// <param name="Agent">agent 的名字。</param>
/// <param name="Succeeded">做完了（true）还是出错了（false）。</param>
/// <param name="Prompt">任务摘要；不知道时为 null（例如盯梢发现的、或者回应确认框之后做完的）。</param>
/// <param name="Result">结果摘要。</param>
/// <param name="StartedAt">开始的时间；不知道时为 null。</param>
public sealed record HistoryEntry(
    DateTimeOffset At,
    string Device,
    string Agent,
    bool Succeeded,
    string? Prompt,
    string? Result,
    DateTimeOffset? StartedAt);

/// <summary>
/// 最近做完（或出错）的任务，给 <c>/history</c> 看。只存摘要（任务和结果各截短到 <see cref="SummaryChars"/> 字），
/// 保存在数据目录的 <c>history.json</c>（权限 600），最多 <see cref="Capacity"/> 条。
/// </summary>
public sealed class TaskHistory
{
    /// <summary>最多保留几条。</summary>
    internal const int Capacity = 200;

    /// <summary>任务和结果摘要的长度。</summary>
    internal const int SummaryChars = 80;

    /// <summary><c>history.json</c> 的序列化设置（驼峰命名）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>保护 <c>_entries</c> 和写盘：多台设备的事件可能同时到达。</summary>
    private readonly Lock _lock = new();
    /// <summary><c>history.json</c> 的完整路径。</summary>
    private readonly string _path;
    private readonly ILogger<TaskHistory> _logger;
    /// <summary>内存里的全部记录（旧的在前），每次变动后整份写盘。</summary>
    private readonly List<HistoryEntry> _entries;

    /// <summary>从数据目录加载；文件不存在或损坏时从空记录开始（历史不是关键数据）。</summary>
    /// <param name="dataDirectory">服务器的数据目录。</param>
    /// <param name="logger">日志。</param>
    public TaskHistory(string dataDirectory, ILogger<TaskHistory> logger)
    {
        _path = Path.Combine(dataDirectory, "history.json");
        _logger = logger;
        _entries = Load(_path, logger);
    }

    /// <summary>记下一个事件；只记做完和出错，卡在确认框的不算一个任务的结束。</summary>
    /// <param name="deviceName">事件来自哪台设备。</param>
    /// <param name="agentEvent">节点上报的事件。</param>
    /// <param name="now">记作任务结束的时间。</param>
    /// <exception cref="ArgumentNullException"><paramref name="agentEvent"/> 为 null。</exception>
    public void Record(string deviceName, AgentEvent agentEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        if (agentEvent.Kind == AgentEventKind.Blocked)
        {
            return;
        }

        var entry = new HistoryEntry(
            now,
            deviceName,
            agentEvent.Agent.DisplayName,
            agentEvent.Kind == AgentEventKind.Completed,
            Summarize(agentEvent.Prompt),
            Summarize(agentEvent.Text),
            agentEvent.StartedAt);
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveRange(0, _entries.Count - Capacity);
            }

            try
            {
                SecureFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("保存任务记录失败（只影响重启后的 /history）：{Message}", ex.Message);
            }
        }
    }

    /// <summary>所有记录（旧的在前）。</summary>
    /// <returns>记录的副本；还没有记录时为空列表。</returns>
    public IReadOnlyList<HistoryEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    /// <summary>摘要：合并空白、截短。</summary>
    /// <param name="text">原文，可以有多行。</param>
    /// <returns>压成一行、不超过 <see cref="SummaryChars"/> 字的摘要；原文为 null 或全是空白时为 null。</returns>
    internal static string? Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return TextChunker.Truncate(flat, SummaryChars);
    }

    /// <summary>读取保存的记录，只保留最新的 <see cref="Capacity"/> 条。</summary>
    /// <param name="path"><c>history.json</c> 的路径。</param>
    /// <param name="logger">读取失败时记日志。</param>
    /// <returns>记录列表；文件不存在、读不了或内容损坏时为空列表。</returns>
    private static List<HistoryEntry> Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path), JsonOptions)?.TakeLast(Capacity).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("读取任务记录失败，从空记录开始：{Message}", ex.Message);
            return [];
        }
    }
}
