using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using ChatPilot.Node.Config;
using ChatPilot.Node.Connection;
using ChatPilot.Node.Execution;
using ChatPilot.Node.Herdr;
using ChatPilot.Server.Chat;
using ChatPilot.Shared.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatPilot.Tests.Support;

/// <summary>在内存里运行的服务器（TestServer），微信关闭，测试通道打开。</summary>
internal sealed class ChatPilotFactory : WebApplicationFactory<Program>
{
    public const string DevToken = "test-dev-token-0123456789";

    /// <summary>测试里用这个请求头模拟远程客户端的 IP。</summary>
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    private readonly TempDirectory? _ownedDataDir;

    /// <param name="dataDir">数据目录；不传时用一个新的临时目录（测试结束删除）。传入已有目录可以模拟服务器重启。</param>
    public ChatPilotFactory(string? dataDir = null)
    {
        if (dataDir is null)
        {
            _ownedDataDir = new TempDirectory();
            dataDir = _ownedDataDir.Path;
        }

        DataDir = dataDir;
    }

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["ChatPilot:Tls:Enabled"] = "false",
        ["ChatPilot:ListenAddress"] = "127.0.0.1",
        ["ChatPilot:WeChat:Enabled"] = "false",
        ["ChatPilot:DevChannel:Enabled"] = "true",
        ["ChatPilot:DevChannel:Token"] = DevToken,
        ["ChatPilot:PublicUrl"] = "https://chatpilot.example.com:8443",
    };

    public Action<IServiceCollection>? ConfigureServicesHook { get; set; }

    public CapturingLoggerProvider Logs { get; } = new();

    public string DataDir { get; }

    public HttpClient CreateDevClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(DevChannel.TokenHeader, DevToken);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ChatPilot:DataDir", DataDir);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            ConfigureServicesHook?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _ownedDataDir?.Dispose();
        }
    }

    /// <summary>TestServer 默认没有客户端 IP；这里默认设成回环地址，带了测试请求头时用请求头里的地址。</summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue(RemoteIpHeader, out var ip)
                    ? IPAddress.Parse(ip.ToString())
                    : IPAddress.Loopback;
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>通过 TestServer 建立 WebSocket 连接（代替真实的 ClientWebSocket）。</summary>
internal sealed class TestServerConnector(TestServer server) : IWebSocketConnector
{
    public async Task<WebSocket> ConnectAsync(Uri uri, string token, string? fingerprint, CancellationToken cancellationToken)
    {
        var client = server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
        try
        {
            return await client.ConnectAsync(new Uri(server.BaseAddress, ProtocolInfo.NodeWebSocketPath), cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("401", StringComparison.Ordinal))
        {
            throw new NodeUnauthorizedException("401");
        }
    }
}

/// <summary>一个跑在测试进程里的节点：真实的连接层和执行层 + 模拟的 herdr。</summary>
internal sealed class NodeHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly PromptTracker _tracker;
    private readonly TaskCompletionSource<WelcomeMessage> _welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private NodeHarness(
        FakeHerdr herdr, NodeConfig config, ServerConnection connection, PromptTracker tracker, AgentWatcher watcher, ConcurrentQueue<string> woken, TempDirectory root)
    {
        Herdr = herdr;
        Config = config;
        Connection = connection;
        _tracker = tracker;
        Watcher = watcher;
        Woken = woken;
        Root = root;
        connection.Connected += welcome => _welcome.TrySetResult(welcome);
        RunTask = connection.RunAsync(_stop.Token);
    }

    public FakeHerdr Herdr { get; }

    public NodeConfig Config { get; }

    public ServerConnection Connection { get; }

    public TempDirectory Root { get; }

    public AgentWatcher Watcher { get; }

    /// <summary>节点被要求广播唤醒包的 MAC 地址（不真的发包）。</summary>
    public ConcurrentQueue<string> Woken { get; }

    public Task<int> RunTask { get; }

    public Task<WelcomeMessage> Welcome => _welcome.Task;

    /// <summary>节点是否还在跟踪（或正在提交）这个 pane 上的任务；这时再派活会排队。</summary>
    public bool IsTracking(string paneId) => _tracker.IsTracking(paneId);

