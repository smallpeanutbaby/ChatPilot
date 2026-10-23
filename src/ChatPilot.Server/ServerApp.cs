using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ChatPilot.Server.Chat;
using ChatPilot.Server.Devices;
using ChatPilot.Server.Options;
using ChatPilot.Server.Security;
using ChatPilot.Server.State;
using ChatPilot.Server.WeChat;
using ChatPilot.Shared;
using ChatPilot.Shared.Protocol;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server;

/// <summary>服务器入口：<c>run</c>（默认）启动服务，<c>login</c> 交互式绑定微信，<c>fingerprint</c> 显示证书指纹。</summary>
public static class ServerApp
{
    private const string Usage = """
        ChatPilot 服务器

        用法：
          chatpilot-server [run]       启动服务（没绑定微信时会在日志里显示二维码）
          chatpilot-server login       交互式绑定微信（需要输入验证码时用这个；先停掉正在运行的服务）
          chatpilot-server fingerprint 显示服务器证书指纹

        配置在 appsettings.json 的 ChatPilot 节，也可以用环境变量覆盖，例如 ChatPilot__Port=9443。
        """;

    /// <summary>运行服务器命令行，返回进程退出码：0 成功，1 出错。</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Console.OutputEncoding = Encoding.UTF8;
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "run";
        var hostArgs = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (command is not ("run" or "login" or "fingerprint"))
        {
            Console.Error.WriteLine($"❌ 不认识的命令：{command}\n\n{Usage}");
            return 1;
        }

        var app = Build(hostArgs);
        var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        if (options.Validate() is { Count: > 0 } errors)
        {
            Console.Error.WriteLine("❌ 配置有问题：\n- " + string.Join("\n- ", errors));
            return 1;
        }

        if (command == "run")
        {
            try
            {
                await app.RunAsync().ConfigureAwait(false);
                return 0;
            }
            catch (InvalidDataException ex)
            {
                // 状态文件损坏等需要人工处理的问题：给出清楚的说明，不输出一大段堆栈。
                Console.Error.WriteLine("❌ " + ex.Message);
                return 1;
            }
        }

        if (command == "fingerprint")
        {
            Console.WriteLine(options.Tls.Enabled
                ? app.Services.GetRequiredService<CertificateProvider>().Fingerprint
                : "没有开启 TLS（ChatPilot:Tls:Enabled=false），不需要指纹");
            return 0;
        }

