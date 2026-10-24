using ChatPilot.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPilot.Tests.Integration;

/// <summary>服务器 + 节点 + 测试通道的端到端流程（herdr 用模拟的）。</summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly ChatPilotFactory _factory;
    private readonly HttpClient _chat;

    public EndToEndTests()
    {
        _factory = new ChatPilotFactory();
        _chat = _factory.CreateDevClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _chat.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Pair_command_returns_code_and_instructions()
    {
        // F-01
        var reply = await DevChat.SendAsync(_chat, "/pair mac");
        Assert.Matches("配对码：[A-Z0-9]{4}-[A-Z0-9]{4}", reply);
        Assert.Contains("10 分钟内有效", reply, StringComparison.Ordinal);
        Assert.Contains("chatpilot-node pair --server https://chatpilot.example.com:8443 --code", reply, StringComparison.Ordinal);
        Assert.Contains("--name mac", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_goes_online_and_offline()
    {
        // F-02 / F-03
        Assert.Contains("还没有设备", await DevChat.SendAsync(_chat, "/devices"), StringComparison.Ordinal);
        var node = await NodeHarness.PairAndStartAsync(_factory, "mac");
        Assert.Contains("🟢 mac", await DevChat.SendAsync(_chat, "/devices"), StringComparison.Ordinal);

        await node.DisposeAsync();
        await DevChat.EventuallyAsync(
            async () => (await DevChat.SendAsync(_chat, "/devices")).Contains("⚪ mac", StringComparison.Ordinal),
            "节点退出后设备应该显示离线");
    }

    [Fact]
    public async Task List_use_prompt_and_completion_notification()
    {
        // F-10 / F-11 / F-12 / F-15
        var herdr = new FakeHerdr { PromptDelay = TimeSpan.FromMilliseconds(200) };
        herdr.AddAgent("tideq", "TideQ", "idle");
        herdr.AddAgent(null, "workhub", "working").Title = "项目需求讨论";
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        var list = await DevChat.SendAsync(_chat, "/ls");
        Assert.Contains("📱 mac", list, StringComparison.Ordinal);
        Assert.Contains("1. 💤 tideq · claude · 空闲", list, StringComparison.Ordinal);
        Assert.Contains("2. ⏳ workhub · claude · 工作中 · w2:p1", list, StringComparison.Ordinal);
        Assert.Contains("项目需求讨论", list, StringComparison.Ordinal);

        Assert.Contains("默认设备切换为 mac", await DevChat.SendAsync(_chat, "/use MAC"), StringComparison.Ordinal);
        Assert.Contains("没有叫 nope 的设备", await DevChat.SendAsync(_chat, "/use nope"), StringComparison.Ordinal);

        var ack = await DevChat.SendAsync(_chat, "@1 只回复：收到");
        Assert.Equal("📨 已发给 [mac] tideq，做完会通知你", ack);
        Assert.Contains(herdr.CallsStartingWith("agent", "prompt"), c => c[2] == "w1:p1" && c[3] == "只回复：收到");

        var done = await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅", StringComparison.Ordinal));
        Assert.Equal("✅ [mac] tideq 做完了：\n\n收到", done);
    }

    [Theory]
    [InlineData("@tideq 你好")]
    [InlineData("@TIDEQ 你好")]
    [InlineData("@w1:p1 你好")]
    [InlineData("@mac/tideq 你好")]
    [InlineData("@workhub 你好")]
    public async Task Targets_resolve_by_name_pane_label_and_device_prefix(string text)
    {
        // F-12 / F-14
        var herdr = new FakeHerdr();
        herdr.AddAgent("tideq", "TideQ");
        herdr.AddAgent(null, "workhub");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        Assert.StartsWith("📨 已发给 [mac]", await DevChat.SendAsync(_chat, text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_missing_and_offline_targets_are_explained()
    {
        // F-13 / F-14
        var mac = new FakeHerdr();
        mac.AddAgent("web", "web");
        var win = new FakeHerdr();
        win.AddAgent("web", "web");
        await using var macNode = await NodeHarness.PairAndStartAsync(_factory, "mac", mac);
        await using var winNode = await NodeHarness.PairAndStartAsync(_factory, "win", win);

        var ambiguous = await DevChat.SendAsync(_chat, "@web hi");
        Assert.Contains("有多个匹配", ambiguous, StringComparison.Ordinal);
        Assert.Contains("mac/web", ambiguous, StringComparison.Ordinal);
        Assert.Contains("win/web", ambiguous, StringComparison.Ordinal);
        Assert.Empty(mac.CallsStartingWith("agent", "prompt"));
        Assert.Empty(win.CallsStartingWith("agent", "prompt"));

        Assert.Contains("找不到 nothing", await DevChat.SendAsync(_chat, "@nothing hi"), StringComparison.Ordinal);
        Assert.Contains("没有序号 9", await DevChat.SendAsync(_chat, "@9 hi"), StringComparison.Ordinal);

        await DevChat.SendAsync(_chat, "/use win");
        Assert.StartsWith("📨 已发给 [win] web", await DevChat.SendAsync(_chat, "@web hi"), StringComparison.Ordinal);

        await winNode.DisposeAsync();
        await DevChat.EventuallyAsync(
            async () => (await DevChat.SendAsync(_chat, "@win/web hi")).Contains("设备 win 离线", StringComparison.Ordinal),
            "离线设备应该给出提示");
    }

    [Fact]
    public async Task Read_new_keys_rename_and_respond()
    {
        // F-16 / F-17 / F-18 / F-19
        var herdr = new FakeHerdr();
        var agent = herdr.AddAgent("tideq", "TideQ");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        Assert.Contains("⏺ 收到", await DevChat.SendAsync(_chat, "/read tideq 30"), StringComparison.Ordinal);
        var last = await DevChat.SendAsync(_chat, "/last tideq");
        Assert.Contains("最近的回复：\n\n收到", last, StringComparison.Ordinal);

        var project = node.Root.CreateDirectory("web");
        var created = await DevChat.SendAsync(_chat, $"/new {project} claude webby");
        Assert.Contains("🚀 已在 [mac] 启动 webby（claude）", created, StringComparison.Ordinal);
        Assert.Contains("发 @mac/webby 内容", created, StringComparison.Ordinal);

        var outside = Path.GetDirectoryName(node.Root.Path);
        Assert.Contains("不在允许的目录内", await DevChat.SendAsync(_chat, $"/new mac {outside}"), StringComparison.Ordinal);

        Assert.Contains("已发送按键：down enter", await DevChat.SendAsync(_chat, "/keys tideq down enter"), StringComparison.Ordinal);
        Assert.Contains("不允许的按键", await DevChat.SendAsync(_chat, "/keys tideq ctrl+d"), StringComparison.Ordinal);
        Assert.Contains("已改名为 tq", await DevChat.SendAsync(_chat, "/name tideq tq"), StringComparison.Ordinal);

        // 卡住时节点没有通知过（比如是在电脑上派的活）：第一次 /yes 先把确认框发过来，第二次才按键。
        agent.Status = "blocked";
        herdr.Screen = SampleScreens.TrustDialog;
        var unseen = await DevChat.SendAsync(_chat, "/yes tq");
        Assert.Contains("还没发给你看过", unseen, StringComparison.Ordinal);
        Assert.Contains("Yes, I trust this folder", unseen, StringComparison.Ordinal);
        Assert.Contains("已选择：Yes, I trust this folder", await DevChat.SendAsync(_chat, "/yes tq"), StringComparison.Ordinal);
        Assert.Contains("已按 Esc", await DevChat.SendAsync(_chat, "/no tq"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_agent_waiting_for_trust_shows_screen_and_how_to_answer()
    {
        var herdr = new FakeHerdr { StartBlocked = true, Screen = SampleScreens.TrustDialog };
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        var reply = await DevChat.SendAsync(_chat, $"/new {node.Root.Path} claude fresh");
        Assert.Contains("它在等你确认", reply, StringComparison.Ordinal);
        Assert.Contains("Yes, I trust this folder", reply, StringComparison.Ordinal);
        Assert.Contains("/yes mac/fresh", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_text_goes_to_butler_or_explains_how_to_create_one()
    {
        // F-20 / F-21
        var herdr = new FakeHerdr();
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        Assert.Contains("还没有管家", await DevChat.SendAsync(_chat, "看看谁闲着"), StringComparison.Ordinal);

        var created = await DevChat.SendAsync(_chat, "/butler");
        Assert.Contains("启动 butler", created, StringComparison.Ordinal);
        await DevChat.EventuallyAsync(
            () => Task.FromResult(herdr.CallsStartingWith("agent", "prompt").Any(c => c[2] == herdr.Find("butler")?.PaneId && c[3].Contains("herdr --skill", StringComparison.Ordinal))),
            "管家就绪后应该收到初始说明");

        Assert.Equal("📨 已交给 [mac] 的管家，做完会通知你", await DevChat.SendAsync(_chat, "看看谁闲着"));
    }

    [Fact]
    public async Task Bad_input_returns_usage_not_errors()
    {
        // F-22
        Assert.StartsWith("❓ 用法：/read", await DevChat.SendAsync(_chat, "/read"), StringComparison.Ordinal);
        Assert.StartsWith("❓ 不认识的指令", await DevChat.SendAsync(_chat, "/what"), StringComparison.Ordinal);
        Assert.Contains("ChatPilot 指令", await DevChat.SendAsync(_chat, "/help"), StringComparison.Ordinal);
        Assert.Contains("没有在线的设备", await DevChat.SendAsync(_chat, "@x hi"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_replies_are_split_into_at_most_four_messages()
    {
        // F-15
        var herdr = new FakeHerdr { Screen = "❯ q\n\n⏺ " + string.Join("\n  ", Enumerable.Range(0, 3000).Select(i => $"第 {i} 行输出")) };
        herdr.AddAgent("tideq", "TideQ");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        await DevChat.SendAsync(_chat, "@tideq 输出很多");
        var done = await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅", StringComparison.Ordinal));
        var chunks = _factory.Services.GetRequiredService<ChatPilot.Server.Chat.DevChannel>().Chunk(done);
        Assert.InRange(chunks.Count, 2, 4);
        Assert.All(chunks, c => Assert.True(c.Length <= 4000));
    }

    [Fact]
    public async Task Unpair_disconnects_and_revokes_token()
    {
        // F-05
        var node = await NodeHarness.PairAndStartAsync(_factory, "mac");
        Assert.Contains("已撤销设备 mac", await DevChat.SendAsync(_chat, "/unpair mac"), StringComparison.Ordinal);
        Assert.Equal(2, await node.RunTask.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("还没有设备", await DevChat.SendAsync(_chat, "/devices"), StringComparison.Ordinal);
        await node.DisposeAsync();
    }

    [Fact]
    public async Task Second_connection_replaces_first()
    {
        // F-06
        var code = await NodeHarness.PairingCodeAsync(_factory);
        var paired = await ChatPilot.Node.Connection.PairingClient.PairAsync(
            _factory.Server.BaseAddress, _factory.Server.CreateHandler(), new ChatPilot.Shared.Protocol.PairRequest(code, "mac", "x"), CancellationToken.None);
        var firstHerdr = new FakeHerdr();
        firstHerdr.AddAgent("first", "first");
        var secondHerdr = new FakeHerdr();
        secondHerdr.AddAgent("second", "second");

        await using var first = NodeHarness.Start(_factory, paired.Token, "mac", firstHerdr);
        await first.Welcome.WaitAsync(TimeSpan.FromSeconds(10));
        await using var second = NodeHarness.Start(_factory, paired.Token, "mac", secondHerdr);
        await second.Welcome.WaitAsync(TimeSpan.FromSeconds(10));

        await DevChat.EventuallyAsync(
            async () => (await DevChat.SendAsync(_chat, "/ls")).Contains("second", StringComparison.Ordinal),
            "新连接应该替换旧连接");
    }
}

/// <summary>F-23：节点不响应时服务器按时返回超时，不会卡住。</summary>
public sealed class NodeTimeoutTests
{
    [Fact]
    public async Task Node_timeouts_do_not_hang_the_server()
    {
        // F-23
        await using var factory = new ChatPilotFactory();
        factory.Settings["ChatPilot:CommandTimeoutSeconds"] = "5";
        using var chat = factory.CreateDevClient();
        var herdr = new FakeHerdr { CommandDelay = TimeSpan.FromSeconds(8) };
        await using var node = await NodeHarness.PairAndStartAsync(factory, "slow", herdr);

        var started = DateTime.UtcNow;
        var reply = await DevChat.SendAsync(chat, "/ls");
        Assert.Contains("在 5 秒内没有响应", reply, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8));
        Assert.Contains("ChatPilot 指令", await DevChat.SendAsync(chat, "/help"), StringComparison.Ordinal);
    }
}
