using System.Text.RegularExpressions;
using ChatPilot.Node.Config;
using ChatPilot.Node.Herdr;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>取 agent 的最后一条回复和屏幕摘要：有 Claude 会话记录就读记录，没有就读终端。</summary>
public sealed partial class ReplyService(HerdrClient herdr, NodeConfig config, ILogger<ReplyService> logger)
{
    /// <summary>取 agent 的最后一条回复，超长时保留结尾部分。</summary>
    /// <exception cref="HerdrException">读终端失败。</exception>
    public async Task<string> GetLastReplyAsync(HerdrAgent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (FindTranscript(agent) is { } transcript
            && ReplyExtractor.FromClaudeTranscript(transcript, config.MaxReplyChars) is { Length: > 0 } fromTranscript)
        {
            return fromTranscript;
        }

        var screen = await herdr.ReadAsync(agent.PaneId, "recent-unwrapped", 400, cancellationToken).ConfigureAwait(false);
        var reply = ReplyExtractor.FromTerminal(screen, config.MaxReplyChars);
        return reply.Length > 0 ? reply : "（没有提取到回复内容，可以用 /read 查看终端）";
    }

    /// <summary>当前屏幕的摘要（去掉输入框和空行后的最后几行），用来展示确认框。</summary>
    /// <exception cref="HerdrException">读终端失败。</exception>
    public async Task<string> GetScreenExcerptAsync(string target, CancellationToken cancellationToken)
    {
        var screen = await herdr.ReadAsync(target, "visible", 80, cancellationToken).ConfigureAwait(false);
        return ReplyExtractor.ScreenExcerpt(screen);
    }

    /// <summary>找 Claude 的会话记录文件：herdr 直接给了路径就用路径，给了会话 ID 就到各个 projects 目录里找。</summary>
    internal string? FindTranscript(HerdrAgent agent)
    {
        if (agent.Session is not { } session)
        {
            return null;
        }

        if (session.Kind == "path")
        {
            return session.Value.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && File.Exists(session.Value)
                ? session.Value
                : null;
        }

        if (session.Kind != "id" || !SessionIdRegex().IsMatch(session.Value))
        {
            return null;
        }

        foreach (var root in ProjectRoots())
        {
            try
            {
                foreach (var project in Directory.EnumerateDirectories(root))
                {
                    var candidate = Path.Combine(project, session.Value + ".jsonl");
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
    private IEnumerable<string> ProjectRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>(config.ClaudeProjectRoots.Select(PathPolicy.ExpandHome));
        if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configDir)
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

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdRegex();
}
