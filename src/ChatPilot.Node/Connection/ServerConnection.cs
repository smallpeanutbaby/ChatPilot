using System.Net.WebSockets;
using System.Runtime.InteropServices;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Connection;

/// <summary>同一个设备令牌在别处建立了新连接（通常是运行了两个节点），这个节点应当退出。</summary>
public sealed class NodeReplacedException() : Exception("同一个设备在别处连上了服务器（可能运行了两个节点），这个节点退出");

/// <summary>
/// 节点到服务器的长连接：连接 → 握手 → 收指令 / 发结果和事件；断线后指数退避重连（1 秒到 60 秒）。
/// </summary>
/// <param name="webSocketUri">服务器的 WebSocket 地址，见 <see cref="ServerEndpoint.WebSocketUri"/>。</param>
/// <param name="token">设备令牌。</param>
/// <param name="fingerprint">服务器证书指纹（wss 必填）。</param>
/// <param name="connector">建立连接的方式（测试里换成内存连接）。</param>
/// <param name="events">待上报的 agent 事件；断线期间留在队列里，重连后补发。</param>
/// <param name="execute">执行一条指令并返回结果；抛出的异常会被当成内部错误回给服务器。</param>
/// <param name="logger">日志。</param>
public sealed class ServerConnection(
    Uri webSocketUri,
    string token,
    string? fingerprint,
    IWebSocketConnector connector,
    AgentEventQueue events,
    Func<NodeCommand, CancellationToken, Task<CommandOutcome>> execute,
    ILogger<ServerConnection> logger)
{
    /// <summary>同时执行的指令上限；超过时新指令直接回 <see cref="ErrorCodes.Busy"/>，不排队。</summary>
    private const int MaxConcurrentCommands = 8;

    /// <summary>发出 hello 后等 welcome 最多多久。</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>第一次重连前等多久；握手成功后重置成这个值。</summary>
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>重连等待每次翻倍，最多等这么久。</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>握手成功时触发（测试和日志用）。</summary>
    public event Action<WelcomeMessage>? Connected;

    /// <summary>
    /// 一直运行到取消。返回进程退出码：0 正常退出（包括被另一个实例替换），2 设备令牌被拒绝。
    /// 被替换时以 0 退出，开机自启工具就不会再把它拉起来和另一个实例互相顶替。
    /// </summary>
    /// <param name="cancellationToken">取消时断开连接、停止重连并返回 0。</param>
    /// <returns>进程退出码：0 正常退出或被替换，2 设备令牌被拒绝。</returns>
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
            catch (NodeReplacedException ex)
            {
                logger.LogError("{Message}", ex.Message);
                return 0;
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

    /// <summary>发 hello（协议版本、节点版本、系统、电脑名、网卡），然后在 <see cref="HandshakeTimeout"/> 内等服务器的 welcome。</summary>
    /// <param name="socket">刚建立的连接。</param>
    /// <param name="sendLock">这个连接的发送锁。</param>
    /// <param name="cancellationToken">取消握手。</param>
    /// <returns>服务器的 welcome，带最终的设备 ID 和设备名。</returns>
    /// <exception cref="WireProtocolException">服务器没回 welcome（例如协议版本不一致被关闭）或回了别的消息。</exception>
    private static async Task<WelcomeMessage> HandshakeAsync(WebSocket socket, SemaphoreSlim sendLock, CancellationToken cancellationToken)
    {
        var hello = new HelloMessage(
            ProtocolInfo.Version,
            typeof(ServerConnection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            TextChunker.Truncate($"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}", 100),
            TextChunker.Truncate(Environment.MachineName, 64),
            LocalNetwork.Describe());
        await WebSocketMessaging.SendAsync(socket, sendLock, hello, cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var reply = await WebSocketMessaging.ReceiveAsync(socket, ProtocolInfo.MaxFrameBytes, timeout.Token).ConfigureAwait(false);
        return reply as WelcomeMessage
            ?? throw new WireProtocolException(
                "服务器拒绝了握手：" + (socket.CloseStatusDescription ?? "没有返回 welcome"),
                WebSocketCloseStatus.ProtocolError);
    }

    /// <summary>
    /// 一次连接的会话：后台上报事件，前台逐条收指令、各自并发执行（最多 <see cref="MaxConcurrentCommands"/> 条）。
    /// 不认识的指令单独回错误、不断开；连接关闭或消息不合规时结束，并等所有在执行的指令和事件上报收尾。
    /// </summary>
    /// <param name="socket">握手成功的连接。</param>
    /// <param name="sendLock">这个连接的发送锁，结果和事件共用。</param>
    /// <param name="cancellationToken">取消会话。</param>
    /// <exception cref="NodeReplacedException">服务器以 <see cref="CloseReasons.Replaced"/> 关闭连接。</exception>
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
                catch (WireProtocolException ex) when (UnsupportedCommandId(ex.Payload) is { } unsupportedId)
                {
                    // 服务器比节点新、发来了不认识的指令：单独回错误，不断开连接。
                    await SendResultAsync(socket, sendLock, unsupportedId,
                        CommandOutcome.Failure(ErrorCodes.InvalidArgument, "节点不支持这个指令，请升级节点（在那台电脑上运行 chatpilot-node update）"), session.Token).ConfigureAwait(false);
                    continue;
                }
                catch (WireProtocolException ex)
                {
                    logger.LogWarning("服务器消息不合规：{Message}", ex.Message);
                    await WebSocketMessaging.CloseQuietlyAsync(socket, ex.CloseStatus, ex.Message).ConfigureAwait(false);
                    return;
                }

                if (message is null)
                {
                    if (socket.CloseStatusDescription == CloseReasons.Replaced)
                    {
                        throw new NodeReplacedException();
                    }

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

    /// <summary>
    /// 如果无法解析的消息是一条结构正常、只是 op 不认识的指令，返回它的 id；否则返回 null。
    /// </summary>
    /// <param name="payload">无法解析的原始消息，见 <see cref="WireProtocolException.Payload"/>。</param>
    /// <returns><c>type</c> 是 <c>command</c> 且 <c>id</c> 是 1–64 个字符的字符串时为这个 id；否则（不是合法 JSON、不是对象、缺字段）为 null。</returns>
    internal static string? UnsupportedCommandId(byte[]? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String && type.GetString() == "command"
                && root.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                && id.GetString() is { Length: > 0 and <= 64 } value
                    ? value
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>执行一条指令并回结果；执行抛异常时回 <see cref="ErrorCodes.Internal"/>，会话结束导致的取消不回结果。最后归还并发名额。</summary>
    /// <param name="socket">当前连接。</param>
    /// <param name="sendLock">这个连接的发送锁。</param>
    /// <param name="command">服务器发来的指令。</param>
    /// <param name="inflight">并发名额，调用前已经占了一个，这里负责释放。</param>
    /// <param name="cancellationToken">会话的取消令牌。</param>
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

    /// <summary>回一条指令结果；连接已断时只记调试日志，不抛异常。</summary>
    /// <param name="socket">当前连接。</param>
    /// <param name="sendLock">这个连接的发送锁。</param>
    /// <param name="id">指令 ID，见 <see cref="CommandMessage.Id"/>。</param>
    /// <param name="outcome">执行结果。</param>
    /// <param name="cancellationToken">取消发送。</param>
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
    /// <param name="socket">当前连接；发送失败时中止它，让会话结束、触发重连。</param>
    /// <param name="sendLock">这个连接的发送锁。</param>
    /// <param name="cancellationToken">会话结束时取消。</param>
    private async Task PumpEventsAsync(WebSocket socket, SemaphoreSlim sendLock, CancellationToken cancellationToken)
    {
        try
        {
            var reader = events.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryPeek(out var queued))
                {
                    await WebSocketMessaging.SendAsync(socket, sendLock, new AgentEventMessage(queued.Event), cancellationToken).ConfigureAwait(false);
                    reader.TryRead(out _);
                    try
                    {
                        queued.Delivered?.Invoke();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning("事件已发出，但更新本地记录失败：{Message}", ex.Message);
                    }
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
