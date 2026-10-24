using System.Text.Json;
using ChatPilot.Server.Chat;
using ChatPilot.Server.Devices;
using ChatPilot.Server.Options;
using ChatPilot.Server.Security;
using ChatPilot.Server.State;
using ChatPilot.Server.WeChat;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ChatPilot.Tests.Integration;

/// <summary>微信扫码绑定（F-30、F-31）：二维码刷新、IDC 重定向、验证码、只信任腾讯域名、登录链接不进日志。</summary>
public sealed class WeChatLoginTests : IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly FakeILink _ilink = new();
    private readonly FakeTimeProvider _time = new();
    private readonly StateStore _store;
    private readonly WeChatLogin _login;
    private readonly List<string> _shown = [];
    private readonly CapturingLoggerProvider _logs = new();

    public WeChatLoginTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { DataDir = _data.Path });
        _store = new StateStore(_data.Path);
        var logger = LoggerFactory.Create(builder => builder.AddProvider(_logs)).CreateLogger<WeChatLogin>();
        _login = new WeChatLogin(new ILinkClient(new HttpClient(_ilink), options), _store, options, _time, logger)
        {
            ShowQrCode = (ascii, png, link) => _shown.Add(png),
        };
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Handles_refresh_redirect_and_verify_code_then_saves_owner()
    {
        // F-30 / F-31
        foreach (var status in new object[]
        {
            new { status = "wait" },
            new { status = "scaned" },
            new { status = "expired" },
            new { status = "scaned_but_redirect", redirect_host = "sh.ilinkai.weixin.qq.com" },
            new { status = "need_verifycode" },
            new { status = "scaned" },
            new { status = "confirmed", bot_token = "bot-token", ilink_bot_id = "bot1", baseurl = "https://ilinkai.weixin.qq.com", ilink_user_id = "owner@im.wechat" },
        })
        {
            _ilink.QrStatuses.Enqueue(status);
        }

        var asked = new List<string>();
        var ok = await RunWithPumpAsync(() => _login.RunAsync(prompt =>
        {
            asked.Add(prompt);
            return Task.FromResult<string?>(" 123456 ");
        }, CancellationToken.None));

        Assert.True(ok);
        Assert.Equal(2, _ilink.Count("/ilink/bot/get_bot_qrcode"));
        Assert.Equal(2, _shown.Count);
        Assert.True(File.Exists(_shown[0]));
        Assert.Single(asked);

        var polls = _ilink.Requests.Where(r => r.Uri.AbsolutePath == "/ilink/bot/get_qrcode_status").ToList();
        Assert.Equal("ilinkai.weixin.qq.com", polls[0].Uri.Host);
        Assert.Equal("sh.ilinkai.weixin.qq.com", polls[^1].Uri.Host);
        Assert.Contains(polls, p => p.Uri.Query.Contains("verify_code=123456", StringComparison.Ordinal));
        Assert.Contains("qrcode=qr-2", polls[^1].Uri.Query, StringComparison.Ordinal);

        var account = _store.WeChatSnapshot();
        Assert.NotNull(account);
        Assert.Equal(("bot1", "bot-token", "owner@im.wechat"), (account.BotId, account.BotToken, account.OwnerUserId));
    }

    [Fact]
    public async Task Untrusted_redirect_and_baseurl_are_ignored()
    {
        _ilink.QrStatuses.Enqueue(new { status = "scaned_but_redirect", redirect_host = "evil.example.com" });
        _ilink.QrStatuses.Enqueue(new { status = "confirmed", bot_token = "t", ilink_bot_id = "b", baseurl = "https://evil.example.com", ilink_user_id = "o" });
        Assert.True(await RunWithPumpAsync(() => _login.RunAsync(null, CancellationToken.None)));
        Assert.DoesNotContain(_ilink.Requests, r => r.Uri.Host.Contains("evil", StringComparison.Ordinal));
        Assert.Equal("https://ilinkai.weixin.qq.com", _store.WeChatSnapshot()?.BaseUrl);
    }

    [Fact]
    public async Task Gives_up_after_three_expired_codes_and_when_verify_code_is_needed_non_interactively()
    {
        for (var i = 0; i < 3; i++)
        {
            _ilink.QrStatuses.Enqueue(new { status = "expired" });
        }

        Assert.False(await RunWithPumpAsync(() => _login.RunAsync(null, CancellationToken.None)));
        Assert.Equal(3, _ilink.Count("/ilink/bot/get_bot_qrcode"));

        _ilink.QrStatuses.Enqueue(new { status = "need_verifycode" });
        Assert.False(await RunWithPumpAsync(() => _login.RunAsync(null, CancellationToken.None)));
        Assert.Null(_store.WeChatSnapshot());
    }

    [Fact]
    public async Task Login_link_is_never_written_to_logs()
    {
        // 审查 #15：登录链接等于"成为主人"的凭证，后台模式下不能出现在日志里。
        _ilink.QrStatuses.Enqueue(new { status = "confirmed", bot_token = "t", ilink_bot_id = "b", ilink_user_id = "o" });
        Assert.True(await RunWithPumpAsync(() => _login.RunAsync(null, CancellationToken.None)));
        Assert.DoesNotContain("liteapp.weixin.qq.com", _logs.AllText, StringComparison.Ordinal);
        Assert.Contains("wechat-login.png", _logs.AllText, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_data.Path, "wechat-login.png")));
        }
    }

    [Fact]
    public async Task Requests_carry_ilink_headers()
    {
        _ilink.QrStatuses.Enqueue(new { status = "confirmed", bot_token = "t", ilink_bot_id = "b", ilink_user_id = "o" });
        await RunWithPumpAsync(() => _login.RunAsync(null, CancellationToken.None));
        var qr = _ilink.Requests.First();
        Assert.Equal("bot", qr.Headers["iLink-App-Id"]);
        Assert.Equal("132106", qr.Headers["iLink-App-ClientVersion"]);
        Assert.Equal("ilink_bot_token", qr.Headers["AuthorizationType"]);
        Assert.Contains("bot_type=3", qr.Uri.Query, StringComparison.Ordinal);
    }

    /// <summary>登录流程每次轮询间隔 1 秒（用的是模拟时间），后台不断推进时间。</summary>
    private async Task<T> RunWithPumpAsync<T>(Func<Task<T>> action)
    {
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(2, CancellationToken.None);
            }
        });
        try
        {
            return await action().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await stop.CancelAsync();
            await pump;
        }
    }
}

