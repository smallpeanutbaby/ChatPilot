using System.Threading.Channels;
using ChatPilot.Server.Chat;
using ChatPilot.Server.Options;
using ChatPilot.Server.State;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Telegram;

/// <summary>
/// Telegram 通道：微信之外的备用聊天通道。长轮询收消息，只处理 <see cref="TelegramOptions.OwnerUserId"/> 私聊发来的文字和图片，
/// 回复发回同一个对话；主动通知和微信一样两边都发。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>offset 先写进状态文件再处理：重启后宁可漏处理也不重复执行指令（和微信通道一样）。</item>
/// <item>收到的消息进入有序队列，由单个后台任务按顺序处理。带说明文字的图片：先暂存图片，再把说明文字当成一条指令执行。</item>
/// <item>token 无效（401）暂停 1 小时；有别的程序在收这个 bot 的消息（409）30 秒后重试；其他失败退避重试。</item>
/// </list>
/// </remarks>
public sealed class TelegramChannel(
    TelegramClient client,
    StateStore store,
    ChatDispatcher dispatcher,
    IOptions<ServerOptions> options,
    TimeProvider time,
    ILogger<TelegramChannel> logger) : BackgroundService, IChatChannel
{
    private static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(30);
    private const int InboxCapacity = 50;
    private const int MaxConsecutiveFailures = 3;

    private readonly Channel<Inbound> _inbox = Channel.CreateBounded<Inbound>(
        new BoundedChannelOptions(InboxCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    // 记过日志的陌生用户：每个只记一次，免得被刷屏。
    private readonly HashSet<long> _ignored = [];

    /// <summary>一条主人的消息：文字，或者图片（可能带说明文字）；都没有表示不支持的消息类型。</summary>
    private sealed record Inbound(string? Text, string? PhotoFileId);

    private TelegramOptions Settings => options.Value.Telegram;

    /// <inheritdoc />
    public string Name => "Telegram";

    /// <inheritdoc />
    public bool IsReady => Settings is { Enabled: true, OwnerUserId: > 0, BotToken.Length: > 0 };

    /// <summary>按 Telegram 的长度限制分段发给主人。</summary>
    public async Task SendToOwnerAsync(string text, CancellationToken cancellationToken)
    {
        if (!IsReady)
        {
            return;
        }

        foreach (var chunk in TextChunker.Split(text, Settings.MaxChunkChars, Settings.MaxChunks))
        {
            await client.SendMessageAsync(Settings.OwnerUserId!.Value, chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsReady)
        {
            return;
        }

        var worker = ProcessInboxAsync(stoppingToken);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (TelegramException ex) when (ex.StatusCode == 401)
            {
                logger.LogError("Telegram bot token 无效，暂停 1 小时后重试。请检查 Telegram:BotToken");
                await DelayAsync(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
            }
            catch (TelegramException ex) when (ex.StatusCode == 409)
            {
                logger.LogError("另一个程序也在收这个 Telegram bot 的消息（或者设置了 webhook），30 秒后重试");
                await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (TelegramException ex)
            {
                failures++;
                logger.LogWarning("拉取 Telegram 消息失败（{Failures}/{Max}）：{Message}", failures, MaxConsecutiveFailures, ex.Message);
                await DelayAsync(failures >= MaxConsecutiveFailures ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                if (failures >= MaxConsecutiveFailures)
                {
                    failures = 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Telegram 通道出问题不能拖垮整个服务器。只记消息：异常里可能带着含 token 的地址。
                logger.LogError("Telegram 通道出错，1 分钟后重试：{Type} {Message}", ex.GetType().Name, ex.Message);
                await DelayAsync(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
        }

        _inbox.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }

    /// <summary>拉一次消息：先保存新的 offset，再把主人的消息放进队列。</summary>
    internal async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        var offset = store.Read(s => s.TelegramOffset);
        var updates = await client.GetUpdatesAsync(offset, LongPoll, stoppingToken).ConfigureAwait(false);
        if (updates.Count == 0)
        {
            return;
        }

        var next = updates.Max(u => u.UpdateId) + 1;
        if (!store.UpdateBestEffort(state => state.TelegramOffset = next))
        {
            logger.LogWarning("保存 Telegram offset 失败（磁盘写入失败），重启后可能重复收到这批消息");
        }

        foreach (var update in updates.OrderBy(u => u.UpdateId))
        {
            if (update.Message is { } message)
            {
                await AcceptAsync(message, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>只认主人在私聊里发的消息。</summary>
    private async Task AcceptAsync(TelegramMessage message, CancellationToken stoppingToken)
    {
        var owner = Settings.OwnerUserId;
        if (message.From?.Id != owner || message.Chat.Id != owner)
        {
            var from = message.From?.Id ?? 0;
            if (_ignored.Count < 100 && _ignored.Add(from))
            {
                // 打印 ID 方便第一次配置 OwnerUserId；陌生人的消息内容不记。
                logger.LogWarning("忽略一条 Telegram 消息：来自用户 {UserId}，不是配置的 OwnerUserId（或者不是私聊）", from);
            }

            return;
        }

        var photo = message.Photo is { Count: > 0 } sizes
            ? sizes.Where(p => p.FileSize is null or <= Shared.Protocol.ProtocolInfo.MaxImageBytes).LastOrDefault()?.FileId
            : null;
        var text = message.Text ?? message.Caption;
        await _inbox.Writer.WriteAsync(new Inbound(string.IsNullOrWhiteSpace(text) ? null : text, photo), stoppingToken).ConfigureAwait(false);
    }

    private async Task ProcessInboxAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _inbox.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (item.PhotoFileId is { } fileId)
                    {
                        await SendToOwnerAsync(await AcceptPhotoAsync(fileId, stoppingToken).ConfigureAwait(false), stoppingToken).ConfigureAwait(false);
                    }

                    if (item.Text is { } text)
                    {
                        await SendToOwnerAsync(await dispatcher.HandleAsync(text, stoppingToken).ConfigureAwait(false), stoppingToken).ConfigureAwait(false);
                    }
                    else if (item.PhotoFileId is null)
                    {
                        await SendToOwnerAsync("目前只支持文字和图片消息。发 /help 查看用法", stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("处理 Telegram 消息失败：{Type} {Message}", ex.GetType().Name, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 服务器正在关闭。
        }
    }

    private async Task<string> AcceptPhotoAsync(string fileId, CancellationToken stoppingToken)
    {
        try
        {
            var data = await client.DownloadFileAsync(fileId, stoppingToken).ConfigureAwait(false);
            return await dispatcher.HandleImageAsync(data, stoppingToken).ConfigureAwait(false);
        }
        catch (TelegramException ex)
        {
            logger.LogWarning("下载 Telegram 图片失败：{Message}", ex.Message);
            return "❌ 图片没收到（下载失败），请再发一次";
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken) => Task.Delay(delay, time, stoppingToken);
}