    /// <summary>用配对码配对并启动节点，等到握手成功。</summary>
    public static async Task<NodeHarness> PairAndStartAsync(ChatPilotFactory factory, string deviceName, FakeHerdr? herdr = null)
    {
        var code = await PairingCodeAsync(factory, deviceName);
        var paired = await PairingClient.PairAsync(factory.Server.BaseAddress, factory.Server.CreateHandler(), new PairRequest(code, deviceName, "TestOS"), CancellationToken.None);
        var node = Start(factory, paired.Token, paired.DeviceName, herdr ?? new FakeHerdr());
        await node.Welcome.WaitAsync(TimeSpan.FromSeconds(10));
        return node;
    }

    public static NodeHarness Start(ChatPilotFactory factory, string token, string deviceName, FakeHerdr herdr)
    {
        var root = new TempDirectory();
        var config = new NodeConfig
        {
            ServerUrl = "http://localhost",
            DeviceName = deviceName,
            DeviceId = "test",
            Token = token,
            AllowedRoots = [root.Path],
            CommandTimeoutSeconds = 10,
        };
        var client = new HerdrClient(herdr, "herdr", null, TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
        var events = new AgentEventQueue(NullLogger<AgentEventQueue>.Instance);
        var replies = new ReplyService(client, config, NullLogger<ReplyService>.Instance);
        var tracker = new PromptTracker(client, replies, events, config, NullLogger<PromptTracker>.Instance, initialPromptGrace: TimeSpan.FromMilliseconds(50));
        var watcher = new AgentWatcher(client, replies, events, tracker, null, NullLogger.Instance, TimeSpan.FromMilliseconds(100));
        var attachments = new AttachmentDownloader(new HttpClient(factory.Server.CreateHandler()), factory.Server.BaseAddress, token, root.Combine("attachments"));
        var woken = new ConcurrentQueue<string>();
        var executor = new CommandExecutor(
            client, new PathPolicy(config.AllowedRoots), tracker, replies, config, NullLogger<CommandExecutor>.Instance,
            watcher, new GitInspector(new ProcessRunner(), "git", TimeSpan.FromSeconds(10)), attachments,
            (macs, _) =>
            {
                foreach (var mac in macs)
                {
                    woken.Enqueue(mac);
                }

                return Task.FromResult(macs.Count);
            });
        var connection = new ServerConnection(
            new Uri(factory.Server.BaseAddress, ProtocolInfo.NodeWebSocketPath),
            token,
            null,
            new TestServerConnector(factory.Server),
            events,
            executor.ExecuteAsync,
            NullLogger<ServerConnection>.Instance);
        watcher.Start();
        return new NodeHarness(herdr, config, connection, tracker, watcher, woken, root);
    }

    /// <summary>在测试通道里发 /pair，从回复里取出配对码。</summary>
    public static async Task<string> PairingCodeAsync(ChatPilotFactory factory, string? deviceName = null)
    {
        using var client = factory.CreateDevClient();
        var reply = await DevChat.SendAsync(client, deviceName is null ? "/pair" : "/pair " + deviceName);
        var match = System.Text.RegularExpressions.Regex.Match(reply, "配对码：([A-Z0-9]{4}-[A-Z0-9]{4})");
        Assert.True(match.Success, reply);
        return match.Groups[1].Value;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync();
        try
        {
            await RunTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }

        await Watcher.DisposeAsync();
        await _tracker.DisposeAsync();
        _stop.Dispose();
        Root.Dispose();
    }
}

/// <summary>测试通道的辅助方法。</summary>
internal static class DevChat
{
    public static async Task<string> SendAsync(HttpClient client, string text)
    {
        using var response = await client.PostAsJsonAsync("/dev/chat", new { text });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DevChatResponse>(ProtocolJson.Options);
        return string.Join("\n", body?.Replies ?? []);
    }

    /// <summary>轮询主动通知，直到出现满足条件的一条。</summary>
    public static async Task<string> WaitForEventAsync(HttpClient client, Func<string, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var seen = new List<string>();
        while (DateTime.UtcNow < deadline)
        {
            var body = await client.GetFromJsonAsync<DevEventsResponse>("/dev/events", ProtocolJson.Options);
            foreach (var item in body?.Events ?? [])
            {
                seen.Add(item);
                if (predicate(item))
                {
                    return item;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("没有等到期望的通知。收到的：\n" + string.Join("\n---\n", seen));
    }

    /// <summary>等条件成立（例如设备上线 / 下线）。</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string message, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(message);
    }
}
