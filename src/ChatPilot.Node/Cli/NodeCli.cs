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

/// <summary>节点命令行：pair（配对）、run（运行）、doctor（自检）、update（升级）、version（版本）。</summary>
public static class NodeCli
{
    /// <summary>帮助文字：没给命令、<c>--help</c> 或命令不认识时打印。</summary>
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
          chatpilot-node update   从 GitHub 下载最新版，核对校验值后替换程序，并重启开机自启的节点
              --check                    只看有没有新版本
              --no-restart               替换后不重启（自己重启）
          chatpilot-node version  显示版本

        通用选项：
          --config <路径>  配置文件，默认 ~/.chatpilot/node.json（也可用环境变量 CHATPILOT_NODE_CONFIG）
        """;

    /// <summary>运行节点命令行，返回进程退出码：0 成功，1 出错，2 设备令牌被拒绝。</summary>
    /// <param name="args">命令行参数，第一个是命令名。</param>
    /// <returns>进程退出码；配置、配对、升级等可预期的错误打印说明后返回 1，不抛出。</returns>
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
                "update" => await UpdateAsync(cli).ConfigureAwait(false),
                "version" => Version(),
                _ => Fail($"不认识的命令：{cli.Command}\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is PairingException or FileNotFoundException or InvalidDataException or IOException
            or UnauthorizedAccessException or System.Text.Json.JsonException or UpdateException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// <c>pair</c>：先用占位的设备信息把配置校验一遍（配对码只能用一次，不能浪费在配置错误上），
    /// 再用配对码换设备令牌，成功后保存配置。
    /// </summary>
    /// <param name="cli">解析好的命令行。</param>
    /// <param name="configPath">配置文件路径；已存在时要加 <c>--force</c> 才覆盖。</param>
    /// <returns>0 配对成功；1 参数或配置有问题。</returns>
    /// <exception cref="PairingException">配对码无效、请求太频繁或连不上服务器。</exception>
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

    /// <summary>
    /// 另一个节点拿着这个配置的锁时，最多等它多久。升级重启时旧进程可能还差一点才完全退出；
    /// 等不到就说明真的有另一个节点在运行。测试里改短。
    /// </summary>
    internal static TimeSpan InstanceLockWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// <c>run</c>：先拿这个配置的单实例锁，再组装 herdr 客户端、任务跟踪、盯着 agent 的 watcher、附件下载和指令执行，
    /// 然后连上服务器一直运行，直到收到 SIGINT / SIGTERM、被另一个实例替换或设备令牌被拒绝。
    /// Windows 上启动时顺便清掉升级留下的旧程序。
    /// </summary>
    /// <param name="cli">解析好的命令行。</param>
    /// <param name="configPath">配置文件路径；任务记录、排队指令、锁文件和附件都存在它旁边。</param>
    /// <returns>
    /// 0 正常退出（包括被替换、另一个节点一直在用这个配置运行：都以 0 退出，开机自启工具就不会反复把它拉起来）；
    /// 1 参数有问题；2 设备令牌被拒绝。
    /// </returns>
    /// <exception cref="FileNotFoundException">还没配对。</exception>
    /// <exception cref="InvalidDataException">配置有问题。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限在配置目录里创建锁文件。</exception>
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

        // 两个节点用同一份配置会各自读 node.queue.json、各自发排队的指令、互相覆盖状态文件，所以先拿锁再碰这些文件。
        using var instance = await AcquireInstanceLockAsync(configPath, InstanceLockWait, logger, stopping.Token).ConfigureAwait(false);
        if (instance is null)
        {
            return 0;
        }

        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } self)
        {
            NodeUpdater.CleanUpOldBinaries(self);
        }

        var herdr = new HerdrClient(new ProcessRunner(), config.HerdrPath, config.HerdrSession, TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
        var paths = new PathPolicy(config.AllowedRoots);
        if (paths.Roots.Count == 0)
        {
            logger.LogWarning("AllowedRoots 里没有存在的目录，/new 和 /butler 将不可用");
        }

        var events = new AgentEventQueue(loggerFactory.CreateLogger<AgentEventQueue>());
        var replies = new ReplyService(herdr, config, loggerFactory.CreateLogger<ReplyService>());
        var tasks = new TrackedTasks(TrackedTasks.PathFor(configPath), loggerFactory.CreateLogger<TrackedTasks>());
        var queue = new PromptQueue(PromptQueue.PathFor(configPath), loggerFactory.CreateLogger<PromptQueue>());
        await using var tracker = new PromptTracker(herdr, replies, events, config, loggerFactory.CreateLogger<PromptTracker>(), tasks: tasks, queue: queue);
        tracker.ResumeTrackedTasks();
        await using var watcher = new AgentWatcher(herdr, replies, events, tracker, AgentWatcher.PathFor(configPath), loggerFactory.CreateLogger<AgentWatcher>());
        watcher.Start();
        var git = new GitInspector(new ProcessRunner(), config.GitPath, TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
        using var mediaHttp = new HttpClient(PinnedTransport.CreateHttpHandler(config.NormalizedFingerprint)) { Timeout = TimeSpan.FromSeconds(config.CommandTimeoutSeconds) };
        var attachments = new AttachmentDownloader(
            mediaHttp,
            new Uri(config.ServerUrl),
            config.Token,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "attachments"));
        var executor = new CommandExecutor(
            herdr, paths, tracker, replies, config, loggerFactory.CreateLogger<CommandExecutor>(), watcher, git, attachments);
        var connection = new ServerConnection(
            ServerEndpoint.WebSocketUri(new Uri(config.ServerUrl)),
            config.Token,
            config.NormalizedFingerprint,
            new ClientWebSocketConnector(),
            events,
            executor.ExecuteAsync,
            loggerFactory.CreateLogger<ServerConnection>());

        logger.LogInformation("ChatPilot 节点 {Version} 启动：设备 {Device}，服务器 {Server}", NodeUpdater.CurrentVersion(), config.DeviceName, config.ServerUrl);
        return await connection.RunAsync(stopping.Token).ConfigureAwait(false);

        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            stopping.Cancel();
        }
    }

    /// <summary>
    /// 拿这个配置的单实例锁。另一个节点拿着时最多等 <paramref name="wait"/>：升级重启时旧进程可能还没完全退出，
    /// 等它退出后接手；一直等不到就说明真的有另一个节点在用这个配置运行，这个节点不启动。
    /// </summary>
    /// <param name="configPath">配置文件路径；锁文件见 <see cref="NodeConfigStore.LockPathFor"/>。</param>
    /// <param name="wait">最多等多久。</param>
    /// <param name="logger">记录在等谁、为什么不启动。</param>
    /// <param name="cancellationToken">收到退出信号时停止等待。</param>
    /// <returns>拿到的锁，节点运行期间一直拿着；等不到或在等的时候收到退出信号时为 null。</returns>
    /// <exception cref="UnauthorizedAccessException">没有权限在配置目录里创建锁文件。</exception>
    internal static async Task<ExclusiveFileLock?> AcquireInstanceLockAsync(string configPath, TimeSpan wait, ILogger logger, CancellationToken cancellationToken)
    {
        var path = NodeConfigStore.LockPathFor(configPath);
        if (ExclusiveFileLock.TryAcquire(path) is { } acquired)
        {
            return acquired;
        }

        logger.LogWarning("另一个节点正在用这份配置运行（锁文件 {Path}），等它退出，最多等 {Seconds:0} 秒", path, wait.TotalSeconds);
        try
        {
            var waited = await ExclusiveFileLock.AcquireAsync(path, wait, cancellationToken).ConfigureAwait(false);
            if (waited is null)
            {
                logger.LogError("另一个节点一直在用这份配置运行，这个节点不启动。同一份配置同时只能运行一个节点；要重启请先停掉正在运行的那个");
            }

            return waited;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary><c>doctor</c>：逐项检查 herdr、允许的目录、各启动方式、git 和服务器健康检查，打印结果。</summary>
    /// <param name="cli">解析好的命令行。</param>
    /// <param name="configPath">配置文件路径。</param>
    /// <returns>0 herdr 和服务器都正常（目录、启动方式、git 只给警告，不影响结果）；1 参数有问题或任一必需项不正常。</returns>
    /// <exception cref="FileNotFoundException">还没配对。</exception>
    /// <exception cref="InvalidDataException">配置有问题。</exception>
    private static async Task<int> DoctorAsync(CommandLine cli, string configPath)
    {
        cli.RejectUnknown("config");
        if (cli.Errors.Count > 0)
        {
            return Fail(string.Join('\n', cli.Errors));
        }

        var config = NodeConfigStore.Load(configPath);
        var ok = true;
        Console.WriteLine($"版本：{NodeUpdater.CurrentVersion()}（升级：chatpilot-node update）");
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

        try
        {
            var version = await new ProcessRunner().RunAsync(config.GitPath, ["--version"], TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(version.ExitCode == 0
                ? $"✅ {version.StandardOutput.Trim()}（/diff 可用）"
                : $"⚠️ git 运行出错（{config.GitPath}），/diff 不可用");
        }
        catch (Exception ex) when (ex is ProcessStartException or TimeoutException)
        {
            Console.WriteLine($"⚠️ 找不到 git（{config.GitPath}），/diff 不可用；可以在配置的 gitPath 里写绝对路径");
        }

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

    /// <summary><c>version</c>：打印节点版本和运行时标识（RID）。</summary>
    /// <returns>总是 0。</returns>
    private static int Version()
    {
        Console.WriteLine($"chatpilot-node {NodeUpdater.CurrentVersion()}（{RuntimeInformation.RuntimeIdentifier}）");
        return 0;
    }

    /// <summary>升级到 GitHub 上的最新版本。只有发布版（单文件程序）能升级：用源码运行的，替换掉的只是 dotnet 的启动器。</summary>
    /// <param name="cli">解析好的命令行，可带 <c>--check</c>、<c>--no-restart</c>。</param>
    /// <returns>0 已是最新、有新版本（<c>--check</c>）或已升级；1 不能升级或没有权限替换程序。</returns>
    /// <exception cref="UpdateException">查不到版本、下载失败或校验值不符。</exception>
    private static async Task<int> UpdateAsync(CommandLine cli)
    {
        cli.RejectUnknown("check", "no-restart", "config");
        if (cli.Errors.Count > 0)
        {
            return Fail(string.Join('\n', cli.Errors));
        }

        var current = NodeUpdater.CurrentVersion();
        var self = Environment.ProcessPath;
        var rid = RuntimeInformation.RuntimeIdentifier;
        if (self is null || File.Exists(Path.Combine(AppContext.BaseDirectory, "chatpilot-node.dll")))
        {
            return Fail("只有发布版（单文件程序）能自动升级；用源码运行的，请拉取最新代码后重新打包");
        }

        if (!NodeUpdater.SupportedRuntimes.Contains(rid))
        {
            return Fail($"这个平台（{rid}）没有发布版，不能自动升级");
        }

        using var http = NodeUpdater.CreateHttpClient();
        var updater = new NodeUpdater(http, NodeUpdater.DefaultRepository, rid, self, NodeUpdater.TryRunAsync);
        Console.WriteLine($"当前版本：{current}，正在查最新版本…");
        var latest = await updater.GetLatestAsync(CancellationToken.None).ConfigureAwait(false);
        if (!latest.Version.IsNewerThan(current))
        {
            Console.WriteLine($"✅ 已经是最新版本（{current}）");
            return 0;
        }

        if (cli.Has("check"))
        {
            Console.WriteLine($"有新版本 {latest.Version}。运行 chatpilot-node update 升级");
            return 0;
        }

        Console.WriteLine($"下载 {latest.Version}（{NodeUpdater.AssetName(rid)}），核对校验值后替换 {self}…");
        try
        {
            await updater.InstallAsync(latest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail($"没有权限修改 {Path.GetDirectoryName(self)}。换有权限的用户运行，例如 sudo chatpilot-node update");
        }

        Console.WriteLine($"✅ 已升级到 {latest.Version}");
        if (cli.Has("no-restart"))
        {
            Console.WriteLine("   " + ServiceRestarter.ManualHint());
            return 0;
        }

        var restarted = await ServiceRestarter.TryRestartAsync(self, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(restarted is null
            ? "   没找到运行这个程序的开机自启服务。" + ServiceRestarter.ManualHint()
            : $"   已重启{restarted}，新版本正在运行。正在等的任务会接着跟踪");
        return 0;
    }

    /// <summary>节点运行时的日志：Information 级别，单行、带时间戳输出到控制台（开机自启时由服务管理器收集）。</summary>
    /// <returns>调用方负责释放的日志工厂。</returns>
    private static ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            }));

    /// <summary>把错误打印到标准错误。</summary>
    /// <param name="message">给用户看的说明。</param>
    /// <returns>总是 1，方便直接 <c>return Fail(…)</c>。</returns>
    private static int Fail(string message)
    {
        Console.Error.WriteLine("❌ " + message);
        return 1;
    }

    /// <summary>把操作系统描述截到 100 个字符以内再交给服务器。</summary>
    /// <param name="text">操作系统描述。</param>
    /// <returns>截短后的描述。</returns>
    private static string ClipPlatform(string text) => Shared.Text.TextChunker.Truncate(text, 100);

    /// <summary>电脑名里只保留设备名允许的字符。</summary>
    /// <returns>不允许的字符换成 <c>-</c>、截到 32 个字符的电脑名；仍不合格时为 <c>device</c>。</returns>
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
    /// <param name="command">命令名，不带扩展名。</param>
    /// <returns>第一个找到的完整路径；找不到时为 null。</returns>
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
