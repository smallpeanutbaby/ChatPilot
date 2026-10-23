namespace ChatPilot.Server.Security;

/// <summary>
/// 按来源 IP 统计设备令牌验证失败的次数：一分钟内失败超过 <see cref="MaxFailuresPerMinute"/> 次后，
/// 这个 IP 在本分钟剩下的时间里直接返回 429。
/// </summary>
/// <remarks>
/// 只统计失败：正常节点断线重连不会被限流，也不会因为别人在刷错误令牌而连不上（除非共用同一个 IP，例如反向代理后面）。
/// </remarks>
public sealed class AuthFailureLimiter(TimeProvider time)
{
    /// <summary>每个来源每分钟允许的失败次数。</summary>
    public const int MaxFailuresPerMinute = 10;

    private const int MaxTrackedSources = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _failures = new(StringComparer.Ordinal);

    /// <summary>这个来源当前是否已被限流。</summary>
    public bool IsBlocked(string source)
    {
        lock (_lock)
        {
            return _failures.TryGetValue(source, out var entry)
                && time.GetUtcNow() - entry.Start < Window
                && entry.Count >= MaxFailuresPerMinute;
        }
    }

    /// <summary>记一次失败。</summary>
    public void RecordFailure(string source)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_failures.Count >= MaxTrackedSources)
            {
                // 防止被大量不同 IP 撑爆内存：清掉过期的记录。
                foreach (var expired in _failures.Where(f => now - f.Value.Start >= Window).Select(f => f.Key).ToList())
                {
                    _failures.Remove(expired);
                }
            }

            _failures[source] = _failures.TryGetValue(source, out var entry) && now - entry.Start < Window
                ? (entry.Start, entry.Count + 1)
                : (now, 1);
        }
    }
}