/// <summary>微信长轮询通道：在完整的服务器里运行（iLink 用模拟的）。</summary>
public sealed class WeChatChannelTests : IDisposable
{
    private readonly TempDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Owner_messages_are_answered_with_context_token_and_strangers_are_ignored()
    {
        // F-32 / F-33 / S-01
        SeedAccount("buf0");
        var ilink = new FakeILink();
        await using (var factory = CreateFactory(ilink))
        {
            using var client = factory.CreateClient();
            await ilink.Updates.Writer.WriteAsync(new
            {
                ret = 0,
                get_updates_buf = "buf1",
                msgs = new[]
                {
                    FakeILink.Message("stranger@im.wechat", "/pair", "ctx-stranger"),
                    FakeILink.Message("owner@im.wechat", "/help", "ctx-owner"),
                    FakeILink.Message("owner@im.wechat", "echo of bot", "ctx-bot", type: 2),
                },
            });

            await DevChat.EventuallyAsync(() => Task.FromResult(ilink.Sent.Count >= 1), "主人的消息应该得到回复");
            await Task.Delay(300);
            var sent = Assert.Single(ilink.Sent);
            var msg = sent.Json.GetProperty("msg");
            Assert.Equal("owner@im.wechat", msg.GetProperty("to_user_id").GetString());
            Assert.Equal("ctx-owner", msg.GetProperty("context_token").GetString());
            Assert.Equal(2, msg.GetProperty("message_type").GetInt32());
            Assert.Contains("ChatPilot 指令", msg.GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString(), StringComparison.Ordinal);

            // 真机发现：中文和标点转义成 \uXXXX 发出去，微信里的消息会在中文引号处被截断。要和腾讯官方插件一样原样发 UTF-8。
            Assert.Contains("ChatPilot 指令", sent.Body, StringComparison.Ordinal);
            Assert.Equal("Bearer bot-token", sent.Headers["Authorization"]);
            Assert.StartsWith("ChatPilot/", sent.Json.GetProperty("base_info").GetProperty("bot_agent").GetString(), StringComparison.Ordinal);

            var store = factory.Services.GetRequiredService<StateStore>();
            Assert.Equal("buf1", store.WeChatSnapshot()?.SyncBuf);
            Assert.Equal("ctx-owner", store.WeChatSnapshot()?.ContextToken);
            Assert.Equal("buf0", ilink.Requests.First(r => r.Uri.AbsolutePath == "/ilink/bot/getupdates").Json.GetProperty("get_updates_buf").GetString());
        }

        // 重启后从保存的游标继续。
        var restarted = new FakeILink();
        await using (var factory = CreateFactory(restarted))
        {
            using var client = factory.CreateClient();
            await DevChat.EventuallyAsync(() => Task.FromResult(restarted.Count("/ilink/bot/getupdates") >= 1), "重启后应该继续拉取");
            Assert.Equal("buf1", restarted.Requests.First(r => r.Uri.AbsolutePath == "/ilink/bot/getupdates").Json.GetProperty("get_updates_buf").GetString());
        }
    }

