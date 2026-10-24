using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ChatPilot.Server.Chat;
using ChatPilot.Server.Options;
using ChatPilot.Server.State;
using ChatPilot.Server.Telegram;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ChatPilot.Tests.Integration;

/// <summary>Telegram 备用通道（F-53、S-21）：只认主人、offset 先保存再处理、图片带说明文字、通知两边都发、token 不进日志。</summary>
public sealed class TelegramTests : IAsyncLifetime
{
    private const string Token = "123456:test-token_abcdefghijklmnopqrstuvwxyz";
    private const long Owner = 1001;

    private readonly FakeTelegram _telegram = new();
    private readonly ChatPilotFactory _factory;

    public TelegramTests()
    {
        _factory = new ChatPilotFactory();
        _factory.Settings["ChatPilot:Telegram:Enabled"] = "true";
        _factory.Settings["ChatPilot:Telegram:BotToken"] = Token;
        _factory.Settings["ChatPilot:Telegram:OwnerUserId"] = Owner.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _factory.Settings["ChatPilot:Telegram:ApiBaseUrl"] = "https://api.telegram.test";
        _factory.ConfigureServicesHook = services =>
        {
            services.RemoveAll<TelegramClient>();
            services.AddSingleton(sp => new TelegramClient(new HttpClient(_telegram), sp.GetRequiredService<IOptions<ServerOptions>>()));
        };
    }

