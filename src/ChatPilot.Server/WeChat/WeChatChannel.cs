using System.Threading.Channels;
using ChatPilot.Server.Chat;
using ChatPilot.Server.Options;
using ChatPilot.Server.State;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.WeChat;

/// <summary>
/// 微信通道：没绑定时先扫码；绑定后长轮询收消息，只处理主人的文字消息，回复时带上 context_token。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>收到的消息进入有序队列，由单个后台任务按顺序处理，长轮询不会被慢指令卡住，指令也不会乱序。</item>
/// <item>出错时重试：token 失效（-14）暂停 1 小时，连续 3 次失败退避 30 秒，其他失败 2 秒后重试。</item>
/// <item>同步游标和 context_token 写盘失败只影响重启后的恢复，不影响这一批消息的处理。</item>
/// </list>
/// </remarks>
public sealed class WeChatChannel(
    ILinkClient client,
    WeChatLogin login,
    StateStore store,
    ChatDispatcher dispatcher,
    IOptions<ServerOptions> options,
    TimeProvider time,
    ILogger<WeChatChannel> logger) : BackgroundService, IChatChannel
{
    /// <summary>bot token 失效（-14）后暂停多久再试（和腾讯插件一致）。</summary>
    internal static readonly TimeSpan StaleTokenPause = TimeSpan.FromHours(1);
    /// <summary>连续失败 3 次后退避多久。</summary>
    internal static readonly TimeSpan BackoffDelay = TimeSpan.FromSeconds(30);
    /// <summary>普通失败后多久重试。</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxConsecutiveFailures = 3;
    private const int InboxCapacity = 50;

    /// <summary>待处理的主人消息；null 表示收到了不支持的消息类型（图片等）。</summary>
    private readonly Channel<string?> _inbox = Channel.CreateBounded<string?>(
        new BoundedChannelOptions(InboxCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    /// <inheritdoc />
    public string Name => "微信";

    /// <inheritdoc />
    public bool IsReady => store.Read(s => s.WeChat is not null);

    /// <summary>按微信的长度限制分段，带上最近的 context_token 发给主人；还没绑定时什么也不做。</summary>
    public async Task SendToOwnerAsync(string text, CancellationToken cancellationToken)
    {
        if (store.WeChatSnapshot() is not { } account)
        {
            return;
        }

        var settings = options.Value.WeChat;
        foreach (var chunk in TextChunker.Split(text, settings.MaxChunkChars, settings.MaxChunks))
        {
            await client.SendTextAsync(account.BaseUrl, account.BotToken, account.OwnerUserId, chunk, account.ContextToken, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WeChat.Enabled)
        {
            logger.LogInformation("微信通道已关闭");
            return;
        }

        var worker = ProcessInboxAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (store.WeChatSnapshot() is null)
                {
                    logger.LogWarning("微信还没绑定，开始扫码登录");
                    if (!await login.RunAsync(askVerifyCode: null, stoppingToken).ConfigureAwait(false))
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                await PollAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 微信通道出问题不能拖垮整个服务器（设备连接、测试通道还要继续工作）。
                logger.LogError(ex, "微信通道出错，1 分钟后重试");
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken).ConfigureAwait(false);
            }
        }

        await worker.ConfigureAwait(false);
    }

    /// <summary>长轮询主循环；账号被清除时返回。</summary>
    internal async Task PollAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(35);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested && store.WeChatSnapshot() is { } account)
        {
            GetUpdatesResponse response;
            try
            {
                response = await client.GetUpdatesAsync(account.BaseUrl, account.BotToken, account.SyncBuf ?? "", timeout, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (ILinkException ex)
            {
                failures = await BackoffAsync(failures, ex.Message, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (response.LongpollingTimeoutMs is > 0 and < 300_000)
            {
                timeout = TimeSpan.FromMilliseconds(response.LongpollingTimeoutMs.Value);
            }

            if (response.Ret is { } ret and not 0 || response.Errcode is { } code and not 0)
            {
                if (response.Ret == ILinkConstants.StaleTokenErrorCode || response.Errcode == ILinkConstants.StaleTokenErrorCode)
                {
                    logger.LogError("微信 bot token 失效，暂停 1 小时后重试。如果一直失效，请运行 chatpilot-server login 重新绑定");
                    failures = 0;
                    await Task.Delay(StaleTokenPause, time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                failures = await BackoffAsync(failures, $"ret={response.Ret} errcode={response.Errcode} {response.Errmsg}", stoppingToken)
                    .ConfigureAwait(false);
                continue;
            }

            failures = 0;
            if (!string.IsNullOrEmpty(response.GetUpdatesBuf) && response.GetUpdatesBuf != account.SyncBuf)
            {
                // 先记下游标再处理：重启后宁可漏处理也不重复执行指令（比如重复 /new）。
                var saved = store.UpdateBestEffort(state =>
                {
                    if (state.WeChat is { } current)
                    {
                        current.SyncBuf = response.GetUpdatesBuf;
                    }
                });
                if (!saved)
                {
                    logger.LogWarning("保存微信同步游标失败（磁盘写入失败），重启后可能重复收到这批消息");
                }
            }

            foreach (var message in response.Msgs ?? [])
            {
                await AcceptAsync(account, message, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> BackoffAsync(int failures, string reason, CancellationToken stoppingToken)
    {
        failures++;
        logger.LogWarning("拉取微信消息失败（{Failures}/{Max}）：{Reason}", failures, MaxConsecutiveFailures, reason);
        if (failures >= MaxConsecutiveFailures)
        {
            await Task.Delay(BackoffDelay, time, stoppingToken).ConfigureAwait(false);
            return 0;
        }

        await Task.Delay(RetryDelay, time, stoppingToken).ConfigureAwait(false);
        return failures;
    }

    /// <summary>过滤一条消息：只认主人发的用户消息，放进有序队列等后台任务处理。</summary>
    private async Task AcceptAsync(WeChatAccount account, WeixinMessage message, CancellationToken stoppingToken)
    {
        if (message.MessageType != ILinkConstants.MessageTypeUser)
        {
            return;
        }

        if (!string.Equals(message.FromUserId, account.OwnerUserId, StringComparison.Ordinal))
        {
            logger.LogWarning("忽略一条不是主人发来的消息");
            return;
        }

        if (!string.IsNullOrEmpty(message.ContextToken))
        {
            store.UpdateBestEffort(state =>
            {
                if (state.WeChat is { } current)
                {
                    current.ContextToken = message.ContextToken;
                }
            });
        }

        await _inbox.Writer.WriteAsync(ExtractText(message), stoppingToken).ConfigureAwait(false);
    }

    /// <summary>按收到的顺序逐条处理主人的消息并回复。</summary>
    private async Task ProcessInboxAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var text in _inbox.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var reply = text is null
                        ? "目前只支持文字消息（语音会自动转成文字）。发 /help 查看用法"
                        : await dispatcher.HandleAsync(text, stoppingToken).ConfigureAwait(false);
                    await SendToOwnerAsync(reply, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "处理微信消息失败");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 服务器正在关闭。
        }
    }

    /// <summary>取第一段文字；语音消息用微信自带的语音转文字结果。</summary>
    internal static string? ExtractText(WeixinMessage message)
    {
        foreach (var item in message.ItemList ?? [])
        {
            if (item.Type == ILinkConstants.ItemTypeText && !string.IsNullOrWhiteSpace(item.TextItem?.Text))
            {
                return item.TextItem.Text;
            }

            if (item.Type == ILinkConstants.ItemTypeVoice && !string.IsNullOrWhiteSpace(item.VoiceItem?.Text))
            {
                return item.VoiceItem.Text;
            }
        }

        return null;
    }
}