    [Fact]
    public async Task Messages_in_one_batch_are_handled_strictly_in_order()
    {
        // 审查 #1：先发的指令先执行、先回复，不会并发乱序。
        SeedAccount("buf0");
        var ilink = new FakeILink();
        await using var factory = CreateFactory(ilink);
        using var client = factory.CreateClient();
        await ilink.Updates.Writer.WriteAsync(new
        {
            ret = 0,
            get_updates_buf = "buf1",
            msgs = new[]
            {
                FakeILink.Message("owner@im.wechat", "/ls", "c1"),
                FakeILink.Message("owner@im.wechat", "/help", "c2"),
                FakeILink.Message("owner@im.wechat", "/devices", "c3"),
                FakeILink.Message("owner@im.wechat", "/nope", "c4"),
            },
        });

        await DevChat.EventuallyAsync(() => Task.FromResult(ilink.Sent.Count >= 4), "四条消息都应该得到回复");
        var texts = ilink.Sent.Select(r => r.Json.GetProperty("msg").GetProperty("item_list")[0].GetProperty("text_item").GetProperty("text").GetString() ?? "").ToList();
        Assert.StartsWith("还没有设备", texts[0], StringComparison.Ordinal);
        Assert.Contains("ChatPilot 指令", texts[1], StringComparison.Ordinal);
        Assert.StartsWith("还没有设备", texts[2], StringComparison.Ordinal);
        Assert.StartsWith("❓ 不认识的指令", texts[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notifications_are_pushed_to_wechat_using_latest_context_token()
    {
        SeedAccount("buf0", contextToken: "ctx-last");
        var ilink = new FakeILink();
        await using var factory = CreateFactory(ilink);
        using var client = factory.CreateClient();
        await DevChat.EventuallyAsync(() => Task.FromResult(factory.Services.GetRequiredService<WeChatChannel>().IsReady), "微信通道应该就绪");
        await factory.Services.GetRequiredService<Notifier>().BroadcastAsync("✅ [mac] tideq 做完了", CancellationToken.None);
        var msg = Assert.Single(ilink.Sent).Json.GetProperty("msg");
        Assert.Equal("ctx-last", msg.GetProperty("context_token").GetString());
    }

    [Fact]
    public async Task Stale_token_pauses_for_an_hour_and_repeated_failures_back_off()
    {
        // F-34
        SeedAccount("buf0");
        var ilink = new FakeILink();
        var time = new FakeTimeProvider();
        var channel = CreateChannel(ilink, time);
        using var stop = new CancellationTokenSource();
        var poll = channel.PollAsync(stop.Token);

        await ilink.Updates.Writer.WriteAsync(new { ret = -14, errmsg = "session timeout" });
        await WaitForPollsAsync(ilink, 2 - 1);
        await Task.Delay(200);
        time.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(200);
        Assert.Equal(1, ilink.Count("/ilink/bot/getupdates"));
        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForPollsAsync(ilink, 2);

        // 连续 3 次失败：前两次各等 2 秒，第三次后等 30 秒。
        for (var i = 0; i < 3; i++)
        {
            await ilink.Updates.Writer.WriteAsync(new { ret = -1, errmsg = "busy" });
        }

        await Task.Delay(200);
        time.Advance(WeChatChannel.RetryDelay);
        await WaitForPollsAsync(ilink, 3);
        await Task.Delay(200);
        time.Advance(WeChatChannel.RetryDelay);
        await WaitForPollsAsync(ilink, 4);
        await Task.Delay(200);
        time.Advance(WeChatChannel.RetryDelay);
        await Task.Delay(200);
        Assert.Equal(4, ilink.Count("/ilink/bot/getupdates"));
        time.Advance(WeChatChannel.BackoffDelay);
        await WaitForPollsAsync(ilink, 5);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
    }

    [Fact]
    public void Message_ids_are_parsed_without_losing_precision_and_voice_text_is_used()
    {
        // F-35
        const string json = """{"ret":0,"msgs":[{"message_id":18446744073709551615,"from_user_id":"o","message_type":1,"item_list":[{"type":3,"voice_item":{"text":"看看谁闲着"}}]},{"message_id":"123","item_list":[{"type":2}]}]}""";
        var response = JsonSerializer.Deserialize<GetUpdatesResponse>(json, ILinkConstants.Json);
        Assert.NotNull(response?.Msgs);
        Assert.Equal(ulong.MaxValue, response.Msgs[0].MessageId);
        Assert.Equal(123UL, response.Msgs[1].MessageId);
        Assert.Equal("看看谁闲着", WeChatChannel.ExtractText(response.Msgs[0]));
        Assert.Null(WeChatChannel.ExtractText(response.Msgs[1]));
    }

    private void SeedAccount(string syncBuf, string? contextToken = null)
    {
        new StateStore(_data.Path).Update(state => state.WeChat = new WeChatAccount
        {
            BotId = "bot1",
            BotToken = "bot-token",
            BaseUrl = "https://ilinkai.weixin.qq.com",
            OwnerUserId = "owner@im.wechat",
            SyncBuf = syncBuf,
            ContextToken = contextToken,
        });
    }

    private ChatPilotFactory CreateFactory(FakeILink ilink)
    {
        var factory = new ChatPilotFactory();
        factory.Settings["ChatPilot:DataDir"] = _data.Path;
        factory.Settings["ChatPilot:WeChat:Enabled"] = "true";
        factory.ConfigureServicesHook = services =>
        {
            services.RemoveAll<ILinkClient>();
            services.AddSingleton(sp => new ILinkClient(new HttpClient(ilink), sp.GetRequiredService<IOptions<ServerOptions>>()));
        };
        return factory;
    }

    private WeChatChannel CreateChannel(FakeILink ilink, TimeProvider time)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { DataDir = _data.Path, Tls = new TlsOptions { Enabled = false } });
        var store = new StateStore(_data.Path);
        var client = new ILinkClient(new HttpClient(ilink), options);
        var registry = new ChatChannelRegistry();
        var notifier = new Notifier(registry, NullLogger<Notifier>.Instance);
        var presence = new PresenceNotifier(store, notifier, time, options, NullLogger<PresenceNotifier>.Instance);
        var hub = new DeviceHub(store, notifier, presence, time, NullLogger<DeviceHub>.Instance);
        var certificates = new CertificateProvider(options, NullLogger<CertificateProvider>.Instance);
        var dispatcher = new ChatDispatcher(hub, store, new PairingService(store, time, options), new ServerIdentity(options, certificates), options, NullLogger<ChatDispatcher>.Instance);
        var login = new WeChatLogin(client, store, options, time, NullLogger<WeChatLogin>.Instance);
        return new WeChatChannel(client, login, store, dispatcher, options, time, NullLogger<WeChatChannel>.Instance);
    }

    private static Task WaitForPollsAsync(FakeILink ilink, int count) =>
        DevChat.EventuallyAsync(() => Task.FromResult(ilink.Count("/ilink/bot/getupdates") >= count), $"应该至少拉取 {count} 次");
}
