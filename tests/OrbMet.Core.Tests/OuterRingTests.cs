using OrbMet.Core.Models;
using OrbMet.Core.Usage;
using Xunit;

namespace OrbMet.Core.Tests;

public class OuterRingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");

    private static UsageWindow Window(long seconds, double used, double hoursLeft) => new()
    {
        Id = $"w{seconds}",
        UsedPercent = used,
        DurationSeconds = seconds,
        DurationOrigin = DurationOrigin.KnownSemantic,
        ResetsAtUtc = Now.AddHours(hoursLeft),
        ExhaustionEvidence = ExhaustionEvidence.Unknown,
    };

    private static readonly UsageWindow Five = Window(5 * 3600, 19, hoursLeft: 3);
    private static readonly UsageWindow Week = Window(7 * 24 * 3600, 45, hoursLeft: 84);
    private static readonly UsageWindow[] Both = { Five, Week };

    [Fact]
    public void Clock_is_time_passed_in_the_rings_window()
    {
        var reading = OuterRingReadings.For(OuterRing.Clock, Both, Week, Now)!.Value;
        Assert.Equal(0.5, reading.Fraction, precision: 6);
        Assert.False(reading.Over);
    }

    [Fact]
    public void Usage_modes_pick_their_own_window_whatever_the_ring_shows()
    {
        Assert.Equal(0.19, OuterRingReadings.For(OuterRing.FiveHour, Both, Week, Now)!.Value.Fraction, precision: 6);
        Assert.Equal(0.45, OuterRingReadings.For(OuterRing.Weekly, Both, Five, Now)!.Value.Fraction, precision: 6);
    }

    [Fact]
    public void Pace_is_against_an_even_burn_and_flags_overflow()
    {
        Assert.Equal(0.9, OuterRingReadings.For(OuterRing.WeeklyPace, Both, Five, Now)!.Value.Fraction, precision: 6);

        var ahead = new[] { Window(7 * 24 * 3600, 55, hoursLeft: 84) };
        var over = OuterRingReadings.For(OuterRing.WeeklyPace, ahead, null, Now)!.Value;
        Assert.Equal(1.0, over.Fraction);
        Assert.True(over.Over);
    }

    [Fact]
    public void Missing_or_reset_windows_and_off_give_nothing()
    {
        Assert.Null(OuterRingReadings.For(OuterRing.Weekly, new[] { Five }, Five, Now));
        Assert.Null(OuterRingReadings.For(OuterRing.Weekly, new[] { Window(7 * 24 * 3600, 45, hoursLeft: -1) }, null, Now));
        Assert.Null(OuterRingReadings.For(OuterRing.Off, Both, Week, Now));
    }

    [Fact]
    public void Switching_the_window_clock_off_turns_the_outer_ring_off()
    {
        Assert.Equal(OuterRing.Clock, new DisplaySettings().Outer);
        Assert.Equal(OuterRing.Off, (new DisplaySettings { WindowClock = false, OuterRing = OuterRing.Weekly }).Outer);
        Assert.Equal(OuterRing.Weekly, (new DisplaySettings { OuterRing = OuterRing.Weekly }).Outer);
    }
}
