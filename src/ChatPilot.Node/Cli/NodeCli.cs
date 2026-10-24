using System.Runtime.InteropServices;
using System.Text;
using ChatPilot.Node.Config;
using ChatPilot.Node.Connection;
using ChatPilot.Node.Execution;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared;
using ChatPilot.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Cli;

/// <summary>节点命令行：pair（配对）、run（运行）、doctor（自检）。</summary>
public static class NodeCli
{
    private const string Usage = """
        ChatPilot 节点：让手机通过服务器指挥这台电脑上的 herdr agent。

        用法：
          chatpilot-node pair --server <地址> --code <配对码> [选项]
              --fingerprint <sha256:…>  服务器证书指纹（https 必填）
              --name <设备名>            默认用电脑名
              --root <目录>              允许 /new 打开的目录，可重复；默认是用户主目录
              --kind <类型>              默认 agent 类型；装了 reclaude 时默认 reclaude，否则 claude
              --herdr <路径>             herdr 可执行文件；默认在 PATH 里找
              --session <名字>           herdr 命名会话；默认用默认会话
              --force                    覆盖已有的配置
          chatpilot-node run      连接服务器并一直运行
          chatpilot-node doctor   检查配置、herdr 和服务器是否正常

        通用选项：
          --config <路径>  配置文件，默认 ~/.chatpilot/node.json（也可用环境变量 CHATPILOT_NODE_CONFIG）
        """;

    /// <summary>运行节点命令行，返回进程退出码：0 成功，1 出错，2 设备令牌被拒绝。</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cli = CommandLine.Parse(args);
        if (cli.Command is null || cli.Has("help") || cli.Command is "help")
        {
            Console.WriteLine(Usage);
            return cli.Command is null && !cli.Has("help") ? 1 : 0;
        }

