using System.Text.RegularExpressions;
using ChatPilot.Node.Config;
using ChatPilot.Node.Herdr;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>
/// 根据 herdr 给的会话引用找到 agent 的会话记录。
/// herdr 给了路径就直接用；给了会话 ID 就到这种 agent 存放会话记录的地方找。
/// </summary>
/// <remarks>
/// 会话 ID 来自本机 herdr 集成上报，仍然按白名单格式校验后才拼进路径，防止 <c>..</c> 之类的路径穿越。
/// </remarks>
/// <param name="config">节点配置（额外的 Claude 会话目录）。</param>
/// <param name="logger">日志。</param>
/// <param name="home">用户主目录。</param>
/// <param name="environment">读环境变量（CLAUDE_CONFIG_DIR、CODEX_HOME、KIMI_CODE_HOME、QWEN_HOME、XDG_DATA_HOME、COPILOT_HOME 等）。</param>
public sealed partial class TranscriptLocator(NodeConfig config, ILogger logger, string home, Func<string, string?> environment)
{
    /// <summary>用当前用户的主目录和进程环境变量。</summary>
    public TranscriptLocator(NodeConfig config, ILogger logger)
        : this(config, logger, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>能找会话记录的 agent 类型。</summary>
    public static bool Supports(string? kind) => TranscriptReader.Supports(kind) || kind is "opencode" or "kilo";

    /// <summary>找会话记录；没有会话引用、格式不支持或找不到时返回 null。</summary>
    public TranscriptSource? Find(HerdrAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Session is not { } session || agent.Kind is not { } kind || !Supports(kind))
        {
            return null;
        }

        if (session.Kind == "path")
        {
            return session.Value.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && File.Exists(session.Value)
                ? new TranscriptSource(kind, session.Value, null)
                : null;
        }

        if (session.Kind != "id" || !SessionIdRegex().IsMatch(session.Value))
        {
            return null;
        }

        try
        {
            var path = kind switch
            {
                "claude" => FindClaude(session.Value),
                "codex" => FindCodex(session.Value),
                "kimi" => FindKimi(session.Value),
                "qwen" => FindQwen(session.Value),
                "droid" => FindDroid(session.Value),
                "cursor" => FindCursor(session.Value),
                "copilot" => FindCopilot(session.Value),
                "grok" => FindGrok(session.Value),
                "opencode" or "kilo" => FindOpenCodeDatabase(kind, session.Value),
                _ => null,
            };
            return path is null ? null : new TranscriptSource(kind, path, session.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "查找 {Kind} 的会话记录 {Session} 失败", agent.Kind, session.Value);
            return null;
        }
    }

    /// <summary>Claude：各个 projects 目录下的 <c>&lt;项目&gt;/&lt;id&gt;.jsonl</c>。</summary>
    private string? FindClaude(string sessionId)
    {
        foreach (var root in ClaudeProjectRoots())
        {
            try
            {
                foreach (var project in Directory.EnumerateDirectories(root))
                {
                    var candidate = Path.Combine(project, sessionId + ".jsonl");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "跳过无法读取的会话目录 {Root}", root);
            }
        }

        return null;
    }

