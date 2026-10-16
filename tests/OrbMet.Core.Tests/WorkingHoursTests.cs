using OrbMet.Core.Models;
using OrbMet.Core.Usage;
using Xunit;

namespace OrbMet.Core.Tests;

public class WorkingHoursTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Mon 12 Oct 00:00 to Mon 19 Oct 00:00, UTC.
    private static UsageWindow Week(double used) => new()
    {
        Id = "week",
        UsedPercent = used,
        DurationSeconds = 7 * 24 * 3600,
        DurationOrigin = DurationOrigin.KnownSemantic,
        ResetsAtUtc = DateTimeOffset.Parse("2026-10-19T00:00:00Z"),
        ExhaustionEvidence = ExhaustionEvidence.Unknown,
    };

    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text);

    [Fact]
    public void Daily_hours_make_the_week_count_in_whole_days()
    {
        var hours = new WorkingHours(9, 21, Weekends: true);
        Assert.Equal(3.0 / 7, hours.ElapsedShare(Week(0), At("2026-10-15T00:00:00Z"), Utc)!.Value, precision: 6);
    }

    [Fact]
    public void Half_a_working_day_is_half_that_day()
    {
        var hours = new WorkingHours(9, 21, Weekends: true);
        // Thursday 15:00 is 6 of that day's 12 working hours: 3.5 of 7 days.
        Assert.Equal(3.5 / 7, hours.ElapsedShare(Week(0), At("2026-10-15T15:00:00Z"), Utc)!.Value, precision: 6);
    }

    [Fact]
    public void Weekends_off_removes_them_from_the_week()
    {
        var hours = new WorkingHours(9, 21, Weekends: false);
        Assert.Equal(3.0 / 5, hours.ElapsedShare(Week(0), At("2026-10-15T00:00:00Z"), Utc)!.Value, precision: 6);
        Assert.Equal(1.0, hours.ElapsedShare(Week(0), At("2026-10-17T12:00:00Z"), Utc)!.Value, precision: 6);
    }

    [Fact]
    public void Pace_is_against_working_time_only()
    {
        var hours = new WorkingHours(9, 21, Weekends: true);
        // 30% used with 3 of 7 working days gone: 70% of an even burn.
        Assert.Equal(70, UsagePace.PacePercent(Week(30), At("2026-10-15T00:00:00Z"), hours, Utc)!.Value, precision: 6);
        // The same reading against the plain clock gives the same here, since days are whole; mid-night it differs.
        var night = At("2026-10-15T06:00:00Z");
        Assert.Equal(30 / (3.0 / 7), UsagePace.PacePercent(Week(30), night, hours, Utc)!.Value, precision: 6);
        Assert.NotEqual(UsagePace.PacePercent(Week(30), night)!.Value, UsagePace.PacePercent(Week(30), night, hours, Utc)!.Value);
    }

    [Fact]
    public void A_window_wholly_outside_working_hours_says_nothing()
    {
        var night = new UsageWindow
        {
            Id = "five",
            UsedPercent = 10,
            DurationSeconds = 5 * 3600,
            DurationOrigin = DurationOrigin.KnownSemantic,
            ResetsAtUtc = At("2026-10-15T07:00:00Z"),
            ExhaustionEvidence = ExhaustionEvidence.Unknown,
        };
        Assert.Null(new WorkingHours(9, 21, true).ElapsedShare(night, At("2026-10-15T05:00:00Z"), Utc));
    }

    [Fact]
    public void Nonsense_hours_fall_back_to_the_default_span()
    {
        var settings = new DisplaySettings { WorkFromHour = 20, WorkToHour = 8, WorkWeekends = false };
        Assert.Equal(new WorkingHours(9, 21, false), settings.Working);
        Assert.Null(new DisplaySettings { PaceWorkingHours = false }.Working);
        Assert.Equal(new WorkingHours(9, 21, true), new DisplaySettings().Working);
    }

    // A week opened Fri 16 Oct 10:00, read 8 hours in (Fri 18:00).
    private static UsageWindow WeekFromFriday(double used) => Week(used) with { ResetsAtUtc = At("2026-10-23T10:00:00Z") };

    private static readonly DateTimeOffset FridayEvening = At("2026-10-16T18:00:00Z");

    [Fact]
    public void Under_an_even_working_burn_the_forecast_lasts_though_the_whole_clock_says_otherwise()
    {
        var hours = new WorkingHours(9, 21, Weekends: true);
        var window = WeekFromFriday(8.6);

        Assert.Equal(90, UsagePace.PacePercent(window, FridayEvening, hours, Utc)!.Value, precision: 0);
        Assert.True(UsagePace.Forecast(window, FridayEvening)!.RunsOutBeforeReset);
        Assert.Equal(new UsageForecast(false, null), UsagePace.Forecast(window, FridayEvening, hours, Utc));
    }

    [Fact]
    public void Over_an_even_working_burn_it_runs_out_and_the_time_counts_working_hours_only()
    {
        var hours = new WorkingHours(9, 21, Weekends: true);

        // 85% in 8 working hours: the last 15% takes about 85 minutes, before 21:00.
        var soon = UsagePace.Forecast(WeekFromFriday(85), FridayEvening, hours, Utc)!;
        Assert.True(soon.RunsOutBeforeReset);
        Assert.Equal(8.0 * 60 * 15 / 85, soon.TimeToEmpty!.Value.TotalMinutes, precision: 0);

        // 90% at 20:30: the last 10% needs about 63 working minutes, half of
        // them the next morning, so it runs out but past the two-hour horizon.
        var overnight = UsagePace.Forecast(WeekFromFriday(90), At("2026-10-16T20:30:00Z"), hours, Utc)!;
        Assert.True(overnight.RunsOutBeforeReset);
        Assert.Null(overnight.TimeToEmpty);
    }
}
