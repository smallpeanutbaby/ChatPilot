using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using ChatPilot.Node.Connection;
using ChatPilot.Server.Chat;
using ChatPilot.Server.State;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace ChatPilot.Tests.Integration;

/// <summary>新功能的端到端流程：服务器 + 节点 + 测试通道（herdr 用模拟的）。</summary>
public sealed class NewFeatureTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9];

    private readonly ChatPilotFactory _factory;
    private readonly HttpClient _chat;

    public NewFeatureTests()
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
    public async Task Commands_without_a_target_use_the_agent_last_dealt_with()
    {
        // F-40 / F-41
        var herdr = new FakeHerdr();
        herdr.AddAgent("tideq", "TideQ");
        herdr.AddAgent("api", "Api");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        Assert.StartsWith("❓ 还没有打过交道的 agent", await DevChat.SendAsync(_chat, "/re 你好"), StringComparison.Ordinal);
        Assert.Equal("📨 已发给 [mac] api，做完会通知你", await DevChat.SendAsync(_chat, "@api 只回复：收到"));
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅ [mac] api", StringComparison.Ordinal));

        Assert.StartsWith("🖥 [mac] api 最近的输出", await DevChat.SendAsync(_chat, "/read"), StringComparison.Ordinal);
        Assert.StartsWith("💬 [mac] api 最近的回复", await DevChat.SendAsync(_chat, "/last"), StringComparison.Ordinal);
        Assert.Equal("📨 已发给 [mac] api，做完会通知你", await DevChat.SendAsync(_chat, "/re 再来一次"));
        Assert.Contains(herdr.CallsStartingWith("agent", "prompt"), c => c[2] == "w2:p1" && c[3] == "再来一次");

        // 看过别的 agent 之后，默认目标跟着变。
        await DevChat.SendAsync(_chat, "/read tideq");
        Assert.StartsWith("💬 [mac] tideq", await DevChat.SendAsync(_chat, "/last"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Yes_without_a_target_approves_the_only_waiting_agent_and_asks_when_several_are_waiting()
    {
        // F-42
        var herdr = new FakeHerdr { StatusAfterPrompt = "blocked", Screen = SampleScreens.PermissionDialog };
        herdr.AddAgent("tideq", "TideQ");
        herdr.AddAgent("api", "Api");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        await DevChat.SendAsync(_chat, "@tideq 删掉 build");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [mac] tideq 在等你确认", StringComparison.Ordinal));
        await DevChat.SendAsync(_chat, "/read api");
        Assert.StartsWith("👌 [mac] tideq：已选择", await DevChat.SendAsync(_chat, "/yes"), StringComparison.Ordinal);

        herdr.Find("tideq")!.Status = "idle";
        await DevChat.SendAsync(_chat, "@tideq 再删一次");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [mac] tideq", StringComparison.Ordinal));
        await DevChat.SendAsync(_chat, "@api 也删");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [mac] api", StringComparison.Ordinal));
        var ambiguous = await DevChat.SendAsync(_chat, "/no");
        Assert.StartsWith("❓ 有好几个 agent 在等确认", ambiguous, StringComparison.Ordinal);
        Assert.Contains("mac/tideq", ambiguous, StringComparison.Ordinal);
        Assert.Contains("mac/api", ambiguous, StringComparison.Ordinal);
        Assert.DoesNotContain(herdr.CallsStartingWith("agent", "send-keys"), c => c[^1] == "esc");
    }

    [Fact]
    public async Task Busy_agent_queues_prompts_and_pending_shows_it()
    {
        // F-43 / F-49
        var herdr = new FakeHerdr { PromptDelay = TimeSpan.FromMilliseconds(100) };
        var agent = herdr.AddAgent("tideq", "TideQ", "working");
        herdr.AddAgent("api", "Api", "blocked");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        Assert.Equal("📨 [mac] tideq：它正在忙，已排队（第 1 条），轮到时自动发出", await DevChat.SendAsync(_chat, "@tideq 一"));
        Assert.Contains("第 2 条", await DevChat.SendAsync(_chat, "@tideq 二"), StringComparison.Ordinal);
        Assert.Contains("tideq · claude · 工作中 · 排队 2", await DevChat.SendAsync(_chat, "/ls"), StringComparison.Ordinal);

        var pending = await DevChat.SendAsync(_chat, "/pending");
        Assert.Contains("⚠️ 在等你确认\n  [mac] api · /yes mac/api", pending, StringComparison.Ordinal);
        Assert.Contains("⏳ 在干活\n  [mac] tideq · 工作中 · 排队 2 条", pending, StringComparison.Ordinal);

        agent.Status = "idle";
        var first = await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅ [mac] tideq", StringComparison.Ordinal));
        Assert.EndsWith("📥 还有 1 条排队的指令，会接着发给它", first, StringComparison.Ordinal);
        var second = await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅ [mac] tideq", StringComparison.Ordinal));
        Assert.DoesNotContain("排队", second, StringComparison.Ordinal);
        Assert.Equal(["一", "二"], herdr.CallsStartingWith("agent", "prompt").Select(c => c[3]));
    }

    [Fact]
    public async Task Stop_and_close()
    {
        // F-44 / F-45
        var herdr = new FakeHerdr();
        var agent = herdr.AddAgent("tideq", "TideQ", "working");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        await DevChat.SendAsync(_chat, "@tideq 一");
        Assert.Equal("✋ [mac] tideq：已按 Esc 打断它，清空了 1 条排队的指令", await DevChat.SendAsync(_chat, "/stop"));

        var preview = await DevChat.SendAsync(_chat, "/close tideq");
        Assert.StartsWith("⚠️ 要关闭 [mac] tideq 吗？", preview, StringComparison.Ordinal);
        Assert.Contains("/close mac/tideq 确认", preview, StringComparison.Ordinal);
        Assert.Empty(herdr.CallsStartingWith("pane", "close"));

        Assert.StartsWith("🗑 [mac] tideq：已关闭", await DevChat.SendAsync(_chat, "/close mac/tideq 确认"), StringComparison.Ordinal);
        Assert.Null(herdr.Find(agent.PaneId));

        // 关掉的 agent 不再是默认目标。
        Assert.StartsWith("❓ 还没有打过交道的 agent", await DevChat.SendAsync(_chat, "/read"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Yes_without_a_target_ignores_dialogs_already_handled_and_follows_renames()
    {
        // 审查：在电脑上处理掉的确认框不再算；改过名的 agent 用现在的名字。
        var herdr = new FakeHerdr { StatusAfterPrompt = "blocked", Screen = SampleScreens.PermissionDialog };
        var tideq = herdr.AddAgent("tideq", "TideQ");
        herdr.AddAgent("api", "Api");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        await DevChat.SendAsync(_chat, "@tideq 删掉 build");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [mac] tideq", StringComparison.Ordinal));
        await DevChat.SendAsync(_chat, "@api 也删");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⚠️ [mac] api", StringComparison.Ordinal));
        tideq.Status = "idle";
        Assert.StartsWith("👌 [mac] api：已改名为 backend", await DevChat.SendAsync(_chat, "/name api backend"), StringComparison.Ordinal);

        Assert.StartsWith("👌 [mac] backend：已选择", await DevChat.SendAsync(_chat, "/yes"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staged_images_stay_when_the_prompt_is_not_delivered()
    {
        var herdr = new FakeHerdr { Screen = SampleScreens.PermissionDialog };
        herdr.AddAgent("tideq", "TideQ", "blocked");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        await SendImageAsync(Png);

        var reply = await DevChat.SendAsync(_chat, "@tideq 看看这个");
        Assert.StartsWith("❌ 它正在等你确认", reply, StringComparison.Ordinal);
        Assert.EndsWith("图片还暂存着，处理好再派活会一起带上；不要了发 /drop", reply, StringComparison.Ordinal);
        Assert.Contains("🖼 暂存的图片 1 张", await DevChat.SendAsync(_chat, "/pending"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_confirmation_without_a_fresh_preview_only_previews()
    {
        var herdr = new FakeHerdr();
        herdr.AddAgent("tideq", "TideQ");
        herdr.AddAgent("api", "Api");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        Assert.StartsWith("确认已经过期或者对不上", await DevChat.SendAsync(_chat, "/close tideq 确认"), StringComparison.Ordinal);
        await DevChat.SendAsync(_chat, "/close api");
        Assert.StartsWith("确认已经过期或者对不上", await DevChat.SendAsync(_chat, "/close tideq 确认"), StringComparison.Ordinal);
        Assert.Empty(herdr.CallsStartingWith("pane", "close"));
    }

    [Fact]
    public async Task Watch_reports_work_started_on_the_computer()
    {
        // F-46
        var herdr = new FakeHerdr();
        var agent = herdr.AddAgent("tideq", "TideQ");
        herdr.AddAgent("api", "Api");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        Assert.StartsWith("👀 [mac] 开始盯着 tideq", await DevChat.SendAsync(_chat, "/watch tideq"), StringComparison.Ordinal);
        Assert.Contains("📱 mac：盯着 tideq", await DevChat.SendAsync(_chat, "/watch"), StringComparison.Ordinal);
        Assert.Contains("tideq · claude · 空闲 · 👀", await DevChat.SendAsync(_chat, "/ls"), StringComparison.Ordinal);

        agent.Status = "working";
        await Task.Delay(400);
        agent.Status = "idle";
        Assert.Equal("✅ [mac] tideq 做完了：\n\n收到", await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅", StringComparison.Ordinal)));

        Assert.StartsWith("👀 [mac] 开始盯着这台电脑上的所有 agent", await DevChat.SendAsync(_chat, "/watch all"), StringComparison.Ordinal);
        Assert.Contains("📱 mac：盯着全部 agent", await DevChat.SendAsync(_chat, "/watch"), StringComparison.Ordinal);
        Assert.StartsWith("👀 [mac] 不再盯着这台电脑上的任何 agent", await DevChat.SendAsync(_chat, "/unwatch all mac"), StringComparison.Ordinal);
        Assert.Contains("📱 mac：没有盯着任何 agent", await DevChat.SendAsync(_chat, "/watch"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_are_staged_then_delivered_with_the_next_prompt()
    {
        // F-48
        var herdr = new FakeHerdr();
        var agent = herdr.AddAgent("tideq", "TideQ");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        agent.Cwd = node.Root.CreateDirectory("proj");

        Assert.Contains("只支持 PNG", await SendImageAsync("hello"u8.ToArray()), StringComparison.Ordinal);
        Assert.StartsWith("🖼 收到图片（共 1 张待发）", await SendImageAsync(Png), StringComparison.Ordinal);
        Assert.Contains("🖼 暂存的图片 1 张", await DevChat.SendAsync(_chat, "/pending"), StringComparison.Ordinal);

        Assert.Equal("📨 已发给 [mac] tideq（带 1 张图片），做完会通知你", await DevChat.SendAsync(_chat, "@tideq 看看这个报错"));
        var text = Assert.Single(herdr.CallsStartingWith("agent", "prompt"))[3];
        Assert.StartsWith("看看这个报错\n\n（主人从手机发来了图片，请先查看：\n", text, StringComparison.Ordinal);
        var path = text.Split('\n')[3].TrimEnd('）');
        Assert.StartsWith(Path.Combine(agent.Cwd, ".chatpilot", "attachments"), path, StringComparison.Ordinal);
        Assert.Equal(Png, File.ReadAllBytes(path));

        // 发出去之后就不再暂存；/drop 丢掉还没发的。
        Assert.DoesNotContain("暂存的图片", await DevChat.SendAsync(_chat, "/pending"), StringComparison.Ordinal);
        await SendImageAsync(Png);
        Assert.Equal("🗑 丢掉了 1 张暂存的图片", await DevChat.SendAsync(_chat, "/drop"));
    }

    [Fact]
    public async Task Media_downloads_require_the_token_of_the_device_it_was_issued_to()
    {
        // S-20
        var paired = await PairAsync("mac");
        var other = await PairAsync("win");
        var media = _factory.Services.GetRequiredService<MediaStore>();
        media.Stage(Png);
        var issued = Assert.Single(media.Issue(paired.DeviceId) ?? []);
        using var http = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMediaAsync(http, issued.Id, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMediaAsync(http, issued.Id, "wrong-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetMediaAsync(http, issued.Id, other.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetMediaAsync(http, "00" + issued.Id[2..], paired.Token)).StatusCode);
        using var ok = await GetMediaAsync(http, issued.Id, paired.Token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(Png, await ok.Content.ReadAsByteArrayAsync());

        // 下载 ID 只能用一次。
        Assert.Equal(HttpStatusCode.NotFound, (await GetMediaAsync(http, issued.Id, paired.Token)).StatusCode);
    }

    [Fact]
    public async Task Diff_shows_git_changes_in_the_agent_directory()
    {
        // F-47
        var herdr = new FakeHerdr();
        var agent = herdr.AddAgent("tideq", "TideQ");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        var repo = node.Root.CreateDirectory("repo");
        agent.Cwd = repo;
        if (!Git(repo, "init", "-q", "-b", "main"))
        {
            return;
        }

        File.WriteAllText(Path.Combine(repo, "a.txt"), "旧\n");
        Git(repo, "add", "a.txt");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "新\n");

        var summary = await DevChat.SendAsync(_chat, "/diff tideq");
        Assert.StartsWith("🔀 [mac] tideq 的改动：", summary, StringComparison.Ordinal);
        Assert.Contains(" M a.txt", summary, StringComparison.Ordinal);
        var file = await DevChat.SendAsync(_chat, "/diff tideq a.txt");
        Assert.StartsWith("🔀 [mac] tideq 的 a.txt：", file, StringComparison.Ordinal);
        Assert.Contains("+新", file, StringComparison.Ordinal);
        Assert.Contains(" M a.txt", await DevChat.SendAsync(_chat, "/diff"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_lists_finished_tasks_with_today_totals()
    {
        // F-50
        Assert.StartsWith("📜 还没有任务记录", await DevChat.SendAsync(_chat, "/history"), StringComparison.Ordinal);
        var herdr = new FakeHerdr();
        herdr.AddAgent("tideq", "TideQ");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);
        await DevChat.SendAsync(_chat, "@tideq 只回复：收到");
        await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("✅", StringComparison.Ordinal));
        await DevChat.EventuallyAsync(
            async () => (await DevChat.SendAsync(_chat, "/history")).Contains("任务：只回复：收到", StringComparison.Ordinal),
            "做完的任务应该出现在 /history 里");
        var history = await DevChat.SendAsync(_chat, "/history 1");
        Assert.Contains("今天：做完 1 个，出错 0 个", history, StringComparison.Ordinal);
        Assert.Contains("✅ [mac] tideq", history, StringComparison.Ordinal);
        Assert.Contains("结果：收到", history, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_factory.DataDir, "history.json")));
    }

    [Fact]
    public async Task Wake_asks_an_online_device_on_the_same_lan()
    {
        // F-51
        var sleeper = await NodeHarness.PairAndStartAsync(_factory, "mac");
        await using var helper = await NodeHarness.PairAndStartAsync(_factory, "win");
        Assert.Equal("🟢 mac 在线，不用唤醒", await DevChat.SendAsync(_chat, "/wake mac"));
        await sleeper.DisposeAsync();
        await DevChat.EventuallyAsync(async () => (await DevChat.SendAsync(_chat, "/devices")).Contains("⚪ mac", StringComparison.Ordinal), "mac 应该离线");

        var store = _factory.Services.GetRequiredService<StateStore>();
        SetNetworks(store, "mac", null);
        Assert.StartsWith("❓ 还不知道 mac 的网卡地址", await DevChat.SendAsync(_chat, "/wake mac"), StringComparison.Ordinal);

        SetNetworks(store, "mac", [new("02:00:00:00:00:01", "192.168.50.10", 24)]);
        SetNetworks(store, "win", [new("02:00:00:00:00:02", "192.168.50.20", 24)]);
        Assert.StartsWith("⏰ 已让 win 在局域网里广播唤醒包", await DevChat.SendAsync(_chat, "/wake mac"), StringComparison.Ordinal);
        Assert.Equal(["02:00:00:00:00:01"], helper.Woken);
    }

    [Fact]
    public async Task Nodes_report_their_network_cards_when_they_connect()
    {
        var local = LocalNetwork.Describe();
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac");
        if (local.Count == 0)
        {
            return;
        }

        // 服务器发出 welcome 之后才记下网卡。
        var store = _factory.Services.GetRequiredService<StateStore>();
        await DevChat.EventuallyAsync(() => Task.FromResult(store.FindDevice("mac")?.Networks is not null), "上线后应该记下网卡");
        Assert.Equal(local.Select(n => n.Mac), store.FindDevice("mac")?.Networks?.Select(n => n.Mac));
    }

    private static void SetNetworks(StateStore store, string name, IReadOnlyList<NetworkInterfaceInfo>? networks) =>
        store.Update(state =>
        {
            var index = state.Devices.FindIndex(d => d.Name == name);
            state.Devices[index] = state.Devices[index] with { Networks = networks };
        });

    private async Task<string> SendImageAsync(byte[] data)
    {
        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _chat.PostAsync("/dev/image", content);
        response.EnsureSuccessStatusCode();
        var body = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<DevChatResponse>(response.Content, ProtocolJson.Options);
        return string.Join("\n", body?.Replies ?? []);
    }

    private async Task<PairResponse> PairAsync(string name)
    {
        var code = await NodeHarness.PairingCodeAsync(_factory, name);
        return await PairingClient.PairAsync(_factory.Server.BaseAddress, _factory.Server.CreateHandler(), new PairRequest(code, name, "TestOS"), CancellationToken.None);
    }

    private static Task<HttpResponseMessage> GetMediaAsync(HttpClient http, string id, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ProtocolInfo.MediaPath + id);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http.SendAsync(request);
    }

    private static bool Git(string directory, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            process.WaitForExit(20_000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>定时任务（F-52）：按主人的时区算时刻，到点执行，错过太久的不执行。时间用模拟的。</summary>
public sealed class ScheduleTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
    private readonly ChatPilotFactory _factory;
    private readonly HttpClient _chat;

    public ScheduleTests()
    {
        _factory = new ChatPilotFactory();
        _factory.Settings["ChatPilot:TimeZone"] = "Asia/Shanghai";
        _factory.ConfigureServicesHook = services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);
        };
        _chat = _factory.CreateDevClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _chat.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Clock_times_use_the_owner_time_zone_and_jobs_can_be_listed_and_cancelled()
    {
        // 现在是北京时间 18:00：23:00 是今天，8:00 是明天。
        var tonight = await DevChat.SendAsync(_chat, "/at 23:00 @mac/tideq 跑全量测试");
        Assert.StartsWith("⏰ 好的，定时任务 #1 会在 10-23 23:00（UTC+08:00，5 小时后）执行", tonight, StringComparison.Ordinal);
        Assert.Contains("#2 会在 10-24 08:00", await DevChat.SendAsync(_chat, "/at 8:00 整理日报"), StringComparison.Ordinal);
        Assert.Contains("#3 会在 10-24 23:00", await DevChat.SendAsync(_chat, "/at 明天 23:00 @x y"), StringComparison.Ordinal);

        var list = await DevChat.SendAsync(_chat, "/at");
        Assert.Contains("#1 10-23 23:00\n  @mac/tideq 跑全量测试", list, StringComparison.Ordinal);
        Assert.True(list.IndexOf("#1", StringComparison.Ordinal) < list.IndexOf("#2", StringComparison.Ordinal));
        Assert.Equal("🗑 已取消定时任务 #1", await DevChat.SendAsync(_chat, "/at del 1"));
        Assert.StartsWith("❓ 没有定时任务 #1", await DevChat.SendAsync(_chat, "/at del 1"), StringComparison.Ordinal);
        Assert.Contains("⏰ 定时任务 2 个", await DevChat.SendAsync(_chat, "/pending"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Due_jobs_run_like_a_message_and_missed_ones_are_reported()
    {
        var herdr = new FakeHerdr();
        herdr.AddAgent("tideq", "TideQ");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        // @序号 在定时的时候就换成名字。
        await DevChat.SendAsync(_chat, "/ls");
        await DevChat.SendAsync(_chat, "/at 30m @1 只回复：收到");
        Assert.Contains("@mac/tideq 只回复：收到", await DevChat.SendAsync(_chat, "/at"), StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromMinutes(31));
        await Scheduler().RunDueAsync(CancellationToken.None);
        // 节点的"做完了"可能比定时任务的执行结果先到，两条一起收。
        var seen = new List<string>();
        await DevChat.WaitForEventAsync(_chat, e =>
        {
            seen.Add(e);
            return seen.Any(s => s.StartsWith("⏰ 定时任务 #1 到点了", StringComparison.Ordinal)) && seen.Any(s => s.StartsWith("✅ [mac] tideq", StringComparison.Ordinal));
        });
        Assert.Contains("📨 已发给 [mac] tideq", seen.Single(s => s.StartsWith("⏰", StringComparison.Ordinal)), StringComparison.Ordinal);

        await DevChat.SendAsync(_chat, "/at 10m @tideq 晚了");
        _time.Advance(TimeSpan.FromMinutes(30));
        await Scheduler().RunDueAsync(CancellationToken.None);
        Assert.Contains("已经错过，没有执行", await DevChat.WaitForEventAsync(_chat, e => e.StartsWith("⏰ 定时任务 #2", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Single(herdr.CallsStartingWith("agent", "prompt"));
        Assert.StartsWith("⏰ 没有定时任务", await DevChat.SendAsync(_chat, "/at"), StringComparison.Ordinal);
    }

    private Scheduler Scheduler() => _factory.Services.GetServices<IHostedService>().OfType<Scheduler>().Single();
}