        var configPath = cli.Get("config") ?? NodeConfigStore.DefaultPath;
        try
        {
            return cli.Command switch
            {
                "pair" => await PairAsync(cli, configPath).ConfigureAwait(false),
                "run" => await RunNodeAsync(cli, configPath).ConfigureAwait(false),
                "doctor" => await DoctorAsync(cli, configPath).ConfigureAwait(false),
                _ => Fail($"不认识的命令：{cli.Command}\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is PairingException or FileNotFoundException or InvalidDataException or IOException
            or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Fail(ex.Message);
        }
    }

    private static async Task<int> PairAsync(CommandLine cli, string configPath)
    {
        cli.RejectUnknown("server", "code", "fingerprint", "name", "root", "kind", "herdr", "session", "force", "config");
        if (cli.Errors.Count > 0)
        {
            return Fail(string.Join('\n', cli.Errors));
        }

        if (cli.Get("server") is not { } serverText || cli.Get("code") is not { } code)
        {
            return Fail("必须提供 --server 和 --code");
        }

        if (File.Exists(configPath) && !cli.Has("force"))
        {
            return Fail($"{configPath} 已经存在（这台电脑配对过了）。要重新配对请加 --force");
        }

        if (!Uri.TryCreate(serverText, UriKind.Absolute, out var server))
        {
            return Fail("--server 不是合法的地址");
        }

        var fingerprint = cli.Get("fingerprint");
        if (ServerEndpoint.CheckTransportSecurity(server, fingerprint) is { } transportError)
        {
            return Fail(transportError);
        }

        var name = cli.Get("name") ?? DefaultDeviceName();
        if (!Validation.IsDeviceName(name))
        {
            return Fail("设备名只能包含字母、数字、下划线、短横线，最长 32 位");
        }

        var roots = cli.GetAll("root").Select(r => Path.GetFullPath(PathPolicy.ExpandHome(r))).ToList();
        if (roots.Count == 0)
        {
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        var config = new NodeConfig
        {
            ServerUrl = server.ToString().TrimEnd('/'),
            ServerFingerprint = fingerprint,
            DeviceName = name,
            HerdrPath = cli.Get("herdr") ?? FindInPath("herdr") ?? "herdr",
            HerdrSession = cli.Get("session"),
            AllowedRoots = roots,
        };
        // reclaude 保持用命令名：它在新终端里运行，用户的 shell 能找到；写绝对路径遇到空格反而会出错。
        config.DefaultAgentKind = cli.Get("kind") ?? (FindInPath("reclaude") is not null ? "reclaude" : "claude");

        // 配对码只能用一次，所以先把配置校验完（用占位的设备信息），再去配对。
        config.DeviceId = "pending";
        config.Token = "pending";
        var preflight = config.Validate();
        if (preflight.Count > 0)
        {
            return Fail("配置有问题：\n- " + string.Join("\n- ", preflight));
        }

        using var handler = PinnedTransport.CreateHttpHandler(fingerprint);
        var response = await PairingClient.PairAsync(
            server,
            handler,
            new PairRequest(code, name, ClipPlatform(RuntimeInformation.OSDescription)),
            CancellationToken.None).ConfigureAwait(false);

        config.DeviceId = response.DeviceId;
        config.DeviceName = response.DeviceName;
        config.Token = response.Token;
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            return Fail("配置有问题：\n- " + string.Join("\n- ", errors));
        }

        NodeConfigStore.Save(configPath, config);
        Console.WriteLine($"""
            ✅ 配对成功：设备名 {config.DeviceName}
               配置已保存到 {configPath}
               允许的目录：{string.Join("、", config.AllowedRoots)}
               默认 agent 类型：{config.DefaultAgentKind}

            下一步：运行 chatpilot-node run（或按 README 设置开机自启）
            """);
        return 0;
    }

    private static async Task<int> RunNodeAsync(CommandLine cli, string configPath)
    {
        cli.RejectUnknown("config");
        if (cli.Errors.Count > 0)
        {
            return Fail(string.Join('\n', cli.Errors));
        }

        var config = NodeConfigStore.Load(configPath);
        using var loggerFactory = CreateLoggerFactory();
        var logger = loggerFactory.CreateLogger("ChatPilot.Node");
        using var stopping = new CancellationTokenSource();
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

        var herdr = new HerdrClient(new ProcessRunner(), config.HerdrPath, config.HerdrSession, TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
        var paths = new PathPolicy(config.AllowedRoots);
        if (paths.Roots.Count == 0)
        {
            logger.LogWarning("AllowedRoots 里没有存在的目录，/new 和 /butler 将不可用");
        }

        var events = new AgentEventQueue(loggerFactory.CreateLogger<AgentEventQueue>());
        var replies = new ReplyService(herdr, config, loggerFactory.CreateLogger<ReplyService>());
        var tasks = new TrackedTasks(TrackedTasks.PathFor(configPath), loggerFactory.CreateLogger<TrackedTasks>());
        await using var tracker = new PromptTracker(herdr, replies, events, config, loggerFactory.CreateLogger<PromptTracker>(), tasks: tasks);
        tracker.ResumeTrackedTasks();
        var executor = new CommandExecutor(herdr, paths, tracker, replies, config, loggerFactory.CreateLogger<CommandExecutor>());
        var connection = new ServerConnection(
            ServerEndpoint.WebSocketUri(new Uri(config.ServerUrl)),
            config.Token,
            config.NormalizedFingerprint,
            new ClientWebSocketConnector(),
            events,
            executor.ExecuteAsync,
            loggerFactory.CreateLogger<ServerConnection>());

        logger.LogInformation("ChatPilot 节点启动：设备 {Device}，服务器 {Server}", config.DeviceName, config.ServerUrl);
        return await connection.RunAsync(stopping.Token).ConfigureAwait(false);

        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            stopping.Cancel();
        }
    }

    private static async Task<int> DoctorAsync(CommandLine cli, string configPath)
    {
        cli.RejectUnknown("config");
        if (cli.Errors.Count > 0)
        {
            return Fail(string.Join('\n', cli.Errors));
        }

        var config = NodeConfigStore.Load(configPath);
        var ok = true;
        Console.WriteLine($"配置：{configPath}（设备 {config.DeviceName}）");

        var herdr = new HerdrClient(new ProcessRunner(), config.HerdrPath, config.HerdrSession, TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
        try
        {
            var agents = await herdr.ListAgentsAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"✅ herdr 正常（{config.HerdrPath}），当前 {agents.Count} 个 agent");
        }
        catch (HerdrException ex)
        {
            ok = false;
            Console.WriteLine($"❌ herdr 不可用：{ex.Message}");
        }

        var paths = new PathPolicy(config.AllowedRoots);
        Console.WriteLine(paths.Roots.Count > 0
            ? $"✅ 允许的目录：{string.Join("、", paths.Roots)}"
            : "⚠️ 没有存在的允许目录，/new 不可用");

        foreach (var (kind, launcher) in config.Launchers)
        {
            // 启动命令可能带参数；整条命令本身是个存在的文件时按文件判断，否则取第一个词。
            var command = File.Exists(launcher.Command) ? launcher.Command : launcher.Command.Split(' ', 2)[0];
            var found = Path.IsPathFullyQualified(command) ? File.Exists(command) : FindInPath(command) is not null;
            Console.WriteLine(found ? $"✅ 类型 {kind}：{launcher.Command}" : $"⚠️ 类型 {kind}：找不到 {command}");
        }

        Console.WriteLine($"   默认类型：{config.DefaultAgentKind}");

        var server = new Uri(config.ServerUrl);
        using var handler = PinnedTransport.CreateHttpHandler(config.ServerFingerprint);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await http.GetAsync(ServerEndpoint.HealthUri(server)).ConfigureAwait(false);
            ok &= response.IsSuccessStatusCode;
            Console.WriteLine(response.IsSuccessStatusCode ? $"✅ 服务器可达（{server}）" : $"❌ 服务器返回 {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ok = false;
            Console.WriteLine($"❌ 连不上服务器：{ex.Message}");
        }

        return ok ? 0 : 1;
    }

    private static ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            }));

    private static int Fail(string message)
    {
        Console.Error.WriteLine("❌ " + message);
        return 1;
    }

    private static string ClipPlatform(string text) => Shared.Text.TextChunker.Truncate(text, 100);

    /// <summary>电脑名里只保留设备名允许的字符。</summary>
    internal static string DefaultDeviceName()
    {
        var builder = new StringBuilder();
        foreach (var ch in Environment.MachineName)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '-');
        }

        var name = builder.ToString().Trim('-', '_');
        name = name.Length > 32 ? name[..32].TrimEnd('-', '_') : name;
        return Validation.IsDeviceName(name) ? name : "device";
    }

    /// <summary>在 PATH 里找可执行文件。Windows 上只认 .exe：.cmd / .bat 会经过 cmd.exe，参数不安全。</summary>
    internal static string? FindInPath(string command)
    {
        var names = OperatingSystem.IsWindows() ? new[] { command + ".exe" } : [command];
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
