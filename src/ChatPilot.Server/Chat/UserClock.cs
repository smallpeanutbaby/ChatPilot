using System.Globalization;
using ChatPilot.Server.Options;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Chat;

/// <summary>主人所在时区的时间：<c>/at</c> 的时刻按它计算，回复里的时间按它显示（见 <see cref="ServerOptions.TimeZone"/>）。</summary>
/// <param name="time">时间来源，测试里可以换成假时钟。</param>
/// <param name="options">服务器配置，从中解析主人的时区。</param>
public sealed class UserClock(TimeProvider time, IOptions<ServerOptions> options)
{
    /// <summary>启动时解析好的主人时区，之后不再变。</summary>
    private readonly TimeZoneInfo _zone = options.Value.ResolveTimeZone();

    /// <summary>主人的时区。</summary>
    public TimeZoneInfo Zone => _zone;

    /// <summary>现在（UTC）。</summary>
    public DateTimeOffset UtcNow => time.GetUtcNow();

    /// <summary>现在（主人的时区）。</summary>
    public DateTimeOffset Now => ToLocal(time.GetUtcNow());

    /// <summary>转成主人的时区。</summary>
    /// <param name="value">任意时区的时刻。</param>
    /// <returns>同一时刻在主人时区下的表示。</returns>
    public DateTimeOffset ToLocal(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _zone);

    /// <summary>按主人的时区格式化，例如 <c>10-24 23:00</c>。</summary>
    /// <param name="value">要显示的时刻。</param>
    /// <returns>“月-日 时:分”格式的文字，不带年份和时区。</returns>
    public string Format(DateTimeOffset value) => ToLocal(value).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>时区说明，例如 <c>UTC+08:00</c>。</summary>
    /// <param name="value">按这个时刻取偏移，有夏令时的时区在不同日期偏移不同。</param>
    /// <returns><c>UTC+hh:mm</c> 或 <c>UTC-hh:mm</c> 形式的文字。</returns>
    public string OffsetLabel(DateTimeOffset value)
    {
        var offset = _zone.GetUtcOffset(value);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{offset.Duration():hh\\:mm}";
    }
}
