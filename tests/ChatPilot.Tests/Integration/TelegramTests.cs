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
    }

    private static object Update(long id, long from, string? text = null, string? photo = null, string? caption = null) => new
    {
        update_id = id,
        message = new
        {
            message_id = id,
            from = new { id = from },
            chat = new { id = from, type = "private" },
            text,
            caption,
            photo = photo is null ? null : new[] { new { file_id = photo + "-small", file_size = 10 }, new { file_id = photo, file_size = 100 } },
        },
    };

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
