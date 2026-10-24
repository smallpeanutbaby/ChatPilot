using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChatPilot.Node.Connection;
using ChatPilot.Node.Execution;
using ChatPilot.Node.Herdr;
using ChatPilot.Server.Chat;
using ChatPilot.Server.Devices;
using ChatPilot.Server.WeChat;
using ChatPilot.Shared;
using ChatPilot.Shared.Media;
using ChatPilot.Shared.Net;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Tests.Server;

/// <summary>新指令的解析（F-40 起）：不写目标、/re、/diff、/stop、/close、/watch、/pending、/history、/at、/wake、/drop。</summary>
public class NewCommandParserTests
{
    [Fact]
    public void Reply_keeps_body_verbatim()
    {
        Assert.Equal("第一行\n第二行 \"引号\"", Assert.IsType<ChatReply>(ChatCommandParser.Parse("/re 第一行\n第二行 \"引号\"")).Text);
        Assert.Equal("好", Assert.IsType<ChatReply>(ChatCommandParser.Parse("／接着 好")).Text);
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/re"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/re " + new string('a', ProtocolInfo.MaxPromptChars + 1)));
    }

    [Fact]
    public void Target_is_optional_for_yes_no_read_last_stop_and_diff()
    {
        Assert.Equal(new ChatRespond(null, true), ChatCommandParser.Parse("/yes"));
        Assert.Equal(new ChatRespond(null, false), ChatCommandParser.Parse("/no"));
        Assert.Equal(new ChatRead(null, ProtocolInfo.DefaultReadLines), ChatCommandParser.Parse("/read"));
        Assert.Equal(new ChatRead(new TargetRef(null, "2"), 100), ChatCommandParser.Parse("/read 2 100"));
        Assert.Equal(new ChatStop(null), ChatCommandParser.Parse("/stop"));
        Assert.Equal(new ChatStop(new TargetRef("mac", "x")), ChatCommandParser.Parse("/stop mac/x"));
        Assert.Equal(new ChatDiff(null, null), ChatCommandParser.Parse("/diff"));
        Assert.Equal(new ChatDiff(new TargetRef(null, "tideq"), "src/app.ts"), ChatCommandParser.Parse("/diff tideq src/app.ts"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/diff tideq ../secret"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/diff tideq -p"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/diff a b c"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/yes a b"));
    }

    [Fact]
    public void Close_needs_a_target_and_an_explicit_confirmation_word()
    {
        Assert.Equal(new ChatClose(new TargetRef(null, "tideq"), false), ChatCommandParser.Parse("/close tideq"));
        Assert.Equal(new ChatClose(new TargetRef("mac", "tideq"), true), ChatCommandParser.Parse("/close mac/tideq 确认"));
        Assert.True(Assert.IsType<ChatClose>(ChatCommandParser.Parse("/关闭 tideq CONFIRM")).Confirmed);
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/close tideq 嗯"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/close"));
    }

    [Fact]
    public void Watch_and_unwatch()
    {
        Assert.IsType<ChatWatchList>(ChatCommandParser.Parse("/watch"));
        Assert.Equal(new ChatWatchAll(null, true), ChatCommandParser.Parse("/watch all"));
        Assert.Equal(new ChatWatchAll("mac", true), ChatCommandParser.Parse("/watch 全部 mac"));
        Assert.Equal(new ChatWatch(new TargetRef(null, "tideq"), true), ChatCommandParser.Parse("/盯 tideq"));
        Assert.Equal(new ChatWatch(new TargetRef("mac", "all"), false), ChatCommandParser.Parse("/unwatch mac/all"));
        Assert.Equal(new ChatWatchAll(null, false), ChatCommandParser.Parse("/unwatch ALL"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/unwatch"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/watch all a/b"));
    }

    [Fact]
    public void Pending_history_wake_and_drop()
    {
        Assert.IsType<ChatPending>(ChatCommandParser.Parse("/pending"));
        Assert.IsType<ChatPending>(ChatCommandParser.Parse("/待办"));
        Assert.Equal(new ChatHistory(10), ChatCommandParser.Parse("/history"));
        Assert.Equal(new ChatHistory(5), ChatCommandParser.Parse("/历史 5"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/history 99"));
        Assert.Equal(new ChatWake("mac"), ChatCommandParser.Parse("/wake mac"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/wake"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/wake a/b"));
        Assert.IsType<ChatDropImages>(ChatCommandParser.Parse("/drop"));
    }

    [Theory]
    [InlineData("/at 23:00 @api 跑全量测试", null, 23, 0, false, "@api 跑全量测试")]
    [InlineData("/at 7：05 @api 跑", null, 7, 5, false, "@api 跑")]
    [InlineData("/at 明天 9:00 整理日报", null, 9, 0, true, "整理日报")]
    [InlineData("/at tomorrow 18:30 @mac/x y", null, 18, 30, true, "@mac/x y")]
    [InlineData("/at 30m @2 看看测试结果", 30, 0, 0, false, "@2 看看测试结果")]
    [InlineData("/at 1h30m @x y", 90, 0, 0, false, "@x y")]
    [InlineData("/at 2小时 @x y", 120, 0, 0, false, "@x y")]
    [InlineData("/定时 45分钟 @x 第一行\n第二行", 45, 0, 0, false, "@x 第一行\n第二行")]
    public void At_parses_times_and_keeps_the_payload_verbatim(string text, int? minutes, int hour, int minute, bool tomorrow, string payload)
    {
        var at = Assert.IsType<ChatAt>(ChatCommandParser.Parse(text));
        Assert.Equal(new ScheduleTime(minutes is { } m ? TimeSpan.FromMinutes(m) : null, hour, minute, tomorrow), at.When);
        Assert.Equal(payload, at.Text);
    }

    [Theory]
    [InlineData("/at 25:00 @x y")]
    [InlineData("/at 0m @x y")]
    [InlineData("/at 200h @x y")]
    [InlineData("/at 3d @x y")]
    [InlineData("/at 30m")]
    [InlineData("/at 30m /ls")]
    [InlineData("/at 明天 30m @x y")]
    [InlineData("/at 30m @x")]
    [InlineData("/at del x")]
    public void At_rejects_bad_times_and_payloads(string text) => Assert.IsType<ChatInvalid>(ChatCommandParser.Parse(text));

    [Fact]
    public void At_lists_and_cancels()
    {
        Assert.IsType<ChatAtList>(ChatCommandParser.Parse("/at"));
        Assert.Equal(new ChatAtCancel(3), ChatCommandParser.Parse("/at del 3"));
        Assert.Equal(new ChatAtCancel(2), ChatCommandParser.Parse("/at 取消 2"));
    }
}

/// <summary>新功能用到的小工具：格式化、校验、图片识别、密钥解析、网段判断、唤醒包、盯梢的状态变化。</summary>
public class NewFeatureHelperTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    [Fact]
    public void Completion_notice_mentions_queued_prompts_but_confirmation_notice_does_not()
    {
        var agent = new AgentSummary("w1:p1", "tideq", "claude", "idle", null, null, null);
        Assert.EndsWith("📥 还有 2 条排队的指令，会接着发给它", ReplyFormatter.AgentEvent("mac", new AgentEvent(AgentEventKind.Completed, agent, "好了", Queued: 2)), StringComparison.Ordinal);
        Assert.DoesNotContain("排队", ReplyFormatter.AgentEvent("mac", new AgentEvent(AgentEventKind.Blocked, agent, "确认？", Queued: 2)), StringComparison.Ordinal);
        Assert.Equal("✅ [mac] tideq 做完了：\n\n好了", ReplyFormatter.AgentEvent("mac", new AgentEvent(AgentEventKind.Completed, agent, "好了")));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", true)]
    [InlineData("02:00:00:00:00:01", true)]
    [InlineData("aa:bb:cc:dd:ee:ff", false)]
    [InlineData("AA-BB-CC-DD-EE-FF", false)]
    [InlineData("AA:BB:CC:DD:EE:FF\n", false)]
    [InlineData("AA:BB:CC:DD:EE", false)]
    public void Mac_addresses(string value, bool valid) => Assert.Equal(valid, Validation.IsMacAddress(value));

    [Theory]
    [InlineData("src/app.ts", true)]
    [InlineData("README.md", true)]
    [InlineData("目录/文件 名.txt", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("a/../../b", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/x", false)]
    [InlineData("-p", false)]
    [InlineData(":(glob)*", false)]
    [InlineData("a\nb", false)]
    [InlineData("", false)]
    public void Repo_relative_paths(string value, bool valid) => Assert.Equal(valid, Validation.IsRepoRelativePath(value));

    [Fact]
    public void Image_formats_are_detected_from_magic_bytes()
    {
        Assert.Equal("png", ImageFormat.DetectExtension(Png));
        Assert.Equal("jpg", ImageFormat.DetectExtension([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("gif", ImageFormat.DetectExtension("GIF89a.."u8));
        Assert.Equal("webp", ImageFormat.DetectExtension("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(ImageFormat.DetectExtension("<svg>"u8));
        Assert.Null(ImageFormat.DetectExtension([]));
    }

    [Fact]
    public void Wechat_image_keys_accept_hex_raw_base64_and_base64_of_hex()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var hex = Convert.ToHexStringLower(key);
        Assert.Equal(key, ILinkClient.ParseImageKey(hex, "ignored"));
        Assert.Equal(key, ILinkClient.ParseImageKey(null, Convert.ToBase64String(key)));
        Assert.Equal(key, ILinkClient.ParseImageKey(null, Convert.ToBase64String(Encoding.ASCII.GetBytes(hex))));
        Assert.Null(ILinkClient.ParseImageKey(null, null));
        Assert.Throws<ILinkException>(() => ILinkClient.ParseImageKey("xyz", null));
        Assert.Throws<ILinkException>(() => ILinkClient.ParseImageKey(null, Convert.ToBase64String(new byte[20])));
        Assert.Throws<ILinkException>(() => ILinkClient.ParseImageKey(null, "not base64!"));
    }

    [Fact]
    public void Subnets_are_compared_with_the_target_prefix()
    {
        IReadOnlyList<NetworkInterfaceInfo> target = [new("02:00:00:00:00:01", "192.168.1.20", 24)];
        Assert.True(ChatDispatcher.SharesSubnet([new("02:00:00:00:00:02", "192.168.1.99", 24)], target));
        Assert.False(ChatDispatcher.SharesSubnet([new("02:00:00:00:00:02", "192.168.2.99", 24)], target));
        Assert.False(ChatDispatcher.SharesSubnet([new("02:00:00:00:00:02", null, 0)], target));
        Assert.False(ChatDispatcher.SharesSubnet(null, target));
    }

    [Fact]
    public void Reported_networks_are_sanitized()
    {
        var clean = DeviceHub.SanitizeNetworks(
        [
            new("02:00:00:00:00:01", "192.168.1.20", 24),
            new("bad", "192.168.1.21", 24),
            new("02:00:00:00:00:02", "::1", 64),
            new("02:00:00:00:00:03", "10.0.0.1", 40),
            new("02:00:00:00:00:04", null, 0),
        ]);
        Assert.Equal(["02:00:00:00:00:01", "02:00:00:00:00:04"], clean?.Select(n => n.Mac));
        Assert.Null(DeviceHub.SanitizeNetworks(null));
        Assert.Null(DeviceHub.SanitizeNetworks([new("bad", null, 0)]));
    }

    [Fact]
    public void Magic_packet_and_directed_broadcast()
    {
        var packet = LocalNetwork.MagicPacket("02:00:00:00:00:0A");
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0x0A }, packet[96..]);
        Assert.Equal(IPAddress.Parse("192.168.1.255"), Ipv4.Broadcast(IPAddress.Parse("192.168.1.23"), 24));
        Assert.Equal(IPAddress.Parse("10.0.255.255"), Ipv4.Broadcast(IPAddress.Parse("10.0.3.4"), 16));
        Assert.Equal(Ipv4.ToUInt32(IPAddress.Parse("192.168.1.0")), Ipv4.Network(IPAddress.Parse("192.168.1.200"), 24));
        Assert.Throws<ArgumentException>(() => LocalNetwork.MagicPacket("nope"));
        Assert.Throws<ArgumentException>(() => Ipv4.ToUInt32(IPAddress.IPv6Loopback));
    }

    [Fact]
    public async Task Bounded_reads_stop_at_the_limit()
    {
        using var small = new ByteArrayContent(new byte[10]);
        Assert.Equal(10, (await small.ReadBoundedAsync(10, CancellationToken.None))?.Length);
        using var large = new ByteArrayContent(new byte[11]);
        Assert.Null(await large.ReadBoundedAsync(10, CancellationToken.None));
        using var lying = new StreamContent(new MemoryStream(new byte[100]));
        lying.Headers.ContentLength = 5;
        Assert.Null(await lying.ReadBoundedAsync(10, CancellationToken.None));
    }

    [Fact]
    public void Unknown_time_zone_falls_back_with_a_warning_instead_of_refusing_to_start()
    {
        var unknown = new ChatPilot.Server.Options.ServerOptions { TimeZone = "Mars/Olympus" };
        Assert.Empty(unknown.Validate());
        Assert.Equal(TimeZoneInfo.Local, unknown.ResolveTimeZone());
        Assert.Contains("Mars/Olympus", unknown.TimeZoneWarning(), StringComparison.Ordinal);

        var shanghai = new ChatPilot.Server.Options.ServerOptions { TimeZone = "Asia/Shanghai" };
        Assert.Null(shanghai.TimeZoneWarning());
        Assert.Equal(TimeSpan.FromHours(8), shanghai.ResolveTimeZone().BaseUtcOffset);
    }

    [Fact]
    public void Prompt_summaries_keep_the_first_line()
    {
        Assert.Equal("修复登录页", PromptTracker.Summarize("  修复登录页\n细节很多……"));
        Assert.Null(PromptTracker.Summarize("   "));
        Assert.Equal(ProtocolInfo.MaxEventPromptChars, PromptTracker.Summarize(new string('字', 500))?.Length);
    }

    [Theory]
    [InlineData("idle", 1, "blocked", 2, AgentEventKind.Blocked)]
    [InlineData("blocked", 2, "blocked", 2, null)]
    [InlineData("working", 1, "idle", 2, AgentEventKind.Completed)]
    [InlineData("working", 1, "done", 2, AgentEventKind.Completed)]
    [InlineData("idle", 1, "idle", 3, AgentEventKind.Completed)]
    [InlineData("idle", 1, "done", 2, null)]
    [InlineData("idle", 1, "working", 2, null)]
    [InlineData("idle", 5, "idle", 5, null)]
    public void Watcher_reports_only_meaningful_transitions(string before, long beforeSeq, string after, long afterSeq, AgentEventKind? expected)
    {
        var agent = new HerdrAgent("w1:p1", "tideq", "claude", after, "w1", "/tmp", null, null, afterSeq);
        Assert.Equal(expected, AgentWatcher.Transition(before, beforeSeq, agent));
    }
}