    /// <summary>可能存放 Claude 会话记录的目录：配置的、CLAUDE_CONFIG_DIR、~/.claude、reclaude 的各个账号。</summary>
    private IEnumerable<string> ClaudeProjectRoots()
    {
        var roots = new List<string>(config.ClaudeProjectRoots.Select(PathPolicy.ExpandHome));
        if (environment("CLAUDE_CONFIG_DIR") is { Length: > 0 } configDir)
        {
            roots.Add(Path.Combine(configDir, "projects"));
        }

        roots.Add(Path.Combine(home, ".claude", "projects"));
        var reclaudeAccounts = Path.Combine(home, ".rec", "claude", "accounts");
        try
        {
            if (Directory.Exists(reclaudeAccounts))
            {
                roots.AddRange(Directory.EnumerateDirectories(reclaudeAccounts).Select(a => Path.Combine(a, "config", "projects")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "跳过无法读取的 reclaude 账号目录");
        }

        return roots.Where(Directory.Exists).Distinct(PathPolicy.PathComparer);
    }

    /// <summary>
    /// Codex：<c>$CODEX_HOME</c>（默认 <c>~/.codex</c>）下 sessions 按日期分目录，
    /// 文件名是 <c>rollout-&lt;时间&gt;-&lt;id&gt;.jsonl</c>；归档的会话在 archived_sessions。
    /// </summary>
    private string? FindCodex(string sessionId)
    {
        var codexHome = environment("CODEX_HOME") is { Length: > 0 } custom ? custom : Path.Combine(home, ".codex");
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(codexHome, folder);
            if (!Directory.Exists(root))
            {
                continue;
            }

            var match = Directory.EnumerateFiles(root, $"rollout-*-{sessionId}.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Kimi Code：<c>$KIMI_CODE_HOME</c>（默认 <c>~/.kimi-code</c>）下 <c>sessions/&lt;工作区&gt;/session_&lt;uuid&gt;/agents/main/wire.jsonl</c>。
    /// 会话 ID 本身带 <c>session_</c> 前缀；兼容只给了 uuid 的情况。
    /// </summary>
    private string? FindKimi(string sessionId)
    {
        var kimiHome = environment("KIMI_CODE_HOME") is { Length: > 0 } custom ? custom : Path.Combine(home, ".kimi-code");
        var sessions = Path.Combine(kimiHome, "sessions");
        if (!Directory.Exists(sessions))
        {
            return null;
        }

        var folderName = sessionId.StartsWith("session_", StringComparison.Ordinal) ? sessionId : "session_" + sessionId;
        foreach (var workspace in Directory.EnumerateDirectories(sessions))
        {
            var candidate = Path.Combine(workspace, folderName, "agents", "main", "wire.jsonl");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Qwen Code：<c>$QWEN_RUNTIME_DIR</c> 或 <c>$QWEN_HOME</c>（默认 <c>~/.qwen</c>）下 <c>projects/&lt;目录&gt;/chats/&lt;id&gt;.jsonl</c>，
    /// 归档的在 <c>chats/archive</c>。目录名由工作目录换算而来、切换目录后会变，所以逐个项目找（Qwen 自己也是这样找的）。
    /// settings 里自定义的 <c>advanced.runtimeOutputDir</c> 不支持，需要时用 QWEN_RUNTIME_DIR。
    /// </summary>
    private string? FindQwen(string sessionId)
    {
        foreach (var root in new[] { environment("QWEN_RUNTIME_DIR"), environment("QWEN_HOME"), Path.Combine(home, ".qwen") })
        {
            var projects = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "projects");
            if (projects is null || !Directory.Exists(projects))
            {
                continue;
            }

            foreach (var project in Directory.EnumerateDirectories(projects))
            {
                foreach (var candidate in new[] { Path.Combine(project, "chats", sessionId + ".jsonl"), Path.Combine(project, "chats", "archive", sessionId + ".jsonl") })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Droid：<c>$FACTORY_HOME_OVERRIDE</c>（默认用户主目录）下 <c>.factory/sessions</c>，
    /// 按工作目录分子目录；和 Droid 自己一样依次找根目录、<c>btw</c> 和各个子目录。
    /// </summary>
    private string? FindDroid(string sessionId)
    {
        var factoryHome = environment("FACTORY_HOME_OVERRIDE") is { Length: > 0 } custom ? custom : home;
        var sessions = Path.Combine(factoryHome, ".factory", "sessions");
        var file = sessionId + ".jsonl";
        return FirstFile(new[] { Path.Combine(sessions, file), Path.Combine(sessions, "btw", file) }
            .Concat(Subdirectories(sessions).Select(d => Path.Combine(d, file))));
    }

    /// <summary>
    /// Cursor Agent：<c>$CURSOR_DATA_DIR</c>（默认 <c>~/.cursor</c>）下 <c>projects/&lt;目录&gt;/agent-transcripts/&lt;id&gt;/&lt;id&gt;.jsonl</c>，
    /// 旧版本直接放在 agent-transcripts 下。目录名由工作目录换算、不同目录可能重名，所以逐个项目找。
    /// </summary>
    private string? FindCursor(string sessionId)
    {
        var cursorHome = environment("CURSOR_DATA_DIR") is { Length: > 0 } custom ? custom : Path.Combine(home, ".cursor");
        return FirstFile(Subdirectories(Path.Combine(cursorHome, "projects")).SelectMany(project => new[]
        {
            Path.Combine(project, "agent-transcripts", sessionId, sessionId + ".jsonl"),
            Path.Combine(project, "agent-transcripts", sessionId + ".jsonl"),
        }));
    }

    /// <summary>GitHub Copilot CLI：<c>$COPILOT_HOME</c>（默认 <c>~/.copilot</c>）下 <c>session-state/&lt;id&gt;/events.jsonl</c>。</summary>
    private string? FindCopilot(string sessionId)
    {
        var copilotHome = environment("COPILOT_HOME") is { Length: > 0 } custom ? custom : Path.Combine(home, ".copilot");
        return FirstFile([Path.Combine(copilotHome, "session-state", sessionId, "events.jsonl")]);
    }

    /// <summary>Grok Build：<c>$GROK_HOME</c>（默认 <c>~/.grok</c>）下 <c>sessions/&lt;编码后的目录&gt;/&lt;id&gt;/updates.jsonl</c>。</summary>
    private string? FindGrok(string sessionId)
    {
        var grokHome = environment("GROK_HOME") is { Length: > 0 } custom ? custom : Path.Combine(home, ".grok");
        return FirstFile(Subdirectories(Path.Combine(grokHome, "sessions")).Select(d => Path.Combine(d, sessionId, "updates.jsonl")));
    }

    private static string? FirstFile(IEnumerable<string> candidates) => candidates.FirstOrDefault(File.Exists);

    private static IEnumerable<string> Subdirectories(string root) => Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [];

    /// <summary>
    /// opencode / kilo 的 SQLite 库：数据目录是 <c>$XDG_DATA_HOME/&lt;应用&gt;</c>（默认 <c>~/.local/share/&lt;应用&gt;</c>，Windows 也一样），
    /// 库文件可以用 <c>OPENCODE_DB</c> / <c>KILO_DB</c> 指定，否则是 <c>&lt;应用&gt;.db</c>，或者按发布通道命名的 <c>&lt;应用&gt;-&lt;通道&gt;.db</c>
    /// （kilo 还会沿用旧的 <c>opencode-&lt;通道&gt;.db</c>）。逐个打开，找含有这个会话的库。
    /// </summary>
    private string? FindOpenCodeDatabase(string kind, string sessionId)
    {
        var dataHome = environment("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
        var dataDir = Path.Combine(dataHome, kind);
        var candidates = new List<string>();
        if (environment(kind == "kilo" ? "KILO_DB" : "OPENCODE_DB") is { Length: > 0 } custom && custom != ":memory:")
        {
            candidates.Add(Path.IsPathFullyQualified(custom) ? custom : Path.Combine(dataDir, custom));
        }

        candidates.Add(Path.Combine(dataDir, kind + ".db"));
        if (Directory.Exists(dataDir))
        {
            var patterns = kind == "kilo" ? new[] { "kilo-*.db", "opencode-*.db" } : ["opencode-*.db"];
            candidates.AddRange(patterns
                .SelectMany(pattern => Directory.EnumerateFiles(dataDir, pattern))
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        return candidates.Distinct(PathPolicy.PathComparer).FirstOrDefault(c => File.Exists(c) && OpenCodeStore.HasSession(c, sessionId));
    }

    /// <summary>会话 ID 只允许字母、数字、下划线和短横线（Kimi 的 ID 形如 <c>session_&lt;uuid&gt;</c>）。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,80}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdRegex();
}

/// <summary>找到的会话记录。</summary>
/// <param name="Kind">agent 类型，决定按哪种格式读。</param>
/// <param name="Path">会话记录文件（opencode / kilo 是 SQLite 库文件）。</param>
/// <param name="SessionId">herdr 上报的会话 ID；herdr 直接给路径时为 null。</param>
public sealed record TranscriptSource(string Kind, string Path, string? SessionId)
{
    /// <summary>取最后一条回复；读不了或没有内容时返回 null。</summary>
    public string? LastReply(int maxChars) =>
        Kind is "opencode" or "kilo"
            ? SessionId is null ? null : OpenCodeStore.LastReply(Path, SessionId, maxChars)
            : TranscriptReader.LastReply(Kind, Path, maxChars);
}
