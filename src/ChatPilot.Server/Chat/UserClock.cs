using System.Globalization;
using ChatPilot.Server.Options;
using Microsoft.Extensions.Options;

namespace ChatPilot.Server.Chat;

/// <summary>主人所在时区的时间：<c>/at</c> 的时刻按它计算，回复里的时间按它显示（见 <see cref="ServerOptions.TimeZone"/>）。</summary>
public sealed class UserClock(TimeProvider time, IOptions<ServerOptions> options)
{
    private readonly TimeZoneInfo _zone = options.Value.ResolveTimeZone();

    /// <summary>主人的时区。</summary>
    public TimeZoneInfo Zone => _zone;

    /// <summary>现在（UTC）。</summary>
    public DateTimeOffset UtcNow => time.GetUtcNow();

    /// <summary>现在（主人的时区）。</summary>
    public DateTimeOffset Now => ToLocal(time.GetUtcNow());

    /// <summary>转成主人的时区。</summary>
    public DateTimeOffset ToLocal(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _zone);

    /// <summary>按主人的时区格式化，例如 <c>10-24 23:00</c>。</summary>
    public string Format(DateTimeOffset value) => ToLocal(value).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>时区说明，例如 <c>UTC+08:00</c>。</summary>
    public string OffsetLabel(DateTimeOffset value)
    {
        var offset = _zone.GetUtcOffset(value);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{offset.Duration():hh\\:mm}";
    }
}
