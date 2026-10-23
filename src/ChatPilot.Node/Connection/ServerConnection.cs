using System.Net.WebSockets;
using System.Runtime.InteropServices;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Connection;

/// <summary>
/// 节点到服务器的长连接：连接 → 握手 → 收指令 / 发结果和事件；断线后指数退避重连（1 秒到 60 秒）。
/// </summary>
public sealed class ServerConnection(
    Uri webSocketUri,
    string token,
    string? fingerprint,
    IWebSocketConnector connector,
    AgentEventQueue events,
    Func<NodeCommand, CancellationToken, Task<CommandOutcome>> execute,
    ILogger<ServerConnection> logger)
{
    private const int MaxConcurrentCommands = 8;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>握手成功时触发（测试和日志用）。</summary>
    public event Action<WelcomeMessage>? Connected;

    /// <summary>一直运行到取消。返回进程退出码：0 正常退出，2 设备令牌被拒绝。</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var backoff = InitialBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = await connector.ConnectAsync(webSocketUri, token, fingerprint, cancellationToken).ConfigureAwait(false);
                using var sendLock = new SemaphoreSlim(1, 1);
                var welcome = await HandshakeAsync(socket, sendLock, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("已连接服务器，设备名 {Device}", welcome.DeviceName);
                Connected?.Invoke(welcome);
                backoff = InitialBackoff;

                await RunSessionAsync(socket, sendLock, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("与服务器的连接断开了");
            }
            catch (NodeUnauthorizedException ex)
            {
                logger.LogError("{Message}", ex.Message);
                return 2;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("连接服务器失败：{Message}", ex.Message);
            }

            var delay = backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            logger.LogInformation("{Seconds:0} 秒后重连", delay.TotalSeconds);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        return 0;
    }

    private static async Task<WelcomeMessage> HandshakeAsync(WebSocket socket, SemaphoreSlim sendLock, CancellationToken cancellationToken)
    {
        var hello = new HelloMessage(
            ProtocolInfo.Version,
            typeof(ServerConnection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            TextChunker.Truncate($"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}", 100),
            TextChunker.Truncate(Environment.MachineName, 64));
        await WebSocketMessaging.SendAsync(socket, sendLock, hello, cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var reply = await WebSocketMessaging.ReceiveAsync(socket, ProtocolInfo.MaxFrameBytes, timeout.Token).ConfigureAwait(false);
        return reply as WelcomeMessage
            ?? throw new WireProtocolException(
                "服务器拒绝了握手：" + (socket.CloseStatusDescription ?? "没有返回 welcome"),
                WebSocketCloseStatus.ProtocolError);
    }

    private async Task RunSessionAsync(WebSocket socket, SemaphoreSlim sendLock, CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var inflight = new SemaphoreSlim(MaxConcurrentCommands, MaxConcurrentCommands);
        var pump = PumpEventsAsync(socket, sendLock, session.Token);
        var handlers = new List<Task>();
        try
        {
            while (true)
            {
                WireMessage? message;
                try
                {
                    message = await WebSocketMessaging.ReceiveAsync(socket, ProtocolInfo.MaxFrameBytes, session.Token).ConfigureAwait(false);
                }
                catch (WireProtocolException ex)
                {
                    logger.LogWarning("服务器消息不合规：{Message}", ex.Message);
                    await WebSocketMessaging.CloseQuietlyAsync(socket, ex.CloseStatus, ex.Message).ConfigureAwait(false);
                    return;
                }

                if (message is null)
                {
                    await WebSocketMessaging.CloseQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
                    return;
                }

                if (message is not CommandMessage command)
                {
                    logger.LogDebug("忽略服务器消息 {Type}", message.GetType().Name);
                    continue;
                }

                if (!inflight.Wait(0))
                {
                    await SendResultAsync(socket, sendLock, command.Id,
                        CommandOutcome.Failure(ErrorCodes.Busy, "节点正忙，稍后再试"), session.Token).ConfigureAwait(false);
                    continue;
                }

                handlers.RemoveAll(t => t.IsCompleted);
                handlers.Add(HandleCommandAsync(socket, sendLock, command, inflight, session.Token));
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(handlers.Append(pump)).ConfigureAwait(false);
        }
    }

    private async Task HandleCommandAsync(WebSocket socket, SemaphoreSlim sendLock, CommandMessage command, SemaphoreSlim inflight, CancellationToken cancellationToken)
    {
        try
        {
            CommandOutcome outcome;
            try
            {
                outcome = await execute(command.Command, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "执行指令出错");
                outcome = CommandOutcome.Failure(ErrorCodes.Internal, "节点内部错误");
            }

            await SendResultAsync(socket, sendLock, command.Id, outcome, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inflight.Release();
        }
    }

    private async Task SendResultAsync(WebSocket socket, SemaphoreSlim sendLock, string id, CommandOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await WebSocketMessaging.SendAsync(socket, sendLock, new ResultMessage(id, outcome), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            logger.LogDebug("结果没发出去（连接已断）：{Message}", ex.Message);
        }
    }

    /// <summary>把待上报事件逐条发出；先看再取，发送失败的事件留在队列里，重连后再发。</summary>
    private async Task PumpEventsAsync(WebSocket socket, SemaphoreSlim sendLock, CancellationToken cancellationToken)
    {
        try
        {
            var reader = events.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryPeek(out var agentEvent))
                {
                    await WebSocketMessaging.SendAsync(socket, sendLock, new AgentEventMessage(agentEvent), cancellationToken).ConfigureAwait(false);
                    reader.TryRead(out _);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 会话结束。
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
        {
            logger.LogDebug("事件发送失败，等重连后补发：{Message}", ex.Message);
            socket.Abort();
        }
    }
}
