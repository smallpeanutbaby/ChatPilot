using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using ChatPilot.Node.Connection;
using ChatPilot.Server.Chat;
using ChatPilot.Server.State;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatPilot.Tests.Integration;

/// <summary>安全验收（S-xx）。</summary>
public sealed class SecurityTests : IAsyncLifetime
{
    private readonly ChatPilotFactory _factory;
    private readonly HttpClient _chat;

    public SecurityTests()
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
    public async Task WebSocket_without_or_with_wrong_token_gets_401_and_is_not_upgraded()
    {
        // S-02
        using var plain = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.GetAsync(ProtocolInfo.NodeWebSocketPath)).StatusCode);

        var connector = new TestServerConnector(_factory.Server);
        await Assert.ThrowsAsync<NodeUnauthorizedException>(() => connector.ConnectAsync(new Uri("ws://localhost/ws/node"), "wrong-token", null, CancellationToken.None));
        await Assert.ThrowsAsync<NodeUnauthorizedException>(() => connector.ConnectAsync(new Uri("ws://localhost/ws/node"), "", null, CancellationToken.None));
    }

    [Fact]
    public async Task Pairing_code_is_single_use_and_wrong_codes_get_401()
    {
        // S-03
        var code = await NodeHarness.PairingCodeAsync(_factory);
        using var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/pair", new { code, device_name = "mac", platform = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/pair", new { code, device_name = "mac2", platform = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/pair", new { code = "", device_name = "mac2", platform = "x" })).StatusCode);
    }

    [Fact]
    public async Task Pairing_is_rate_limited_per_ip()
    {
        // S-04：每分钟 5 次，第 6 次返回 429。
        using var http = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pair")
            {
                Content = JsonContent.Create(new { code = "AAAA-AAAA", device_name = "x", platform = "x" }),
            };
            request.Headers.Add(ChatPilotFactory.RemoteIpHeader, "203.0.113.7");
            statuses.Add((await http.SendAsync(request)).StatusCode);
        }

        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Unauthorized, 5).Append(HttpStatusCode.TooManyRequests), statuses);

        // 另一个 IP 不受影响。
        using var other = new HttpRequestMessage(HttpMethod.Post, "/api/pair") { Content = JsonContent.Create(new { code = "AAAA-AAAA", device_name = "x", platform = "x" }) };
        other.Headers.Add(ChatPilotFactory.RemoteIpHeader, "203.0.113.8");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(other)).StatusCode);
    }

    [Fact]
    public async Task State_file_never_contains_plaintext_tokens_and_logs_do_not_leak_secrets()
    {
        // S-05 / S-13 / S-14
        var code = await NodeHarness.PairingCodeAsync(_factory);
        var paired = await PairingClient.PairAsync(_factory.Server.BaseAddress, _factory.Server.CreateHandler(), new PairRequest(code, "mac", "x"), CancellationToken.None);
        await using var node = NodeHarness.Start(_factory, paired.Token, "mac", new FakeHerdr());
        await node.Welcome.WaitAsync(TimeSpan.FromSeconds(10));
        await DevChat.SendAsync(_chat, "/ls");

        var store = _factory.Services.GetRequiredService<StateStore>();
        var state = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain(paired.Token, state, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        }

        var logs = _factory.Logs.AllText;
        Assert.DoesNotContain(paired.Token, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(code, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(code.Replace("-", "", StringComparison.Ordinal), logs, StringComparison.Ordinal);
        Assert.DoesNotContain(ChatPilotFactory.DevToken, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_frames_close_the_connection()
    {
        // S-11
        var socket = await ConnectAuthenticatedAsync();
        await socket.SendAsync(ProtocolJson.SerializeToUtf8(new HelloMessage(ProtocolInfo.Version, "t", "t", "t")), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.IsType<WelcomeMessage>(await WebSocketMessaging.ReceiveAsync(socket, ProtocolInfo.MaxFrameBytes, CancellationToken.None));

        var huge = Encoding.UTF8.GetBytes("{\"type\":\"result\",\"id\":\"" + new string('x', ProtocolInfo.MaxFrameBytes) + "\"}");
        try
        {
            await socket.SendAsync(huge, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
        }

        await AssertClosedAsync(socket);
    }

    [Fact]
    public async Task Garbage_and_node_side_forbidden_messages_close_the_connection()
    {
        // S-09（服务器侧）：节点发来协议外或不该由节点发送的消息，直接断开。
        var socket = await ConnectAuthenticatedAsync();
        await socket.SendAsync(ProtocolJson.SerializeToUtf8(new HelloMessage(ProtocolInfo.Version, "t", "t", "t")), WebSocketMessageType.Text, true, CancellationToken.None);
        await WebSocketMessaging.ReceiveAsync(socket, ProtocolInfo.MaxFrameBytes, CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"command","id":"1","command":{"op":"list_agents"}}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        await AssertClosedAsync(socket);

        var second = await ConnectAuthenticatedAsync();
        await second.SendAsync(Encoding.UTF8.GetBytes("""{"type":"shell","cmd":"id"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        await AssertClosedAsync(second);
    }

    [Fact]
    public async Task Protocol_version_mismatch_is_rejected_with_reason()
    {
        // S-16
        var socket = await ConnectAuthenticatedAsync();
        await socket.SendAsync(ProtocolJson.SerializeToUtf8(new HelloMessage(99, "t", "t", "t")), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Null(await WebSocketMessaging.ReceiveAsync(socket, ProtocolInfo.MaxFrameBytes, CancellationToken.None));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.Contains("protocol version mismatch", socket.CloseStatusDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dev_channel_requires_loopback_and_token()
    {
        // S-12
        using var noToken = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.PostAsJsonAsync("/dev/chat", new { text = "/help" })).StatusCode);

        using var wrongToken = _factory.CreateClient();
        wrongToken.DefaultRequestHeaders.Add(DevChannel.TokenHeader, "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongToken.GetAsync("/dev/events")).StatusCode);

        using var remote = new HttpRequestMessage(HttpMethod.Post, "/dev/chat") { Content = JsonContent.Create(new { text = "/help" }) };
        remote.Headers.Add(DevChannel.TokenHeader, ChatPilotFactory.DevToken);
        remote.Headers.Add(ChatPilotFactory.RemoteIpHeader, "198.51.100.1");
        Assert.Equal(HttpStatusCode.Forbidden, (await _chat.SendAsync(remote)).StatusCode);
    }

    [Fact]
    public async Task Path_traversal_and_shell_metacharacters_through_chat_are_harmless()
    {
        // S-07 / S-08（端到端）
        var herdr = new FakeHerdr();
        herdr.AddAgent("tideq", "TideQ");
        await using var node = await NodeHarness.PairAndStartAsync(_factory, "mac", herdr);

        var traversal = await DevChat.SendAsync(_chat, $"/new {node.Root.Path}/../..");
        Assert.Contains("不在允许的目录内", traversal, StringComparison.Ordinal);
        Assert.Empty(herdr.CallsStartingWith("workspace", "create"));

        const string payload = "x\"; rm -rf ~; echo \"$(whoami)`id`\n--wait";
        await DevChat.SendAsync(_chat, "@tideq " + payload);
        var call = Assert.Single(herdr.CallsStartingWith("agent", "prompt"));
        Assert.Equal(payload, call[3]);
    }

    [Fact]
    public async Task Disk_write_failures_during_disconnect_do_not_crash_the_server()
    {
        // 回归：设备断开时写"最后在线时间"失败，曾经导致整个进程崩溃。
        // 用同名目录占住 state.json，让原子替换必然失败（改目录权限没用：写入前会把目录重设为 700）。
        var node = await NodeHarness.PairAndStartAsync(_factory, "mac");
        var statePath = _factory.Services.GetRequiredService<StateStore>().FilePath;
        var backup = statePath + ".bak";
        File.Move(statePath, backup);
        Directory.CreateDirectory(statePath);
        try
        {
            await node.DisposeAsync();
            await DevChat.EventuallyAsync(
                () => Task.FromResult(_factory.Logs.Lines.Any(l => l.Contains("保存设备最后在线时间失败", StringComparison.Ordinal))),
                "写失败应该只记一条警告");
            Assert.Contains("⚪ mac", await DevChat.SendAsync(_chat, "/devices"), StringComparison.Ordinal);
            Assert.Contains("ChatPilot 指令", await DevChat.SendAsync(_chat, "/help"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(statePath, recursive: true);
            File.Move(backup, statePath);
        }
    }

    private async Task<WebSocket> ConnectAuthenticatedAsync()
    {
        var code = await NodeHarness.PairingCodeAsync(_factory);
        var paired = await PairingClient.PairAsync(_factory.Server.BaseAddress, _factory.Server.CreateHandler(), new PairRequest(code, "raw" + Guid.NewGuid().ToString("N")[..6], "x"), CancellationToken.None);
        return await new TestServerConnector(_factory.Server).ConnectAsync(new Uri("ws://localhost/ws/node"), paired.Token, null, CancellationToken.None);
    }

    private static async Task AssertClosedAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var message = await WebSocketMessaging.ReceiveAsync(socket, ProtocolInfo.MaxFrameBytes, timeout.Token);
                if (message is null)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or WireProtocolException)
        {
            // 连接被服务器中断，也算关闭。
        }
    }
}

/// <summary>测试通道默认关闭（S-12），配置不安全时拒绝启动（S-15）。</summary>
public class HardeningDefaultsTests
{
    [Fact]
    public async Task Dev_channel_is_off_by_default()
    {
        await using var factory = new ChatPilotFactory();
        factory.Settings["ChatPilot:DevChannel:Enabled"] = "false";
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DevChannel.TokenHeader, ChatPilotFactory.DevToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/dev/chat", new { text = "/help" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/events")).StatusCode);
    }

    [Fact]
    public async Task Refuses_to_start_without_tls_on_public_interface()
    {
        await using var factory = new ChatPilotFactory();
        factory.Settings["ChatPilot:ListenAddress"] = "0.0.0.0";
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("回环地址", ex.ToString(), StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public void Shipped_configuration_is_secure_by_default()
    {
        var options = new ChatPilot.Server.Options.ServerOptions();
        Assert.True(options.Tls.Enabled);
        Assert.False(options.DevChannel.Enabled);
        Assert.Empty(options.Validate());
        Assert.IsAssignableFrom<IOptions<ChatPilot.Server.Options.ServerOptions>>(Microsoft.Extensions.Options.Options.Create(options));
    }
}
