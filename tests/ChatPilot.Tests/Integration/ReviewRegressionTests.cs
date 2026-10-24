using System.Net;
using System.Text;
using ChatPilot.Node.Cli;
using ChatPilot.Node.Connection;
using ChatPilot.Server.Devices;
using ChatPilot.Server.Security;
using ChatPilot.Server.State;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChatPilot.Tests.Integration;

/// <summary>独立代码审查发现的问题的回归测试（编号对应验收报告第七节）。</summary>
public sealed class ReviewRegressionTests : IAsyncLifetime
{
    private readonly ChatPilotFactory _factory;
    private readonly HttpClient _chat;

    public ReviewRegressionTests()
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
    public async Task Replaced_node_exits_instead_of_flapping()
    {
        // 审查 #11：两个节点用同一个令牌时，被替换的那个退出（退出码 0），不再互相顶来顶去。
        var code = await NodeHarness.PairingCodeAsync(_factory);
        var paired = await PairingClient.PairAsync(_factory.Server.BaseAddress, _factory.Server.CreateHandler(), new PairRequest(code, "mac", "x"), CancellationToken.None);
        await using var first = NodeHarness.Start(_factory, paired.Token, "mac", new FakeHerdr());
        await first.Welcome.WaitAsync(TimeSpan.FromSeconds(10));
        await using var second = NodeHarness.Start(_factory, paired.Token, "mac", new FakeHerdr());
        await second.Welcome.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, await first.RunTask.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(1500);
        Assert.False(second.RunTask.IsCompleted);
        Assert.Contains("🟢 mac", await DevChat.SendAsync(_chat, "/devices"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unpair_takes_effect_even_when_saving_fails()
    {
        // 审查 #5：写盘失败时撤销也要立即生效（断开连接、令牌不能再用），并如实告诉用户。
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac");
        await using (await StateFileBlocker.BlockAsync(_factory.Services.GetRequiredService<StateStore>().FilePath))
        {
            var reply = await DevChat.SendAsync(_chat, "/unpair mac");
            Assert.Contains("保存到磁盘失败", reply, StringComparison.Ordinal);
            Assert.Equal(2, await node.RunTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("还没有设备", await DevChat.SendAsync(_chat, "/devices"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Failed_writes_leave_memory_unchanged_for_normal_updates()
    {
        // 审查 #5：普通修改是事务式的，写盘失败时内存不变。
        var store = _factory.Services.GetRequiredService<StateStore>();
        store.Update(s => s.SelectedDeviceId = "before");
        await using (await StateFileBlocker.BlockAsync(store.FilePath))
        {
            // 被同名目录占住时，Windows 上抛的是 UnauthorizedAccessException，其他系统是 IOException。
            var error = Assert.ThrowsAny<Exception>(() => store.Update(s => s.SelectedDeviceId = "after"));
            Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
            Assert.Equal("before", store.Read(s => s.SelectedDeviceId));
            Assert.False(store.UpdateBestEffort(s => s.SelectedDeviceId = "best-effort"));
            Assert.Equal("best-effort", store.Read(s => s.SelectedDeviceId));
        }
    }

    [Fact]
    public async Task Repeated_bad_tokens_are_throttled_per_source_but_good_nodes_still_connect()
    {
        // 审查 #14：只统计令牌错误的请求，超过 10 次 / 分钟后返回 429。
        using var http = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < AuthFailureLimiter.MaxFailuresPerMinute + 1; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProtocolInfo.NodeWebSocketPath);
            request.Headers.Add("Authorization", "Bearer wrong");
            request.Headers.Add(ChatPilotFactory.RemoteIpHeader, "203.0.113.50");
            statuses.Add((await http.SendAsync(request)).StatusCode);
        }

        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Unauthorized, AuthFailureLimiter.MaxFailuresPerMinute).Append(HttpStatusCode.TooManyRequests), statuses);

        // 其他来源的正常节点不受影响。
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac");
        Assert.Contains("🟢 mac", await DevChat.SendAsync(_chat, "/devices"), StringComparison.Ordinal);
    }
}

/// <summary>不需要服务器的回归单元测试。</summary>
public class ReviewRegressionUnitTests
{
    [Theory]
    [InlineData("""{"type":"command","id":"abc","command":{"op":"format_disk"}}""", "abc")]
    [InlineData("""{"type":"command","id":"abc","command":{"op":"prompt"}}""", "abc")]
    [InlineData("""{"type":"result","id":"abc"}""", null)]
    [InlineData("""{"type":"command","id":5}""", null)]
    [InlineData("""not json""", null)]
    public void Unknown_commands_are_answered_individually_instead_of_dropping_the_connection(string json, string? expectedId)
    {
        // 审查 #12：只有"结构正常但 op 不认识"的指令才单独回错误，其他不合规消息仍然断开。
        Assert.Equal(expectedId, ServerConnection.UnsupportedCommandId(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Device_events_are_rate_limited_per_minute()
    {
        // 审查 #12：一台设备不能用通知刷屏。
        var connection = new NodeConnection(
            new DeviceRecord { Id = "d", Name = "mac", TokenHash = "h" },
            new DeviceSession("d", "h", "p", "v", DateTimeOffset.UtcNow),
            new System.Net.WebSockets.ClientWebSocket());
        var now = DateTimeOffset.Parse("2026-10-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var accepted = Enumerable.Range(0, 25).Count(_ => connection.TryCountEvent(now, DeviceHub.MaxEventsPerMinute));
        Assert.Equal(DeviceHub.MaxEventsPerMinute, accepted);
        Assert.True(connection.TryCountEvent(now.AddMinutes(1), DeviceHub.MaxEventsPerMinute));
    }

    [Fact]
    public void Auth_failure_window_resets_after_a_minute()
    {
        var time = new FakeTimeProvider();
        var limiter = new AuthFailureLimiter(time);
        for (var i = 0; i < AuthFailureLimiter.MaxFailuresPerMinute; i++)
        {
            Assert.False(limiter.IsBlocked("1.2.3.4"));
            limiter.RecordFailure("1.2.3.4");
        }

        Assert.True(limiter.IsBlocked("1.2.3.4"));
        Assert.False(limiter.IsBlocked("5.6.7.8"));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(limiter.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public async Task Pairing_validates_the_config_before_spending_the_one_time_code()
    {
        // 审查 #16：配置有问题时不去连服务器，配对码不会被白白用掉。
        using var temp = new TempDirectory();
        var configPath = temp.Combine("node.json");
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            var exit = await NodeCli.RunAsync(["pair", "--server", "http://127.0.0.1:9", "--code", "AAAA-AAAA", "--session", "Bad Session", "--config", configPath]);
            Assert.Equal(1, exit);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("HerdrSession", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("连不上服务器", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(configPath));
    }

    [Fact]
    public void Script_wrappers_are_rejected_as_herdr_path()
    {
        // 审查 #3：Windows 上 .cmd/.bat 会经过 cmd.exe，参数不安全。
        var config = new ChatPilot.Node.Config.NodeConfig
        {
            ServerUrl = "http://127.0.0.1:8080",
            DeviceId = "d",
            DeviceName = "win",
            Token = "t",
            HerdrPath = @"C:\Users\me\AppData\Roaming\npm\herdr.cmd",
        };
        Assert.Contains(config.Validate(), e => e.Contains(".cmd", StringComparison.Ordinal));
        config.HerdrPath = @"C:\Tools\herdr.exe";
        Assert.Empty(config.Validate());
    }
}