    public Task InitializeAsync()
    {
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Only_the_owner_is_answered_and_the_offset_is_saved()
    {
        await _telegram.Updates.Writer.WriteAsync(new object[]
        {
            Update(10, from: 424242, text: "/pair"),
            Update(11, from: Owner, text: "/help"),
        });

        await DevChat.EventuallyAsync(() => Task.FromResult(_telegram.Sent.Count >= 1), "主人的消息应该得到回复");
        await Task.Delay(300);
        var (chat, text) = Assert.Single(_telegram.Sent);
        Assert.Equal(Owner, chat);
        Assert.Contains("ChatPilot 指令", text, StringComparison.Ordinal);
        Assert.Equal(12, _factory.Services.GetRequiredService<StateStore>().Read(s => s.TelegramOffset));
        await DevChat.EventuallyAsync(() => Task.FromResult(_telegram.Offsets.Contains(12)), "下一次拉取应该带上新的 offset");

        var logs = _factory.Logs.AllText;
        Assert.Contains("424242", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Photo_is_staged_and_its_caption_runs_as_a_command()
    {
        _telegram.Files["photos/p1.png"] = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1];
        await _telegram.Updates.Writer.WriteAsync(new object[] { Update(20, from: Owner, photo: "p1", caption: "/drop") });

        await DevChat.EventuallyAsync(() => Task.FromResult(_telegram.Sent.Count >= 2), "图片和说明文字都应该得到回复");
        var texts = _telegram.Sent.Select(s => s.Text).ToList();
        Assert.StartsWith("🖼 收到图片（共 1 张待发）", texts[0], StringComparison.Ordinal);
        Assert.Equal("🗑 丢掉了 1 张暂存的图片", texts[1]);
    }

    [Fact]
    public async Task Messages_that_arrive_too_late_are_reported_instead_of_run()
    {
        // 审查 #3：Telegram 替我们存着停机、代理不通期间的消息；晚了超过 10 分钟的不执行，告诉主人丢掉了什么。
        await _telegram.Updates.Writer.WriteAsync(new object[]
        {
            Update(30, from: Owner, text: "/pair 旧的", sentAt: DateTimeOffset.UtcNow.AddMinutes(-30)),
            Update(31, from: Owner, text: "/help"),
        });

        await WaitForRepliesAsync(2, "说明和新消息的回复都应该发出");
        var texts = _telegram.Sent.Select(s => s.Text).ToList();
        Assert.StartsWith("⏰ 有 1 条消息晚到了（最早的是 30 分钟前发的", texts[0], StringComparison.Ordinal);
        Assert.Contains("· /pair 旧的", texts[0], StringComparison.Ordinal);
        Assert.Contains("ChatPilot 指令", texts[1], StringComparison.Ordinal);
        Assert.DoesNotContain(texts, t => t.Contains("🔑 配对码：", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Album_photos_are_all_staged_before_the_caption_runs()
    {
        // 审查 #5：相册的说明文字在第一张上；要等几张都收下了再执行，第二批晚一点到也算。
        _telegram.Files["photos/a1.png"] = Png;
        _telegram.Files["photos/a2.png"] = Png;
        _telegram.Files["photos/a3.png"] = Png;
        await _telegram.Updates.Writer.WriteAsync(new object[]
        {
            Update(40, from: Owner, photo: "a1", caption: "/pending", album: "g1"),
            Update(41, from: Owner, photo: "a2", album: "g1"),
        });
        await Task.Delay(300);
        await _telegram.Updates.Writer.WriteAsync(new object[] { Update(42, from: Owner, photo: "a3", album: "g1") });

        await WaitForRepliesAsync(2, "图片和说明文字都应该得到回复");
        var texts = _telegram.Sent.Select(s => s.Text).ToList();
        Assert.StartsWith("🖼 收到图片（共 3 张待发）", texts[0], StringComparison.Ordinal);
        Assert.Contains("🖼 暂存的图片 3 张", texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caption_does_not_run_when_its_photo_was_not_received()
    {
        // 审查 #5：图片没下载下来时不执行说明文字，告诉主人。
        await _telegram.Updates.Writer.WriteAsync(new object[] { Update(50, from: Owner, photo: "missing", caption: "/pending") });

        await WaitForRepliesAsync(1, "应该告诉主人图片没收到");
        await Task.Delay(300);
        var text = Assert.Single(_telegram.Sent).Text;
        Assert.StartsWith("❌ 图片没收到（下载失败）", text, StringComparison.Ordinal);
        Assert.Contains("⚠️ 附件没有收全，说明文字里的指令没有执行：/pending", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_sent_as_files_are_accepted_and_other_files_are_rejected_out_loud()
    {
        // 审查 #5："按文件发送"的图片当作图片；别的文件明确说没处理，说明文字也不执行。
        _telegram.Files["photos/doc1.png"] = Png;
        await _telegram.Updates.Writer.WriteAsync(new object[]
        {
            Update(60, from: Owner, caption: "/drop", document: new { file_id = "doc1", file_name = "shot.png", mime_type = "image/png", file_size = 9 }),
            Update(61, from: Owner, caption: "/pending", document: new { file_id = "log1", file_name = "app.log", mime_type = "text/plain", file_size = 9 }),
        });

        await WaitForRepliesAsync(3, "每条消息都应该得到回复");
        var texts = _telegram.Sent.Select(s => s.Text).ToList();
        Assert.StartsWith("🖼 收到图片（共 1 张待发）", texts[0], StringComparison.Ordinal);
        Assert.Equal("🗑 丢掉了 1 张暂存的图片", texts[1]);
        Assert.StartsWith("❌ 文件「app.log」没有处理", texts[2], StringComparison.Ordinal);
        Assert.Contains("说明文字里的指令没有执行：/pending", texts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notifications_go_to_telegram_too()
    {
        await _factory.Services.GetRequiredService<Notifier>().BroadcastAsync("✅ [mac] tideq 做完了", CancellationToken.None);
        Assert.Contains(_telegram.Sent, s => s.Text == "✅ [mac] tideq 做完了");
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Empty(new TelegramOptions().Validate());
        var errors = new TelegramOptions { Enabled = true, BotToken = "bad token" }.Validate();
        Assert.Contains(errors, e => e.Contains("BotToken", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("OwnerUserId", StringComparison.Ordinal));
        Assert.Contains(new TelegramOptions { Enabled = true, BotToken = Token, OwnerUserId = 1, Proxy = "ftp://x" }.Validate(), e => e.Contains("Proxy", StringComparison.Ordinal));
        Assert.Empty(new TelegramOptions { Enabled = true, BotToken = Token, OwnerUserId = 1, Proxy = "socks5://127.0.0.1:1080" }.Validate());

        // 审查 #11：空白的代理当作没配置；写错的代理在启用时给出清楚的说明，没启用时不影响启动。
        Assert.Empty(new TelegramOptions { Enabled = true, BotToken = Token, OwnerUserId = 1, Proxy = "  " }.Validate());
        Assert.Contains(new TelegramOptions { Enabled = true, BotToken = Token, OwnerUserId = 1, Proxy = "127.0.0.1:7890" }.Validate(), e => e.Contains("Telegram:Proxy", StringComparison.Ordinal));
        Assert.Empty(new TelegramOptions { Proxy = "not a proxy" }.Validate());
        TelegramClient.CreateHttpClient(new TelegramOptions { Proxy = "not a proxy" }).Dispose();
        var error = Assert.Throws<InvalidOperationException>(() => TelegramClient.CreateHttpClient(new TelegramOptions { Enabled = true, Proxy = "not a proxy" }));
        Assert.Contains("Telegram:Proxy", error.Message, StringComparison.Ordinal);
    }

    private static object Update(
        long id, long from, string? text = null, string? photo = null, string? caption = null,
        DateTimeOffset? sentAt = null, string? album = null, object? document = null) => new
    {
        update_id = id,
        message = new
        {
            message_id = id,
            from = new { id = from },
            chat = new { id = from, type = "private" },
            date = (sentAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            text,
            caption,
            photo = photo is null ? null : new[] { new { file_id = photo + "-small", file_size = 10 }, new { file_id = photo, file_size = 100 } },
            media_group_id = album,
            document,
        },
    };

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1];

    private Task WaitForRepliesAsync(int count, string message) =>
        DevChat.EventuallyAsync(() => Task.FromResult(_telegram.Sent.Count >= count), message);

    /// <summary>模拟 Telegram Bot API：getUpdates 从队列取（没有就一直等），记下发出的消息。</summary>
    private sealed class FakeTelegram : HttpMessageHandler
    {
        public Channel<object[]> Updates { get; } = Channel.CreateUnbounded<object[]>();

        public ConcurrentQueue<(long Chat, string Text)> Sent { get; } = new();

        public ConcurrentQueue<long> Offsets { get; } = new();

        public ConcurrentDictionary<string, byte[]> Files { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith($"/file/bot{Token}/", StringComparison.Ordinal))
            {
                return Files.TryGetValue(path[$"/file/bot{Token}/".Length..], out var file)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (!path.StartsWith($"/bot{Token}/", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Unauthorized, new { ok = false, description = "Unauthorized" });
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            switch (path[$"/bot{Token}/".Length..])
            {
                case "getUpdates":
                    if (root.TryGetProperty("offset", out var offset))
                    {
                        Offsets.Enqueue(offset.GetInt64());
                    }

                    return Json(HttpStatusCode.OK, new { ok = true, result = await Updates.Reader.ReadAsync(cancellationToken) });
                case "sendMessage":
                    Sent.Enqueue((root.GetProperty("chat_id").GetInt64(), root.GetProperty("text").GetString() ?? ""));
                    return Json(HttpStatusCode.OK, new { ok = true, result = new { message_id = 1 } });
                case "getFile":
                    var id = root.GetProperty("file_id").GetString();
                    return Json(HttpStatusCode.OK, new { ok = true, result = new { file_id = id, file_size = 9, file_path = $"photos/{id}.png" } });
                default:
                    return Json(HttpStatusCode.NotFound, new { ok = false, description = "Not Found" });
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}

/// <summary>没启用 Telegram 时，它的设置写错了也不影响服务器启动（审查 #11）。</summary>
public sealed class TelegramStartupTests
{
    [Fact]
    public async Task Bad_proxy_does_not_stop_the_server_when_telegram_is_disabled()
    {
        await using var factory = new ChatPilotFactory();
        factory.Settings["ChatPilot:Telegram:Proxy"] = "not a proxy";
        using var chat = factory.CreateDevClient();
        Assert.Contains("ChatPilot 指令", await DevChat.SendAsync(chat, "/help"), StringComparison.Ordinal);
    }
}
