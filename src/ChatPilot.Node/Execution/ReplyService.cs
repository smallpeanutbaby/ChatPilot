using ChatPilot.Node.Config;
using ChatPilot.Node.Herdr;
using Microsoft.Extensions.Logging;

namespace ChatPilot.Node.Execution;

/// <summary>取 agent 的最后一条回复和屏幕摘要：能找到会话记录就读记录，找不到就读终端。</summary>
public sealed class ReplyService(HerdrClient herdr, NodeConfig config, ILogger<ReplyService> logger)
{
    private readonly TranscriptLocator _transcripts = new(config, logger);

    /// <summary>取 agent 的最后一条回复，超长时保留结尾部分。</summary>
    /// <param name="agent">做完任务的 agent。</param>
    /// <param name="prompt">交给它的文字（知道时传入）：读终端时用来定位这一轮从哪里开始。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HerdrException">读终端失败。</exception>
    public async Task<string> GetLastReplyAsync(HerdrAgent agent, string? prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (_transcripts.Find(agent)?.LastReply(config.MaxReplyChars) is { Length: > 0 } fromTranscript)
        {
            return fromTranscript;
        }

        var screen = await herdr.ReadAsync(agent.PaneId, "recent-unwrapped", 400, cancellationToken).ConfigureAwait(false);
        var reply = ReplyExtractor.FromTerminal(screen, config.MaxReplyChars, prompt);
        return reply.Length > 0 ? reply : "（没有提取到回复内容，可以用 /read 查看终端）";
    }

    /// <summary>当前屏幕的摘要（去掉输入框和空行后的最后几行），用来展示确认框。</summary>
    /// <exception cref="HerdrException">读终端失败。</exception>
    public async Task<string> GetScreenExcerptAsync(string target, CancellationToken cancellationToken)
    {
        var screen = await herdr.ReadAsync(target, "visible", 80, cancellationToken).ConfigureAwait(false);
        return ReplyExtractor.ScreenExcerpt(screen);
    }
}
