using System.Collections.Concurrent;
using ChatPilot.Server.Chat;
using ChatPilot.Server.Options;
using ChatPilot.Server.State;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Devices;

/// <summary>
/// 设备掉线、恢复时通知主人。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>断开后 <see cref="ServerOptions.OfflineNoticeSeconds"/> 秒内没连回来才通知"离线"：网络抖动、节点重启不打扰。</item>
/// <item>发过"离线"的设备连回来时通知"恢复"。发没发过记在状态文件里（<see cref="DeviceRecord.OfflineNotified"/>），
/// 服务器重启后不会重复通知，也不会漏掉"恢复"。</item>
/// <item>服务器启动时，上次在线过、还没通知过离线的设备也按同样的规则检查，服务器重启期间掉线的电脑同样会通知。</item>
/// <item>被撤销的设备、服务器关闭引起的断开都不通知。</item>
/// </list>
/// </remarks>
public sealed class PresenceNotifier(
    StateStore store,
    Notifier notifier,
    TimeProvider time,
    IOptions<ServerOptions> options,
    ILogger<PresenceNotifier> logger) : IDisposable
{
    // 每台设备当前的连接数。新连接替换旧连接时，旧连接的清理可能晚于新连接登记，按数量算才不会把在线的设备当成掉线。
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    private TimeSpan Grace => TimeSpan.FromSeconds(options.Value.OfflineNoticeSeconds);

    private bool Enabled => options.Value.OfflineNoticeSeconds > 0;

    /// <summary>服务器启动后调用：给上次在线过、还没通知过离线的设备排一次检查。</summary>
    public void Start()
    {
        if (!Enabled)
        {
            return;
        }

        foreach (var device in store.Devices().Where(d => d.LastSeenAt is not null && !d.OfflineNotified))
        {
            Schedule(device.Id);
        }
    }

    /// <summary>服务器关闭前调用：之后的断开都不再通知。</summary>
    public void Stop() => _stopping.Cancel();

    /// <summary>设备的一个连接建立了（在更新"最后在线时间"之前调用，才算得出离线了多久）。</summary>
    public void Connected(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lock)
        {
            _connections[device.Id] = _connections.GetValueOrDefault(device.Id) + 1;
        }

        CancelPending(device.Id);
        if (store.FindDevice(device.Id) is not { OfflineNotified: true } current)
        {
            return;
        }

        SetOfflineNotified(device.Id, false);
        var away = current.LastSeenAt is { } seen ? $"（离线了 {ReplyFormatter.Duration(time.GetUtcNow() - seen)}）" : "";
        notifier.Enqueue($"✅ [{current.Name}] 重新上线了{away}");
    }

    /// <summary>设备的一个连接断开了（包括被新连接替换的旧连接），在更新"最后在线时间"之后调用。</summary>
    public void Disconnected(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lock)
        {
            var remaining = _connections.GetValueOrDefault(device.Id) - 1;
            if (remaining > 0)
            {
                _connections[device.Id] = remaining;
                return;
            }

            _connections.Remove(device.Id);
        }

        if (Enabled && !_stopping.IsCancellationRequested && store.FindDevice(device.Id) is not null)
        {
            Schedule(device.Id);
        }
    }

    private bool IsOnline(string deviceId)
    {
        lock (_lock)
        {
            return _connections.ContainsKey(deviceId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        foreach (var id in _pending.Keys)
        {
            CancelPending(id);
        }

        _stopping.Dispose();
    }

    private void Schedule(string deviceId)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (_pending.TryGetValue(deviceId, out var previous) && _pending.TryUpdate(deviceId, cancel, previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        else if (!_pending.TryAdd(deviceId, cancel))
        {
            // 并发排了两次：留着已有的那个就够了。
            cancel.Dispose();
            return;
        }

        _ = CheckLaterAsync(deviceId, cancel);
    }

    private async Task CheckLaterAsync(string deviceId, CancellationTokenSource cancel)
    {
        try
        {
            await Task.Delay(Grace, time, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_pending.TryRemove(KeyValuePair.Create(deviceId, cancel)))
        {
            return;
        }

        cancel.Dispose();
        if (IsOnline(deviceId) || store.FindDevice(deviceId) is not { OfflineNotified: false } device)
        {
            return;
        }

        SetOfflineNotified(deviceId, true);
        var away = device.LastSeenAt is { } seen ? ReplyFormatter.Duration(time.GetUtcNow() - seen) : ReplyFormatter.Duration(Grace);
        notifier.Enqueue($"⚠️ [{device.Name}] 离线了，已经 {away} 没连上服务器：可能是电脑睡眠、断网，或者节点退出了。恢复后会再通知你");
    }

    private void CancelPending(string deviceId)
    {
        if (_pending.TryRemove(deviceId, out var cancel))
        {
            cancel.Cancel();
            cancel.Dispose();
        }
    }

    private void SetOfflineNotified(string deviceId, bool value)
    {
        var saved = store.UpdateBestEffort(state =>
        {
            var index = state.Devices.FindIndex(d => d.Id == deviceId);
            if (index >= 0)
            {
                state.Devices[index] = state.Devices[index] with { OfflineNotified = value };
            }
        });
        if (!saved)
        {
            logger.LogWarning("保存设备离线通知状态失败（磁盘写入失败），只更新了内存");
        }
    }
}
