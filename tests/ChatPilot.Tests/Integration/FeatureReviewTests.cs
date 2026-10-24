using ChatPilot.Tests.Support;

namespace ChatPilot.Tests.Integration;

/// <summary>新功能审查发现的问题的回归测试：不写目标的指令只跟着主人自己的操作走，不会替主人同意没想同意的确认框。</summary>
public sealed class FeatureReviewTests : IAsyncLifetime
{
    private readonly ChatPilotFactory _factory;
    private readonly HttpClient _chat;

    public FeatureReviewTests()
    {
        _factory = new ChatPilotFactory();
        _factory.Settings["ChatPilot:CommandTimeoutSeconds"] = "5";
        _chat = _factory.CreateDevClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _chat.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Bare_yes_answers_the_agent_whose_prompt_was_just_refused_not_an_older_dialog()
    {
        // 审查 #1 场景一：a 卡在危险的确认框上一小时了；主人派活给 c，被告知"c 正在等你确认，先回复 /yes"，回 /yes 应该是给 c 的。
        var herdr = new FakeHerdr { StatusAfterPrompt = "blocked", Screen = SampleScreens.PermissionDialog };
        var a = herdr.AddAgent("a", "A");
        var c = herdr.AddAgent("c", "C");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        await DevChat.SendAsync(_chat, "@a git push --force");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [mac] a 在等你确认", StringComparison.Ordinal));
        c.Status = "blocked";
        Assert.StartsWith("❌ 它正在等你确认，先回复 /yes 或 /no", await DevChat.SendAsync(_chat, "@c 修bug"), StringComparison.Ordinal);

        Assert.StartsWith("👌 [mac] c：已选择", await DevChat.SendAsync(_chat, "/yes"), StringComparison.Ordinal);
        Assert.DoesNotContain(herdr.CallsStartingWith("agent", "send-keys"), call => call[2] == a.PaneId);
        Assert.Equal("blocked", a.Status);
    }

    [Fact]
    public async Task Bare_yes_asks_when_a_device_with_a_waiting_agent_does_not_answer()
    {
        // 审查 #1 场景二：a、b 都在等确认，a 所在的电脑查询超时，不能因为只剩 b 能查到就同意 b。
        var mac = new FakeHerdr { StatusAfterPrompt = "blocked", Screen = SampleScreens.PermissionDialog };
        mac.AddAgent("a", "A");
        var win = new FakeHerdr { StatusAfterPrompt = "blocked", Screen = SampleScreens.PermissionDialog };
        var b = win.AddAgent("b", "B");
        await using var macNode = await NodeHarness.PairAndStartAsync(_factory, "mac", mac);
        await using var winNode = await NodeHarness.PairAndStartAsync(_factory, "win", win);

        await DevChat.SendAsync(_chat, "@a 删库");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [mac] a", StringComparison.Ordinal));
        await DevChat.SendAsync(_chat, "@b 改配置");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [win] b", StringComparison.Ordinal));
        await DevChat.SendAsync(_chat, "/read mac/a");
        mac.CommandDelay = TimeSpan.FromSeconds(8);

        var reply = await DevChat.SendAsync(_chat, "/yes");
        Assert.StartsWith("❓ mac（", reply, StringComparison.Ordinal);
        Assert.Contains("请写上目标", reply, StringComparison.Ordinal);
        Assert.Contains("win/b", reply, StringComparison.Ordinal);
        Assert.Empty(win.CallsStartingWith("agent", "send-keys"));
        Assert.Equal("blocked", b.Status);
    }

    [Fact]
    public async Task Bare_yes_answers_a_new_agent_stuck_on_a_dialog()
    {
        // 审查 #1：/new 回复"它在等你确认"之后的 /yes 是给新 agent 的，不是给之前通知过的 a 的。
        var herdr = new FakeHerdr { StatusAfterPrompt = "blocked", Screen = SampleScreens.PermissionDialog };
        var a = herdr.AddAgent("a", "A");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        await DevChat.SendAsync(_chat, "@a 删掉 build");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [mac] a", StringComparison.Ordinal));

        herdr.StartBlocked = true;
        herdr.Screen = SampleScreens.TrustDialog;
        Assert.Contains("它在等你确认", await DevChat.SendAsync(_chat, $"/new {node.Root.Path} claude fresh"), StringComparison.Ordinal);

        Assert.StartsWith("👌 [mac] fresh：已选择：Yes, I trust this folder", await DevChat.SendAsync(_chat, "/yes"), StringComparison.Ordinal);
        Assert.DoesNotContain(herdr.CallsStartingWith("agent", "send-keys"), call => call[2] == a.PaneId);
    }

    [Fact]
    public async Task Watched_agents_finishing_do_not_change_where_stop_and_re_go()
    {
        // 审查 #2：盯着的 web 自己做完了，/stop、/re 还是给主人刚派活的 api；主人派的活有了结果时，默认目标照样跟过去。
        var herdr = new FakeHerdr { PromptDelay = TimeSpan.FromSeconds(1) };
        herdr.AddAgent("api", "Api");
        var web = herdr.AddAgent("web", "Web");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        Assert.StartsWith("👀 [mac] 开始盯着 web", await DevChat.SendAsync(_chat, "/watch web"), StringComparison.Ordinal);

        Assert.Equal("📨 已发给 [mac] api，做完会通知你", await DevChat.SendAsync(_chat, "@api 跑测试"));
        await DevChat.SendAsync(_chat, "/read web");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅ [mac] api", StringComparison.Ordinal));
        Assert.StartsWith("💬 [mac] api", await DevChat.SendAsync(_chat, "/last"), StringComparison.Ordinal);

        web.Status = "working";
        await Task.Delay(400);
        web.Status = "idle";
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅ [mac] web 做完了", StringComparison.Ordinal));

        Assert.StartsWith("✋ [mac] api：", await DevChat.SendAsync(_chat, "/stop"), StringComparison.Ordinal);
        Assert.Equal("📨 已发给 [mac] api，做完会通知你", await DevChat.SendAsync(_chat, "/re 再跑一次"));
        Assert.DoesNotContain(herdr.CallsStartingWith("agent", "prompt"), call => call[2] == web.PaneId);
        Assert.DoesNotContain(herdr.CallsStartingWith("agent", "send-keys"), call => call[2] == web.PaneId);
    }

    [Fact]
    public async Task Bare_confirm_word_after_close_is_not_sent_to_the_butler()
    {
        // 审查 #10："确认"是对"要关闭吗？"的自然回答，不能交给管家，也不能让不写目标的 /yes 去同意别的确认框。
        var herdr = new FakeHerdr();
        herdr.AddAgent("butler", "Butler");
        var tideq = herdr.AddAgent("tideq", "TideQ");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        Assert.StartsWith("⚠️ 要关闭 [mac] tideq 吗？", await DevChat.SendAsync(_chat, "/close tideq"), StringComparison.Ordinal);
        var reminder = await DevChat.SendAsync(_chat, "确认");
        Assert.StartsWith("⚠️ 还没有关闭 [mac] tideq", reminder, StringComparison.Ordinal);
        Assert.Contains("/close mac/tideq 确认", reminder, StringComparison.Ordinal);
        Assert.Contains("/close mac/tideq 确认", await DevChat.SendAsync(_chat, "/yes"), StringComparison.Ordinal);
        Assert.Empty(herdr.CallsStartingWith("agent", "prompt"));
        Assert.Empty(herdr.CallsStartingWith("agent", "send-keys"));
        Assert.NotNull(herdr.Find(tideq.PaneId));

        Assert.StartsWith("🗑 [mac] tideq：已关闭", await DevChat.SendAsync(_chat, "/close mac/tideq 确认"), StringComparison.Ordinal);
        Assert.Equal("📨 已交给 [mac] 的管家，做完会通知你", await DevChat.SendAsync(_chat, "确认"));
    }
}
