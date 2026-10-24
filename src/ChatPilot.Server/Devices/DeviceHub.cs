using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using ChatPilot.Server.Chat;
using ChatPilot.Server.State;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Server.Devices;

/// <summary>在线设备的连接信息（来自节点握手时的 hello）。</summary>
/// <param name="DeviceId">设备 ID。</param>
/// <param name="HostName">电脑名。</param>
/// <param name="Platform">操作系统描述。</param>
/// <param name="NodeVersion">节点程序版本。</param>
/// <param name="ConnectedAt">连上的时间。</param>
public sealed record DeviceSession(string DeviceId, string HostName, string Platform, string NodeVersion, DateTimeOffset ConnectedAt);

/// <summary>
/// 管理所有在线设备：处理握手、给设备发指令并等结果、接收设备上报的事件。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>同一台设备再次连接时，新连接替换旧连接（旧连接收到 <see cref="CloseReasons.Replaced"/>）。</item>
/// <item>设备上报的事件按顺序排队转发，每台设备每分钟最多 <see cref="MaxEventsPerMinute"/> 条，防止刷屏。</item>
/// <item>上线、真正下线（不是被新连接替换）时告诉 <see cref="PresenceNotifier"/>，由它决定要不要通知主人。</item>
/// </list>
/// </remarks>
public sealed class DeviceHub(StateStore store, Notifier notifier, PresenceNotifier presence, TimeProvider time, ILogger<DeviceHub> logger)
{
    /// <summary>每台设备每分钟最多转发的事件数。</summary>
    internal const int MaxEventsPerMinute = 20;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, NodeConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>设备当前是否在线。</summary>
    public bool IsOnline(string deviceId) => _connections.ContainsKey(deviceId);

    /// <summary>在线设备的连接信息；离线时为 null。</summary>
    public DeviceSession? Session(string deviceId) =>
        _connections.TryGetValue(deviceId, out var connection) ? connection.Session : null;

    /// <summary>给设备发指令并等结果。设备离线、超时都会返回失败结果，不抛异常。</summary>
    public async Task<CommandOutcome> SendAsync(string deviceId, NodeCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
        {
            return CommandOutcome.Failure(ErrorCodes.Offline, "设备不在线");
        }

        return await connection.SendCommandAsync(command, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>撤销设备后立即断开它的连接。</summary>
    public async Task DisconnectAsync(string deviceId)
    {
        if (_connections.TryRemove(deviceId, out var connection))
        {
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, CloseReasons.Revoked).ConfigureAwait(false);
        }
    }

    /// <summary>服务器关闭时主动断开所有设备，避免优雅关闭一直等着这些长连接结束。节点会自动重连。</summary>
    public Task CloseAllAsync() =>
        Task.WhenAll(_connections.Values.Select(c => c.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, CloseReasons.ShuttingDown)));

    /// <summary>处理一个已通过令牌验证的设备连接，直到连接断开。</summary>
    public async Task RunConnectionAsync(DeviceRecord device, WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(socket);

        HelloMessage? hello;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(HandshakeTimeout);
            hello = await WebSocketMessaging.ReceiveAsync(socket, ProtocolInfo.MaxFrameBytes, handshake.Token).ConfigureAwait(false) as HelloMessage;
        }
        catch (Exception ex) when (ex is WireProtocolException or OperationCanceledException or WebSocketException)
        {
            logger.LogWarning("设备 {Device} 握手失败：{Message}", device.Name, ex.Message);
            await WebSocketMessaging.CloseQuietlyAsync(socket, WebSocketCloseStatus.ProtocolError, "handshake failed").ConfigureAwait(false);
            return;
        }

        if (hello is null)
        {
            await WebSocketMessaging.CloseQuietlyAsync(socket, WebSocketCloseStatus.ProtocolError, "expected hello").ConfigureAwait(false);
            return;
        }

        if (hello.ProtocolVersion != ProtocolInfo.Version)
        {
            logger.LogWarning("设备 {Device} 协议版本 {NodeVersion} 与服务器 {ServerVersion} 不一致", device.Name, hello.ProtocolVersion, ProtocolInfo.Version);
            await WebSocketMessaging.CloseQuietlyAsync(
                socket,
                WebSocketCloseStatus.PolicyViolation,
                string.Create(CultureInfo.InvariantCulture, $"protocol version mismatch: server {ProtocolInfo.Version}, node {hello.ProtocolVersion}")).ConfigureAwait(false);
            return;
        }

        var session = new DeviceSession(device.Id, Clip(hello.HostName, 64), Clip(hello.Platform, 100), Clip(hello.NodeVersion, 32), time.GetUtcNow());
        var connection = new NodeConnection(device, session, socket);
        await connection.SendAsync(new WelcomeMessage(device.Id, device.Name), cancellationToken).ConfigureAwait(false);

        if (_connections.TryGetValue(device.Id, out var previous))
        {
            logger.LogInformation("设备 {Device} 重新连接，替换旧连接", device.Name);
            await previous.CloseAsync(WebSocketCloseStatus.PolicyViolation, CloseReasons.Replaced).ConfigureAwait(false);
        }

        _connections[device.Id] = connection;

