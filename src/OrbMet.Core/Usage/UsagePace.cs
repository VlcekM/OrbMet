using OrbMet.Core.Models;

namespace OrbMet.Core.Usage;

/// <summary>What may be said about a limit's pace (design.md 7, Tükenme tahmini).</summary>
/// <param name="RunsOutBeforeReset">On course to be spent before the window resets.</param>
/// <param name="TimeToEmpty">How long until it is spent; only when that is before the reset and within two hours.</param>
public sealed record UsageForecast(bool RunsOutBeforeReset, TimeSpan? TimeToEmpty);

/// <summary>The pace semaphore's four levels.</summary>
public enum PaceLevel
{
    Calm,
    Watch,
    High,
    Hot,
}

/// <summary>
/// How far through its window a limit is, and where its pace leads. Both come
/// from one reading: the provider's figure, its reset time and the window's
/// length. Nothing is sampled or kept, so the clock is a fact and the
/// forecast is a labelled extrapolation that never touches the figures.
/// </summary>
public static class UsagePace
{
    /// <summary>Nothing is forecast for a window barely open: one burst would read as a rate that empties it within minutes.</summary>
    public const double MinimumElapsed = 0.03;

    /// <summary>A time further out than this is not given, only whether it lasts.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(2);

    /// <summary>
    /// The share of the window gone, 0 to 1, or null when the window's length
    /// or reset is not reported, or its reset has passed.
    /// </summary>
    public static double? Elapsed(UsageWindow window, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.DurationSeconds is not { } seconds || seconds <= 0
            || window.ResetsAtUtc is not { } resetsAt || resetsAt <= nowUtc)
        {
            return null;
        }

        var left = (resetsAt - nowUtc).TotalSeconds / seconds;
        return Math.Clamp(1 - left, 0, 1);
    }

    /// <summary>
    /// Usage against an even burn, as a percentage: 100 means exactly on pace to
    /// use the whole window by its end, 50 means half of what an even burn would
    /// have used by now, 110 means ahead of it. Null when the window's progress is
    /// unknown or too young to say anything (<see cref="MinimumElapsed"/>). With
    /// <paramref name="hours"/>, only working time counts towards the window.
    /// </summary>
    public static double? PacePercent(UsageWindow window, DateTimeOffset nowUtc, WorkingHours? hours = null, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        // With working hours, "an even burn" spreads over those hours only.
        var elapsed = hours is { IsValid: true }
            ? hours.ElapsedShare(window, nowUtc, zone ?? TimeZoneInfo.Local)
            : Elapsed(window, nowUtc);
        return elapsed is { } share && share >= MinimumElapsed && window.UsedPercent is { } percent
            ? percent / share
            : null;
    }

    /// <summary>The pace semaphore: green under 95% of an even burn, yellow from 95, orange over 100, red over 125.</summary>
    public static PaceLevel LevelOf(double pacePercent) => pacePercent switch
    {
        > 125 => PaceLevel.Hot,
        > 100 => PaceLevel.High,
        >= 95 => PaceLevel.Watch,
        _ => PaceLevel.Calm,
    };

    /// <summary>
    /// Whether the limit lasts its window at the pace so far; null when nothing
    /// may be said. With <paramref name="hours"/>, the pace so far is spread over
    /// the working time gone and carried on through the working time left, the
    /// same measure as <see cref="PacePercent"/>, so the two never disagree.
    /// </summary>
    public static UsageForecast? Forecast(UsageWindow window, DateTimeOffset nowUtc, WorkingHours? hours = null, TimeZoneInfo? zone = null)
    {
        if (hours is { IsValid: true })
        {
            return WorkingForecast(window, nowUtc, hours, zone ?? TimeZoneInfo.Local);
        }

        if (Elapsed(window, nowUtc) is not { } elapsed || elapsed < MinimumElapsed
            || window.UsedPercent is not { } percent)
        {
            return null;
        }

        var used = Math.Clamp(percent / 100, 0, 1);
        if (used >= 1 || window.ExhaustionEvidence == ExhaustionEvidence.Exhausted)
        {
            return new UsageForecast(true, TimeSpan.Zero);
        }

        var untilReset = window.ResetsAtUtc!.Value - nowUtc;
        var elapsedTime = TimeSpan.FromSeconds(window.DurationSeconds!.Value * elapsed);
        if (used <= 0 || elapsedTime <= TimeSpan.Zero)
        {
            return new UsageForecast(false, null);
        }

        var untilEmpty = TimeSpan.FromSeconds((1 - used) / used * elapsedTime.TotalSeconds);
        var first = untilEmpty < untilReset;
        return new UsageForecast(first, first && untilEmpty <= Horizon ? untilEmpty : null);
    }

    private static UsageForecast? WorkingForecast(UsageWindow window, DateTimeOffset nowUtc, WorkingHours hours, TimeZoneInfo zone)
    {
        if (hours.ElapsedShare(window, nowUtc, zone) is not { } share || share < MinimumElapsed
            || window.UsedPercent is not { } percent)
        {
            return null;
        }

        var used = Math.Clamp(percent / 100, 0, 1);
        if (used >= 1 || window.ExhaustionEvidence == ExhaustionEvidence.Exhausted)
        {
            return new UsageForecast(true, TimeSpan.Zero);
        }

        var resetsAt = window.ResetsAtUtc!.Value;
        var worked = hours.WorkingSeconds(resetsAt - TimeSpan.FromSeconds(window.DurationSeconds!.Value), nowUtc, zone);
        if (used <= 0 || worked <= 0)
        {
            return new UsageForecast(false, null);
        }

        var needed = (1 - used) / used * worked;
        if (needed >= hours.WorkingSeconds(nowUtc, resetsAt, zone))
        {
            return new UsageForecast(false, null);
        }

        // The wall-clock moment the working time needed has passed: working
        // seconds only grow with time, so halving the span finds it.
        var low = nowUtc;
        var high = resetsAt;
        for (var i = 0; i < 40 && high - low > TimeSpan.FromSeconds(1); i++)
        {
            var mid = low + (high - low) / 2;
            if (hours.WorkingSeconds(nowUtc, mid, zone) >= needed)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        var untilEmpty = high - nowUtc;
        return new UsageForecast(true, untilEmpty <= Horizon ? untilEmpty : null);
    }
}