        var login = app.Services.GetRequiredService<WeChatLogin>();
        var ok = await login.RunAsync(
            prompt =>
            {
                Console.Write(prompt);
                return Task.FromResult(Console.ReadLine());
            },
            CancellationToken.None).ConfigureAwait(false);
        return ok ? 0 : 1;
    }

    /// <summary>构建服务器（注册服务、配置 Kestrel、映射接口），不启动。测试里也用它启动真实的 Kestrel。</summary>
    public static WebApplication Build(string[] args)
    {
        // 配置文件跟着程序走：开机自启时工作目录往往不是程序所在目录。
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        ConfigureServices(builder);
        var app = builder.Build();
        MapEndpoints(app);
        return app;
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<ServerOptions>().Bind(builder.Configuration.GetSection(ServerOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new StateStore(sp.GetRequiredService<IOptions<ServerOptions>>().Value.ResolvedDataDir));
        services.AddSingleton<CertificateProvider>();
        services.AddSingleton<AuthFailureLimiter>();
        services.AddSingleton<ServerIdentity>();
        services.AddSingleton<PairingService>();
        services.AddSingleton<ChatChannelRegistry>();
        services.AddSingleton<Notifier>();
        services.AddSingleton<DeviceHub>();
        services.AddSingleton<ChatDispatcher>();
        services.AddSingleton<DevChannel>();
        services.AddSingleton(sp => new ILinkClient(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IOptions<ServerOptions>>()));
        services.AddSingleton<WeChatLogin>();
        services.AddSingleton<WeChatChannel>();
        services.AddHostedService(sp => sp.GetRequiredService<WeChatChannel>());

        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy("pair", context => PerIp(context, permits: 5));
            limiter.AddPolicy("ws", context => PerIp(context, permits: 60));
            limiter.AddPolicy("dev", context => PerIp(context, permits: 300));
        });

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var options = kestrel.ApplicationServices.GetRequiredService<IOptions<ServerOptions>>().Value;
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
            var address = IPAddress.TryParse(options.ListenAddress, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"ListenAddress {options.ListenAddress} 不是合法的 IP 地址");
            kestrel.Listen(address, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                if (options.Tls.Enabled)
                {
                    listen.UseHttps(kestrel.ApplicationServices.GetRequiredService<CertificateProvider>().Certificate);
                }
            });
        });
    }

    private static void MapEndpoints(WebApplication app)
    {
        // 定期 ping 并要求节点回应：笔记本睡眠、断网后 20 秒左右就能判定离线，而不是等 TCP 超时（十几分钟）。
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
        app.UseRateLimiter();

        app.MapGet(ProtocolInfo.HealthPath, () => Results.Text("ok"));
        app.MapPost(ProtocolInfo.PairPath, Pair).RequireRateLimiting("pair");
        app.Map(ProtocolInfo.NodeWebSocketPath, AcceptNodeAsync).RequireRateLimiting("ws");

        var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

        // 通道在这里登记，而不是等后台服务跑起来：.NET 10 的后台服务在线程池上启动，启动瞬间的通知可能会漏掉。
        if (options.WeChat.Enabled)
        {
            app.Services.GetRequiredService<ChatChannelRegistry>().Register(app.Services.GetRequiredService<WeChatChannel>());
        }

        if (options.DevChannel.Enabled)
        {
            app.Services.GetRequiredService<ChatChannelRegistry>().Register(app.Services.GetRequiredService<DevChannel>());
            app.MapPost("/dev/chat", DevChatAsync).RequireRateLimiting("dev");
            app.MapGet("/dev/events", DevEvents).RequireRateLimiting("dev");
        }

        app.Lifetime.ApplicationStarted.Register(() => LogStartup(app));
        app.Lifetime.ApplicationStopping.Register(() =>
            app.Services.GetRequiredService<DeviceHub>().CloseAllAsync().Wait(TimeSpan.FromSeconds(5)));
    }

    private static IResult Pair(PairRequest? request, PairingService pairing)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 32
            || string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 64
            || request.Platform is null || request.Platform.Length > 200)
        {
            return Results.BadRequest(new ErrorResponse("参数不合法"));
        }

        return pairing.Redeem(request.Code, request.DeviceName, request.Platform) switch
        {
            PairingService.Paired paired => Results.Ok(new PairResponse(paired.Device.Id, paired.Device.Name, paired.Token)),
            PairingService.InvalidName invalid => Results.BadRequest(new ErrorResponse(invalid.Message)),
            _ => Results.Unauthorized(),
        };
    }

    /// <summary>先验设备令牌再升级 WebSocket：令牌不对直接 401，不会建立连接。</summary>
    private static async Task AcceptNodeAsync(HttpContext context, StateStore store, DeviceHub hub, AuthFailureLimiter failures)
    {
        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (failures.IsBlocked(source))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
        if (store.FindDeviceByToken(token) is not { } device)
        {
            failures.RecordFailure(source);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await hub.RunConnectionAsync(device, socket, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<IResult> DevChatAsync(HttpContext context, DevChatRequest? request, DevChannel channel, ChatDispatcher dispatcher)
    {
        if (channel.Authorize(context) is { } denied)
        {
            return denied;
        }

        if (request?.Text is not { Length: > 0 and <= ProtocolInfo.MaxPromptChars + 1024 } text)
        {
            return Results.BadRequest(new ErrorResponse("text 不能为空，也不能太长"));
        }

        var reply = await dispatcher.HandleAsync(text, context.RequestAborted).ConfigureAwait(false);
        return Results.Ok(new DevChatResponse(channel.Chunk(reply)));
    }

    private static IResult DevEvents(HttpContext context, DevChannel channel) =>
        channel.Authorize(context) ?? Results.Ok(new DevEventsResponse(channel.Drain()));

    private static RateLimitPartition<string> PerIp(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    private static void LogStartup(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ChatPilot.Server");
        var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        var scheme = options.Tls.Enabled ? "https" : "http";
        logger.LogInformation("ChatPilot 服务器已启动：{Scheme}://{Address}:{Port}，数据目录 {DataDir}", scheme, options.ListenAddress, options.Port, options.ResolvedDataDir);
        if (options.Tls.Enabled)
        {
            logger.LogInformation("证书指纹：{Fingerprint}", app.Services.GetRequiredService<CertificateProvider>().Fingerprint);
        }

        if (options.DevChannel.Enabled)
        {
            logger.LogWarning("测试通道已开启（只接受本机请求）");
        }

        if (app.Services.GetRequiredService<StateStore>().Devices().Count == 0)
        {
            logger.LogInformation("还没有设备：微信绑定后发 /pair 生成配对码");
        }
    }
}

/// <summary>启动时校验配置，有问题拒绝启动。</summary>
internal sealed class ServerOptionsValidator : IValidateOptions<ServerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ServerOptions options) =>
        options.Validate() is { Count: > 0 } errors ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
}