        // 令牌是在握手之前验证的；如果握手期间设备被 /unpair 撤销了，这里要把它踢掉。
        if (store.FindDevice(device.Id) is null)
        {
            _connections.TryRemove(new KeyValuePair<string, NodeConnection>(device.Id, connection));
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, CloseReasons.Revoked).ConfigureAwait(false);
            return;
        }

        presence.Connected(device);
        TouchLastSeen(device.Id);
        logger.LogInformation("设备 {Device} 上线（{Host}，{Platform}）", device.Name, session.HostName, session.Platform);

        try
        {
            await ReceiveLoopAsync(device, connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connection.FailPending();
            _connections.TryRemove(new KeyValuePair<string, NodeConnection>(device.Id, connection));
            TouchLastSeen(device.Id);
            presence.Disconnected(device);
            if (!IsOnline(device.Id))
            {
                logger.LogInformation("设备 {Device} 下线", device.Name);
            }
        }
    }

    private async Task ReceiveLoopAsync(DeviceRecord device, NodeConnection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WireMessage? message;
            try
            {
                message = await WebSocketMessaging.ReceiveAsync(connection.Socket, ProtocolInfo.MaxFrameBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (WireProtocolException ex)
            {
                logger.LogWarning("设备 {Device} 发来不合规的消息：{Message}", device.Name, ex.Message);
                await connection.CloseAsync(ex.CloseStatus, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
                return;
            }

            switch (message)
            {
                case null:
                    await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
                    return;
                case ResultMessage result:
                    connection.Complete(result);
                    break;
                case AgentEventMessage agentEvent:
                    ForwardEvent(device, connection, agentEvent.Event);
                    break;
                default:
                    logger.LogWarning("设备 {Device} 发来不该由节点发送的消息 {Type}", device.Name, message.GetType().Name);
                    await connection.CloseAsync(WebSocketCloseStatus.ProtocolError, "unexpected message").ConfigureAwait(false);
                    return;
            }
        }
    }

    /// <summary>把设备的事件放进通知队列。已撤销的设备、超过频率上限的事件直接丢弃。</summary>
    private void ForwardEvent(DeviceRecord device, NodeConnection connection, AgentEvent agentEvent)
    {
        if (store.FindDevice(device.Id) is null)
        {
            return;
        }

        if (!connection.TryCountEvent(time.GetUtcNow(), MaxEventsPerMinute))
        {
            logger.LogWarning("设备 {Device} 的通知超过每分钟 {Limit} 条，丢弃", device.Name, MaxEventsPerMinute);
            return;
        }

        notifier.Enqueue(ReplyFormatter.AgentEvent(device.Name, agentEvent));
    }

    /// <summary>
    /// 更新"最后在线时间"。这是非关键数据，而且会在连接清理阶段（finally）调用，
    /// 写文件失败（磁盘满、没有权限）时只记日志，不能把异常抛出去拖垮进程。
    /// </summary>
    private void TouchLastSeen(string deviceId)
    {
        var now = time.GetUtcNow();
        var saved = store.UpdateBestEffort(state =>
        {
            var index = state.Devices.FindIndex(d => d.Id == deviceId);
            if (index >= 0)
            {
                state.Devices[index] = state.Devices[index] with { LastSeenAt = now };
            }
        });
        if (!saved)
        {
            logger.LogWarning("保存设备最后在线时间失败（磁盘写入失败），只更新了内存");
        }
    }

    private static string Clip(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}

/// <summary>一台设备的连接：串行发送、按 ID 匹配结果。</summary>
internal sealed class NodeConnection(DeviceRecord device, DeviceSession session, WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandOutcome>> _pending = new(StringComparer.Ordinal);
    private DateTimeOffset _eventWindowStart = DateTimeOffset.MinValue;
    private int _eventCount;

    /// <summary>连接对应的设备。</summary>
    public DeviceRecord Device { get; } = device;

    /// <summary>握手时得到的连接信息。</summary>
    public DeviceSession Session { get; } = session;

    /// <summary>底层 WebSocket（只能由接收循环读取）。</summary>
    public WebSocket Socket { get; } = socket;

    /// <summary>发送一条消息（串行，线程安全）。</summary>
    public Task SendAsync(WireMessage message, CancellationToken cancellationToken) =>
        WebSocketMessaging.SendAsync(Socket, _sendLock, message, cancellationToken);

    /// <summary>发指令并等结果；超时、断线都转成失败结果，不抛异常。</summary>
    public async Task<CommandOutcome> SendCommandAsync(NodeCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<CommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendAsync(new CommandMessage(id, command), cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return CommandOutcome.Failure(ErrorCodes.Timeout, $"设备 {Device.Name} 在 {timeout.TotalSeconds:0} 秒内没有响应");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
        {
            return CommandOutcome.Failure(ErrorCodes.Offline, $"设备 {Device.Name} 的连接断开了");
        }
        finally
        {
            _pending.TryRemove(id, out var _);
        }
    }

    /// <summary>按分钟计数事件，超过 <paramref name="limitPerMinute"/> 返回 false。只在接收循环里调用，不需要加锁。</summary>
    public bool TryCountEvent(DateTimeOffset now, int limitPerMinute)
    {
        if (now - _eventWindowStart >= TimeSpan.FromMinutes(1))
        {
            _eventWindowStart = now;
            _eventCount = 0;
        }

        return ++_eventCount <= limitPerMinute;
    }

    /// <summary>收到结果时唤醒对应的等待方；没人在等（已超时）时忽略。</summary>
    public void Complete(ResultMessage result)
    {
        if (_pending.TryRemove(result.Id, out var completion))
        {
            completion.TrySetResult(result.Outcome);
        }
    }

    /// <summary>连接断开时，让所有还在等结果的指令立即失败。</summary>
    public void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetResult(CommandOutcome.Failure(ErrorCodes.Offline, $"设备 {Device.Name} 的连接断开了"));
            }
        }
    }

    /// <summary>尽量发出关闭帧（让节点知道原因），然后中断连接。</summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        await WebSocketMessaging.CloseQuietlyAsync(Socket, status, reason).ConfigureAwait(false);
        Socket.Abort();
    }
}
