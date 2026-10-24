using System.Text;
using ChatPilot.Shared.Text;

namespace ChatPilot.Server.Chat;

/// <summary>
/// 晚到的指令：通道断线、代理不通、服务器停机之后才收到的主人消息，以及错过了时间的定时任务。
/// 晚了超过 <see cref="MaxDelay"/> 的不再执行，只告诉主人：那时的情况可能已经变了（主人可能已经换个通道重发过了，
/// 一条迟到的 <c>/yes</c> 会同意当时还没出现的确认框）。
/// </summary>
public static class StaleCommands
{
    /// <summary>主人的指令最多晚多久还照常执行；晚到的消息、错过时间的定时任务都按它。</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);

    /// <summary>说明里最多列出几条丢掉的消息。</summary>
    private const int MaxListed = 5;

    /// <summary>每条丢掉的消息在说明里最多显示多少字。</summary>
    private const int MaxSummaryChars = 40;

    /// <summary>这条消息是不是晚到太久了。</summary>
    /// <param name="sentAt">主人发出消息的时间（聊天平台记下的）。</param>
    /// <param name="now">现在。</param>
    /// <returns>晚了超过 <see cref="MaxDelay"/> 时为 <c>true</c>；发送时间比现在还晚（时钟不准）时为 <c>false</c>。</returns>
    public static bool IsStale(DateTimeOffset sentAt, DateTimeOffset now) => now - sentAt > MaxDelay;

    /// <summary>一条丢掉的消息在说明里的样子：文字的第一部分；图片写成 <c>[图片]</c>。</summary>
    /// <param name="text">文字或图片的说明文字；没有时为 null。</param>
    /// <param name="attachment">有没有图片、文件之类的附件。</param>
    /// <returns>截短到 <see cref="MaxSummaryChars"/> 字、换行换成空格的摘要；什么都没有时为 <c>[消息]</c>。</returns>
    public static string Summarize(string? text, bool attachment)
    {
        var summary = string.IsNullOrWhiteSpace(text) ? "" : TextChunker.Truncate(text.ReplaceLineEndings(" ").Trim(), MaxSummaryChars);
        return (attachment, summary.Length) switch
        {
            (true, 0) => "[图片]",
            (true, _) => "[图片] " + summary,
            (false, 0) => "[消息]",
            _ => summary,
        };
    }

    /// <summary>告诉主人哪些晚到的消息被丢掉了、没有执行。</summary>
    /// <param name="dropped">丢掉的消息：发出时间和 <see cref="Summarize"/> 生成的摘要，按收到的顺序。</param>
    /// <param name="now">现在。</param>
    /// <returns>发给主人的说明：几条、最早的是多久前发的、各条的摘要（最多 <see cref="MaxListed"/> 条），以及时钟不准时的提示。</returns>
    /// <exception cref="ArgumentException"><paramref name="dropped"/> 为空。</exception>
    public static string Notice(IReadOnlyList<(DateTimeOffset SentAt, string Summary)> dropped, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dropped);
        if (dropped.Count == 0)
        {
            throw new ArgumentException("没有丢掉的消息", nameof(dropped));
        }

        var oldest = dropped.Min(d => d.SentAt);
        var builder = new StringBuilder()
            .Append("⏰ 有 ").Append(dropped.Count).Append(" 条消息晚到了（最早的是 ").Append(ReplyFormatter.Duration(now - oldest))
            .Append("前发的，那时服务器收不到）。情况可能已经变了，这些消息没有执行，还需要的话请重新发：");
        foreach (var (_, summary) in dropped.Take(MaxListed))
        {
            builder.Append("\n· ").Append(summary);
        }

        if (dropped.Count > MaxListed)
        {
            builder.Append("\n· ……还有 ").Append(dropped.Count - MaxListed).Append(" 条");
        }

        return builder.Append("\n（如果是刚刚才发的，说明服务器的时钟不准，请校准服务器时间）").ToString();
    }
}
