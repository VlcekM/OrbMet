using OrbMet.Core.Models;

namespace OrbMet.Core.Usage;

/// <summary>What the thin ring outside the main ring shows.</summary>
public enum OuterRing
{
    /// <summary>How much of the main ring's window has passed (the original window clock).</summary>
    Clock,

    /// <summary>Usage of the 5-hour limit.</summary>
    FiveHour,

    /// <summary>Usage of the weekly limit.</summary>
    Weekly,

    /// <summary>The 5-hour limit against an even burn of its window.</summary>
    FiveHourPace,

    /// <summary>The weekly limit against an even burn of its window.</summary>
    WeeklyPace,

    Off,
}

/// <summary>What the outer ring draws: a share of the circle, and whether it is past a full lap.</summary>
public readonly record struct OuterRingReading(double Fraction, bool Over);

public static class OuterRingReadings
{
    private const long FiveHourSeconds = 5 * 60 * 60;
    private const long WeekSeconds = 7 * 24 * 60 * 60;

    /// <summary>
    /// The outer ring's reading, or null when it is off or the figure is not
    /// available (no such window, its reset has passed, or a pace the window is
    /// too young for). A pace above 100% is a full circle flagged over.
    /// </summary>
    public static OuterRingReading? For(OuterRing mode, IReadOnlyList<UsageWindow> windows, UsageWindow? ringWindow, DateTimeOffset nowUtc)
    {
        switch (mode)
        {
            case OuterRing.Clock:
                return ringWindow is not null && UsagePace.Elapsed(ringWindow, nowUtc) is { } elapsed
                    ? new OuterRingReading(elapsed, false)
                    : null;

            case OuterRing.FiveHour:
            case OuterRing.Weekly:
            {
                var window = Find(windows, mode == OuterRing.FiveHour ? FiveHourSeconds : WeekSeconds, nowUtc);
                return window?.UsedPercent is { } used ? Clamp(used, over: used >= 100) : null;
            }

            case OuterRing.FiveHourPace:
            case OuterRing.WeeklyPace:
            {
                var window = Find(windows, mode == OuterRing.FiveHourPace ? FiveHourSeconds : WeekSeconds, nowUtc);
                return window is not null && UsagePace.PacePercent(window, nowUtc) is { } pace
                    ? Clamp(pace, over: pace > 100)
                    : null;
            }

            default:
                return null;
        }
    }

    private static UsageWindow? Find(IReadOnlyList<UsageWindow> windows, long seconds, DateTimeOffset nowUtc) =>
        windows.FirstOrDefault(window => window.DurationSeconds == seconds
            && (window.ResetsAtUtc is null || window.ResetsAtUtc > nowUtc));

    private static OuterRingReading Clamp(double percent, bool over) =>
        new(Math.Clamp(percent, 0, 100) / 100.0, over);
}
