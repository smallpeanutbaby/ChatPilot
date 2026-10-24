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
/// <item>晚到超过 <see cref="StaleCommands.MaxDelay"/> 的消息（服务器停机、代理不通时 Telegram 替我们存着的）不执行，告诉主人丢掉了哪些。</item>
/// <item>
/// 收到的消息进入有序队列，由单个后台任务按顺序处理。带说明文字的图片：先暂存图片，再把说明文字当成一条指令执行；
/// 相册（一次发几张）等最后一张到了再一起处理；有图片或文件没收下时不执行说明文字，告诉主人。
/// </item>
/// <item>"按文件发送"的图片（MIME 类型是 image/*）当作图片收下；其他文件、视频、语音明确告诉主人没有处理。</item>
/// <item>token 无效（401）暂停 1 小时；有别的程序在收这个 bot 的消息（409）30 秒后重试；其他失败退避重试。</item>
/// <item>日志里只记异常的类型和说明，不记异常对象：bot token 是请求地址的一部分（S-21）。</item>
/// </list>
/// </remarks>
/// <param name="client">Telegram Bot API 客户端。</param>
/// <param name="store">状态文件，保存长轮询的 offset。</param>
/// <param name="dispatcher">处理主人发来的指令和图片。</param>
/// <param name="options">服务器配置，提供 Telegram 的设置。</param>
/// <param name="time">时间来源，用于失败后的等待，测试里可以换成假时钟。</param>
/// <param name="logger">日志。</param>
public sealed class TelegramChannel(
    TelegramClient client,
    StateStore store,
    ChatDispatcher dispatcher,
    IOptions<ServerOptions> options,
    TimeProvider time,
    ILogger<TelegramChannel> logger) : BackgroundService, IChatChannel
{
    /// <summary>每次长轮询最多等多久。</summary>
    private static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(30);
    /// <summary>最多排队多少条还没处理的消息；满了之后拉取循环等待，不丢消息。</summary>
    private const int InboxCapacity = 50;
    /// <summary>连续失败几次后改成等 30 秒再试（之前每次只等 2 秒）。</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>相册的最后一张到了之后再等多久没有新的，才认为这个相册收齐了。</summary>
    internal static readonly TimeSpan AlbumWait = TimeSpan.FromSeconds(2);

    /// <summary>收到的主人消息。只有一个后台任务在读，保证按发送顺序执行。</summary>
    private readonly Channel<Inbound> _inbox = Channel.CreateBounded<Inbound>(
        new BoundedChannelOptions(InboxCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    // 记过日志的陌生用户：每个只记一次，免得被刷屏。
    /// <summary>已经记过日志的陌生用户 ID，最多记 100 个。</summary>
    private readonly HashSet<long> _ignored = [];

    /// <summary>
    /// 一条主人的消息：文字、图片（可能带说明文字），或者收不下的附件；都没有表示不支持的消息类型。
    /// 也可以只是一条要直接发给主人的说明（<see cref="Notice"/>）。
    /// </summary>
    /// <param name="Text">文字或说明文字；没有或全是空白时为 null。</param>
    /// <param name="ImageFileId">要按图片下载的文件 ID（图片，或者按文件发送的图片）；没有时为 null。</param>
    /// <param name="Problem">附件收不下的原因（不是图片的文件、视频、太大），发给主人；没有问题时为 null。</param>
    /// <param name="MediaGroupId">相册编号；不是相册时为 null。</param>
    /// <param name="Notice">直接发给主人的说明（例如晚到的消息被丢掉了）；有它时其他字段都不用。</param>
    private sealed record Inbound(string? Text, string? ImageFileId, string? Problem = null, string? MediaGroupId = null, string? Notice = null);

    /// <summary>Telegram 相关配置。</summary>
    private TelegramOptions Settings => options.Value.Telegram;

    /// <inheritdoc />
    public string Name => "Telegram";

    /// <inheritdoc />
    public bool IsReady => Settings is { Enabled: true, OwnerUserId: > 0, BotToken.Length: > 0 };

    /// <summary>按 Telegram 的长度限制分段发给主人。</summary>
    /// <param name="text">消息内容。</param>
    /// <param name="cancellationToken">取消发送。</param>
    /// <returns>各段都发完后完成的任务；通道没启用或没配置好时什么都不发。</returns>
    /// <exception cref="TelegramException">某一段发送失败，后面的段不再发。</exception>
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

    /// <summary>
    /// 拉一次消息：先保存新的 offset，再把主人的消息放进队列。晚到太久的不放进去，在这批消息前面放一条说明告诉主人丢掉了哪些。
    /// </summary>
    /// <param name="stoppingToken">服务器停止时取消。</param>
    /// <returns>这批消息都放进队列后完成的任务；队列满时会等待。</returns>
    /// <exception cref="TelegramException">拉取失败。</exception>
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

        var now = time.GetUtcNow();
        var fresh = new List<Inbound>();
        var stale = new List<(DateTimeOffset SentAt, string Summary)>();
        foreach (var update in updates.OrderBy(u => u.UpdateId))
        {
            if (update.Message is not { } message || !IsFromOwner(message))
            {
                continue;
            }

            var inbound = ToInbound(message);
            if (StaleCommands.FromUnixMilliseconds(message.Date * 1000) is { } sentAt && StaleCommands.IsStale(sentAt, now))
            {
                stale.Add((sentAt, StaleCommands.Summarize(inbound.Text, inbound.ImageFileId is not null || inbound.Problem is not null)));
            }
            else
            {
                fresh.Add(inbound);
            }
        }

        if (stale.Count > 0)
        {
            logger.LogWarning("丢掉 {Count} 条晚到超过 {Minutes} 分钟的 Telegram 消息", stale.Count, StaleCommands.MaxDelay.TotalMinutes);
            await _inbox.Writer.WriteAsync(new Inbound(null, null, Notice: StaleCommands.Notice(stale, now)), stoppingToken).ConfigureAwait(false);
        }

        foreach (var inbound in fresh)
        {
            await _inbox.Writer.WriteAsync(inbound, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>只认主人在私聊里发的消息；陌生人第一次发消息时记一条日志（只记用户 ID，不记内容）。</summary>
    /// <param name="message">收到的消息。</param>
    /// <returns>发送人和对话都是配置的 <see cref="TelegramOptions.OwnerUserId"/> 时为 <c>true</c>。</returns>
    private bool IsFromOwner(TelegramMessage message)
    {
        var owner = Settings.OwnerUserId;
        if (message.From?.Id == owner && message.Chat.Id == owner)
        {
            return true;
        }

        var from = message.From?.Id ?? 0;
        if (_ignored.Count < 100 && _ignored.Add(from))
        {
            // 打印 ID 方便第一次配置 OwnerUserId；陌生人的消息内容不记。
            logger.LogWarning("忽略一条 Telegram 消息：来自用户 {UserId}，不是配置的 OwnerUserId（或者不是私聊）", from);
        }

        return false;
    }

    /// <summary>
    /// 整理主人的一条消息：文字优先，没有时用说明文字。图片取不超过大小上限的最大尺寸；按文件发送、MIME 类型是图片的也当作图片；
    /// 其他文件、带说明文字的视频语音、太大的图片记下收不下的原因。
    /// </summary>
    /// <param name="message">主人发来的消息。</param>
    /// <returns>放进队列的消息。</returns>
    private static Inbound ToInbound(TelegramMessage message)
    {
        const int maxBytes = Shared.Protocol.ProtocolInfo.MaxImageBytes;
        var tooBig = $"❌ 图片太大了（上限 {maxBytes / 1024 / 1024} MB），没有收下";
        var text = message.Text ?? message.Caption;
        string? fileId = null;
        string? problem = null;
        if (message.Photo is { Count: > 0 } sizes)
        {
            fileId = sizes.Where(p => p.FileSize is null or <= maxBytes).LastOrDefault()?.FileId;
            problem = fileId is null ? tooBig : null;
        }
        else if (message.Document is { } document)
        {
            if (document.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            {
                fileId = document.FileSize is null or <= maxBytes ? document.FileId : null;
                problem = fileId is null ? tooBig : null;
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(document.FileName) ? "" : $"「{TextChunker.Truncate(document.FileName, 60)}」";
                problem = $"❌ 文件{name}没有处理：目前只支持文字和图片（图片可以直接发，也可以按文件发）";
            }
        }
        else if (message.Caption is not null)
        {
            // 只有带附件的消息才有说明文字：这里是视频、语音这类还不支持的附件。
            problem = "❌ 这种附件（视频、语音等）还不支持，没有处理：目前只支持文字和图片";
        }

        return new Inbound(string.IsNullOrWhiteSpace(text) ? null : text, fileId, problem, message.MediaGroupId);
    }

    /// <summary>
    /// 后台任务：按顺序处理队列里的消息，把回复发回给主人。相册的几张图片等收齐了（最后一张之后 <see cref="AlbumWait"/> 内没有新的）
    /// 再一起处理；单条消息出错只记日志（不记内容），不影响后面的消息。
    /// </summary>
    /// <param name="stoppingToken">服务器停止时取消。</param>
    /// <returns>队列关闭或服务器停止后完成的任务。</returns>
    private async Task ProcessInboxAsync(CancellationToken stoppingToken)
    {
        try
        {
            Inbound? pending = null;
            while ((pending ?? await NextAsync(stoppingToken).ConfigureAwait(false)) is { } first)
            {
                pending = null;
                var batch = new List<Inbound> { first };
                if (first.MediaGroupId is { } album)
                {
                    while (await NextWithinAsync(AlbumWait, stoppingToken).ConfigureAwait(false) is { } more)
                    {
                        if (more.MediaGroupId != album)
                        {
                            pending = more;
                            break;
                        }

                        batch.Add(more);
                    }
                }

                try
                {
                    await ProcessAsync(batch, stoppingToken).ConfigureAwait(false);
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

    /// <summary>等队列里的下一条消息。</summary>
    /// <param name="stoppingToken">服务器停止时取消。</param>
    /// <returns>下一条消息；队列已经关闭并且取完时为 null。</returns>
    private async Task<Inbound?> NextAsync(CancellationToken stoppingToken)
    {
        while (await _inbox.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            if (_inbox.Reader.TryRead(out var item))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>最多等 <paramref name="wait"/>，看队列里有没有下一条消息（按注入的时间来源计时）。</summary>
    /// <param name="wait">最多等多久。</param>
    /// <param name="stoppingToken">服务器停止时取消。</param>
    /// <returns>下一条消息；等不到或者队列已经关闭时为 null。</returns>
    private async Task<Inbound?> NextWithinAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        if (_inbox.Reader.TryRead(out var ready))
        {
            return ready;
        }

        using var timeout = new CancellationTokenSource(wait, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        try
        {
            return await NextAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// 处理一条消息，或者一个相册的几条：先把图片都暂存下来，再执行说明文字（相册里有几条说明文字时按顺序连起来）。
    /// 有图片没下载下来、没暂存下来，或者有收不下的附件时，不执行说明文字：agent 拿不到图片就去干活，多半会干错。
    /// </summary>
    /// <param name="batch">要一起处理的消息，按收到的顺序。</param>
    /// <param name="stoppingToken">服务器停止时取消。</param>
    /// <returns>回复都发出后完成的任务。</returns>
    /// <exception cref="TelegramException">回复发送失败。</exception>
    private async Task ProcessAsync(IReadOnlyList<Inbound> batch, CancellationToken stoppingToken)
    {
        if (batch is [{ Notice: { } notice }])
        {
            await SendToOwnerAsync(notice, stoppingToken).ConfigureAwait(false);
            return;
        }

        var problems = new List<string>();
        string? staged = null;
        foreach (var item in batch)
        {
            if (item.Problem is { } problem)
            {
                problems.Add(problem);
            }
            else if (item.ImageFileId is { } fileId)
            {
                var (ok, reply) = await AcceptPhotoAsync(fileId, stoppingToken).ConfigureAwait(false);
                if (ok)
                {
                    staged = reply;
                }
                else
                {
                    problems.Add(reply);
                }
            }
        }

        if (staged is not null)
        {
            await SendToOwnerAsync(staged, stoppingToken).ConfigureAwait(false);
        }

        var texts = batch.Select(i => i.Text).OfType<string>().ToList();
        var text = texts.Count == 0 ? null : string.Join('\n', texts);
        if (problems.Count > 0)
        {
            var failed = string.Join('\n', problems.Distinct(StringComparer.Ordinal));
            var kept = staged is null ? "" : "收下的图片还暂存着，不要了发 /drop；";
            await SendToOwnerAsync(
                text is null
                    ? failed
                    : $"{failed}\n\n⚠️ 附件没有收全，说明文字里的指令没有执行：{TextChunker.Truncate(text.ReplaceLineEndings(" "), 200)}\n{kept}补发之后再把指令发一次",
                stoppingToken).ConfigureAwait(false);
            return;
        }

        if (text is not null)
        {
            await SendToOwnerAsync(await dispatcher.HandleAsync(text, stoppingToken).ConfigureAwait(false), stoppingToken).ConfigureAwait(false);
        }
        else if (staged is null)
        {
            await SendToOwnerAsync("目前只支持文字和图片消息。发 /help 查看用法", stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>下载图片并交给指令分发器暂存。</summary>
    /// <param name="fileId">图片的文件 ID。</param>
    /// <param name="stoppingToken">服务器停止时取消。</param>
    /// <returns>是否暂存下来，以及要回复主人的文字：暂存结果，或者下载失败、不是图片、已满的说明。</returns>
    private async Task<(bool Staged, string Reply)> AcceptPhotoAsync(string fileId, CancellationToken stoppingToken)
    {
        try
        {
            var data = await client.DownloadFileAsync(fileId, stoppingToken).ConfigureAwait(false);
            return await dispatcher.StageImageAsync(data, stoppingToken).ConfigureAwait(false);
        }
        catch (TelegramException ex)
        {
            logger.LogWarning("下载 Telegram 图片失败：{Message}", ex.Message);
            return (false, "❌ 图片没收到（下载失败），请再发一次");
        }
    }

    /// <summary>按注入的时间来源等待，测试里可以快进。</summary>
    /// <param name="delay">等多久。</param>
    /// <param name="stoppingToken">服务器停止时取消。</param>
    /// <returns>等够时间后完成的任务；被取消时抛出 <see cref="OperationCanceledException"/>。</returns>
    private Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken) => Task.Delay(delay, time, stoppingToken);
}
