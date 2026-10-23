using ChatPilot.Server.Chat;
using ChatPilot.Server.Options;
using ChatPilot.Server.State;
using ChatPilot.Shared.Text;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.WeChat;

/// <summary>
/// 微信通道：没绑定时先扫码；绑定后长轮询收消息，只处理主人的文字消息，回复时带上 context_token。
/// 出错时重试：token 失效（-14）暂停 1 小时，连续 3 次失败退避 30 秒，其他失败 2 秒后重试。
/// </summary>
public sealed class WeChatChannel(
    ILinkClient client,
    WeChatLogin login,
    StateStore store,
    ChatDispatcher dispatcher,
    ChatChannelRegistry registry,
    IOptions<ServerOptions> options,
    TimeProvider time,
    ILogger<WeChatChannel> logger) : BackgroundService, IChatChannel
{
    internal static readonly TimeSpan StaleTokenPause = TimeSpan.FromHours(1);
    internal static readonly TimeSpan BackoffDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxConsecutiveFailures = 3;
    private const int MaxConcurrentMessages = 4;

    private readonly SemaphoreSlim _messageSlots = new(MaxConcurrentMessages, MaxConcurrentMessages);

    public string Name => "微信";

    public bool IsReady => store.Read(s => s.WeChat is not null);

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

    public override void Dispose()
    {
        _messageSlots.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WeChat.Enabled)
        {
            logger.LogInformation("微信通道已关闭");
            return;
        }

        registry.Register(this);
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
                store.Update(state =>
                {
                    if (state.WeChat is { } current)
                    {
                        current.SyncBuf = response.GetUpdatesBuf;
                    }
                });
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

    /// <summary>过滤并处理一条消息：只认主人发的用户消息。指令在后台执行，不阻塞长轮询。</summary>
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
            store.Update(state =>
            {
                if (state.WeChat is { } current)
                {
                    current.ContextToken = message.ContextToken;
                }
            });
        }

        var text = ExtractText(message);
        await _messageSlots.WaitAsync(stoppingToken).ConfigureAwait(false);
        _ = Task.Run(async () =>
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
            finally
            {
                _messageSlots.Release();
            }
        }, CancellationToken.None);
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
