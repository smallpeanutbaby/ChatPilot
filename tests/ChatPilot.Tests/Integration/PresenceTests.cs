using ChatPilot.Server.Chat;
using ChatPilot.Tests.Support;

namespace ChatPilot.Tests.Integration;

/// <summary>设备掉线、恢复时通知主人。</summary>
public sealed class PresenceTests
{
    [Fact]
    public async Task Offline_and_back_online_are_each_notified()
    {
        await using var factory = Factory(offlineNoticeSeconds: 1);
        using var chat = factory.CreateDevClient();
        var node = await NodeHarness.PairAndStartAsync(factory, "mac");
        var token = node.Config.Token;
        await node.DisposeAsync();

        var offline = await DevChat.WaitForEventAsync(chat, e => e.Contains("离线了", StringComparison.Ordinal));
        Assert.StartsWith("⚠️ [mac] 离线了，已经", offline, StringComparison.Ordinal);

        await using var back = NodeHarness.Start(factory, token, "mac", new FakeHerdr());
        await back.Welcome.WaitAsync(TimeSpan.FromSeconds(10));
        var online = await DevChat.WaitForEventAsync(chat, e => e.Contains("重新上线", StringComparison.Ordinal));
        Assert.StartsWith("✅ [mac] 重新上线了（离线了", online, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconnecting_within_the_grace_period_is_not_notified()
    {
        // 网络抖一下、节点升级重启，几秒内就连回来了，不打扰。
        await using var factory = Factory(offlineNoticeSeconds: 3);
        using var chat = factory.CreateDevClient();
        var node = await NodeHarness.PairAndStartAsync(factory, "mac");
        var token = node.Config.Token;
        await node.DisposeAsync();
        await using var back = NodeHarness.Start(factory, token, "mac", new FakeHerdr());
        await back.Welcome.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            DevChat.WaitForEventAsync(chat, e => e.Contains("离线", StringComparison.Ordinal) || e.Contains("上线", StringComparison.Ordinal), TimeSpan.FromSeconds(4.5)));
    }

    [Fact]
    public async Task Unpaired_devices_are_not_reported_offline()
    {
        await using var factory = Factory(offlineNoticeSeconds: 1);
        using var chat = factory.CreateDevClient();
        await using var node = await NodeHarness.PairAndStartAsync(factory, "mac");
        Assert.Contains("已撤销", await DevChat.SendAsync(chat, "/unpair mac"), StringComparison.Ordinal);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            DevChat.WaitForEventAsync(chat, e => e.Contains("离线", StringComparison.Ordinal), TimeSpan.FromSeconds(2.5)));
    }

    [Fact]
    public async Task Device_that_drops_while_the_server_restarts_is_still_reported()
    {
        // 服务器关闭时断开的连接不算掉线；重启后它一直没连回来，才通知离线。
        using var data = new TempDirectory();
        NodeHarness node;
        await using (var before = Factory(offlineNoticeSeconds: 1, data.Path))
        {
            node = await NodeHarness.PairAndStartAsync(before, "mac");
        }

        // 服务器在节点连着的时候关掉了；节点之后也没再连上。
        var token = node.Config.Token;
        await node.DisposeAsync();

        // 旧服务器清理连接时还会异步写一次状态文件（最后在线时间），等它写完，免得和新服务器读文件撞上。
        await Task.Delay(TimeSpan.FromSeconds(1));

        await using var after = Factory(offlineNoticeSeconds: 1, data.Path);
        using var chat = after.CreateDevClient();
        var offline = await DevChat.WaitForEventAsync(chat, e => e.Contains("离线了", StringComparison.Ordinal));
        Assert.StartsWith("⚠️ [mac] 离线了", offline, StringComparison.Ordinal);

        await using var back = NodeHarness.Start(after, token, "mac", new FakeHerdr());
        await back.Welcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("✅ [mac] 重新上线了", await DevChat.WaitForEventAsync(chat, e => e.Contains("重新上线", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, "不到 1 分钟")]
    [InlineData(150, "2 分钟")]
    [InlineData(3 * 3600 + 59, "3 小时")]
    [InlineData(2 * 86400 + 5, "2 天")]
    public void Durations_read_naturally(int seconds, string expected) =>
        Assert.Equal(expected, ReplyFormatter.Duration(TimeSpan.FromSeconds(seconds)));

    private static ChatPilotFactory Factory(int offlineNoticeSeconds, string? dataDir = null)
    {
        var factory = new ChatPilotFactory(dataDir);
        factory.Settings["ChatPilot:OfflineNoticeSeconds"] = offlineNoticeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return factory;
    }
}
