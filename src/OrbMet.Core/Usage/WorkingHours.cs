using OrbMet.Core.Models;

namespace OrbMet.Core.Usage;

/// <summary>
/// The hours a person actually uses Claude: from <see cref="FromHour"/> to
/// <see cref="ToHour"/> local time on each day, with or without weekends.
/// Pace is measured against these hours only, so a night's sleep does not
/// read as an allowance left unused.
/// </summary>
public sealed record WorkingHours(int FromHour, int ToHour, bool Weekends)
{
    public static readonly WorkingHours Default = new(9, 21, true);

    /// <summary>Valid when the day is a non-empty span within 0 to 24 hours.</summary>
    public bool IsValid => FromHour is >= 0 and < 24 && ToHour is > 0 and <= 24 && FromHour < ToHour;

    /// <summary>
    /// The share of the window's working time that has passed, 0 to 1, or null
    /// when the window's length or reset is unknown, it is over, or it holds no
    /// working time at all.
    /// </summary>
    public double? ElapsedShare(UsageWindow window, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(zone);

        if (!IsValid || window.DurationSeconds is not { } seconds || seconds <= 0
            || window.ResetsAtUtc is not { } resetsAt || resetsAt <= nowUtc)
        {
            return null;
        }

        var start = resetsAt - TimeSpan.FromSeconds(seconds);
        var total = WorkingSeconds(start, resetsAt, zone);
        if (total <= 0)
        {
            return null;
        }

        return Math.Clamp(WorkingSeconds(start, nowUtc < resetsAt ? nowUtc : resetsAt, zone) / total, 0, 1);
    }

    /// <summary>Working seconds between two moments.</summary>
    public double WorkingSeconds(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        if (to <= from)
        {
            return 0;
        }

        var firstDay = TimeZoneInfo.ConvertTime(from, zone).Date;
        var lastDay = TimeZoneInfo.ConvertTime(to, zone).Date;
        double sum = 0;
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (!Weekends && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var open = At(day.AddHours(FromHour), zone);
            var close = At(day.AddHours(ToHour), zone);
            var overlapStart = open > from ? open : from;
            var overlapEnd = close < to ? close : to;
            if (overlapEnd > overlapStart)
            {
                sum += (overlapEnd - overlapStart).TotalSeconds;
            }
        }

        return sum;
    }

    private static DateTimeOffset At(DateTime local, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local));
}
