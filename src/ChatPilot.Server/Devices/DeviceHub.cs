using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using ChatPilot.Server.Chat;
using ChatPilot.Server.State;
using ChatPilot.Shared;
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
/// <item>转发的事件同时记进对话上下文（<see cref="ConversationState"/>）和任务记录（<see cref="TaskHistory"/>）。</item>
/// </list>
/// </remarks>
/// <param name="store">状态文件：确认设备没被撤销，记录最后在线时间和网卡。</param>
/// <param name="notifier">把 agent 事件推送给主人。</param>
/// <param name="presence">设备上线、下线时通知它。</param>
/// <param name="conversation">转发的事件记进对话上下文。</param>
/// <param name="history">转发的事件记进任务记录。</param>
/// <param name="time">时间来源，测试里可以换成假时钟。</param>
/// <param name="logger">日志。</param>
public sealed class DeviceHub(
    StateStore store,
    Notifier notifier,
    PresenceNotifier presence,
    ConversationState conversation,
    TaskHistory history,
    TimeProvider time,
    ILogger<DeviceHub> logger)
{
    /// <summary>每台设备每分钟最多转发的事件数。</summary>
    internal const int MaxEventsPerMinute = 20;

    /// <summary>连接建立后等节点发 hello 的时间，超时就断开。</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>在线设备的当前连接（设备 ID → 连接），每台设备只保留最新的一个。</summary>
    private readonly ConcurrentDictionary<string, NodeConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>设备当前是否在线。</summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <returns>握手完成、连接还没断开时为 <c>true</c>。</returns>
    public bool IsOnline(string deviceId) => _connections.ContainsKey(deviceId);

    /// <summary>在线设备的连接信息；离线时为 null。</summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <returns>握手时得到的连接信息；设备离线时为 null。</returns>
    public DeviceSession? Session(string deviceId) =>
        _connections.TryGetValue(deviceId, out var connection) ? connection.Session : null;

    /// <summary>给设备发指令并等结果。设备离线、超时都会返回失败结果，不抛异常。</summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <param name="command">要执行的指令。</param>
    /// <param name="timeout">最多等多久。</param>
    /// <param name="cancellationToken">取消等待；取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>节点返回的结果；离线、断线时错误码为 <see cref="ErrorCodes.Offline"/>，超时为 <see cref="ErrorCodes.Timeout"/>。</returns>
    public async Task<CommandOutcome> SendAsync(string deviceId, NodeCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
        {
            return CommandOutcome.Failure(ErrorCodes.Offline, "设备不在线");
        }

        return await connection.SendCommandAsync(command, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>撤销设备后立即断开它的连接。</summary>
    /// <param name="deviceId">被撤销的设备 ID。</param>
    /// <returns>连接关闭后完成的任务；设备本来就不在线时立即完成。</returns>
    public async Task DisconnectAsync(string deviceId)
    {
        if (_connections.TryRemove(deviceId, out var connection))
        {
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, CloseReasons.Revoked).ConfigureAwait(false);
        }
    }

    /// <summary>服务器关闭时主动断开所有设备，避免优雅关闭一直等着这些长连接结束。节点会自动重连。</summary>
    /// <returns>所有连接都关闭后完成的任务。</returns>
    public Task CloseAllAsync() =>
        Task.WhenAll(_connections.Values.Select(c => c.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, CloseReasons.ShuttingDown)));

    /// <summary>处理一个已通过令牌验证的设备连接，直到连接断开。</summary>
    /// <param name="device">令牌对应的设备。</param>
    /// <param name="socket">已升级的 WebSocket。</param>
    /// <param name="cancellationToken">请求中止时取消。</param>
    /// <returns>
    /// 连接结束后完成的任务。握手失败、第一条不是 hello、协议版本不一致、握手期间设备被撤销时直接关闭连接返回。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> 或 <paramref name="socket"/> 为 null。</exception>
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
        TouchLastSeen(device.Id, SanitizeNetworks(hello.Networks));
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

    /// <summary>
    /// 接收循环：结果交给等待方，事件转发给主人。节点发来不合规或不该由节点发的消息时关闭连接；
    /// 节点正常关闭、连接出错或请求中止时返回。
    /// </summary>
    /// <param name="device">连接对应的设备。</param>
    /// <param name="connection">当前连接。</param>
    /// <param name="cancellationToken">请求中止时取消。</param>
    /// <returns>连接结束后完成的任务。</returns>
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
    /// <param name="device">上报事件的设备。</param>
    /// <param name="connection">当前连接，用来统计事件频率。</param>
    /// <param name="agentEvent">节点上报的事件。</param>
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

        conversation.OnEvent(device.Id, device.Name, agentEvent);
        history.Record(device.Name, agentEvent, time.GetUtcNow());
        notifier.Enqueue(ReplyFormatter.AgentEvent(device.Name, agentEvent));
    }

    /// <summary>
    /// 更新"最后在线时间"（上线时顺便记下网卡）。这是非关键数据，而且会在连接清理阶段（finally）调用，
    /// 写文件失败（磁盘满、没有权限）时只记日志，不能把异常抛出去拖垮进程。
    /// </summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <param name="networks">节点报告的网卡；为 null 时保留原来记下的。</param>
    private void TouchLastSeen(string deviceId, IReadOnlyList<NetworkInterfaceInfo>? networks = null)
    {
        var now = time.GetUtcNow();
        var saved = store.UpdateBestEffort(state =>
        {
            var index = state.Devices.FindIndex(d => d.Id == deviceId);
            if (index >= 0)
            {
                var current = state.Devices[index];
                state.Devices[index] = current with { LastSeenAt = now, Networks = networks ?? current.Networks };
            }
        });
        if (!saved)
        {
            logger.LogWarning("保存设备最后在线时间失败（磁盘写入失败），只更新了内存");
        }
    }

    /// <summary>只留格式正确的网卡信息（最多 <see cref="ProtocolInfo.MaxWakeAddresses"/> 块）；节点没报告或一块都不对时返回 null。</summary>
    /// <param name="networks">节点在 hello 里报告的网卡。</param>
    /// <returns>MAC 地址合法、前缀长度在 0–32、地址为空或是 IPv4 的网卡；一块都没有时为 null。</returns>
    internal static IReadOnlyList<NetworkInterfaceInfo>? SanitizeNetworks(IReadOnlyList<NetworkInterfaceInfo>? networks)
    {
        var valid = (networks ?? [])
            .Where(n => n is not null && Validation.IsMacAddress(n.Mac) && n.PrefixLength is >= 0 and <= 32
                && (n.Address is null || (IPAddress.TryParse(n.Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)))
            .Take(ProtocolInfo.MaxWakeAddresses)
            .ToList();
        return valid.Count > 0 ? valid : null;
    }

    /// <summary>截短节点报告的文字，防止超长的电脑名、系统描述撑大日志和回复。</summary>
    /// <param name="value">节点报告的文字。</param>
    /// <param name="max">最多保留的字符数。</param>
    /// <returns>截短后的文字；为 null 或空时为空字符串。</returns>
    private static string Clip(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}

/// <summary>一台设备的连接：串行发送、按 ID 匹配结果。</summary>
/// <param name="device">连接对应的设备。</param>
/// <param name="session">握手时得到的连接信息。</param>
/// <param name="socket">底层 WebSocket。</param>
internal sealed class NodeConnection(DeviceRecord device, DeviceSession session, WebSocket socket)
{
    /// <summary>保证同一时刻只有一条消息在发送：WebSocket 不允许并发发送。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    /// <summary>已发出、还在等结果的指令（指令 ID → 等待方）。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandOutcome>> _pending = new(StringComparer.Ordinal);
    /// <summary>当前事件计数窗口的开始时间。</summary>
    private DateTimeOffset _eventWindowStart = DateTimeOffset.MinValue;
    /// <summary>当前窗口里已经收到的事件数。</summary>
    private int _eventCount;

    /// <summary>连接对应的设备。</summary>
    public DeviceRecord Device { get; } = device;

    /// <summary>握手时得到的连接信息。</summary>
    public DeviceSession Session { get; } = session;

    /// <summary>底层 WebSocket（只能由接收循环读取）。</summary>
    public WebSocket Socket { get; } = socket;

    /// <summary>发送一条消息（串行，线程安全）。</summary>
    /// <param name="message">要发送的消息。</param>
    /// <param name="cancellationToken">取消发送。</param>
    /// <returns>消息发出后完成的任务。</returns>
    public Task SendAsync(WireMessage message, CancellationToken cancellationToken) =>
        WebSocketMessaging.SendAsync(Socket, _sendLock, message, cancellationToken);

    /// <summary>发指令并等结果；超时、断线都转成失败结果，不抛异常。</summary>
    /// <param name="command">要执行的指令。</param>
    /// <param name="timeout">最多等多久。</param>
    /// <param name="cancellationToken">取消等待；取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>节点返回的结果；超时时错误码为 <see cref="ErrorCodes.Timeout"/>，连接断开为 <see cref="ErrorCodes.Offline"/>。</returns>
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
    /// <param name="now">当前时间。</param>
    /// <param name="limitPerMinute">每分钟允许的事件数。</param>
    /// <returns>这个事件还在额度内时为 <c>true</c>，应当丢弃时为 <c>false</c>。</returns>
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
    /// <param name="result">节点返回的结果。</param>
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
    /// <param name="status">关闭状态码。</param>
    /// <param name="reason">关闭原因，例如 <see cref="CloseReasons.Revoked"/>。</param>
    /// <returns>连接中断后完成的任务；发关闭帧失败不会抛出。</returns>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        await WebSocketMessaging.CloseQuietlyAsync(Socket, status, reason).ConfigureAwait(false);
        Socket.Abort();
    }
}
