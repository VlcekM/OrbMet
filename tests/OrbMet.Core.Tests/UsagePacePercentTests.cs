using OrbMet.Core.Models;
using OrbMet.Core.Usage;
using Xunit;

namespace OrbMet.Core.Tests;

public class UsagePacePercentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");

    private static UsageWindow Week(double? used, double hoursLeft) => new()
    {
        Id = "claudeCode.seven_day",
        UsedPercent = used,
        DurationSeconds = 7 * 24 * 3600,
        DurationOrigin = DurationOrigin.KnownSemantic,
        ResetsAtUtc = Now.AddHours(hoursLeft),
        ExhaustionEvidence = ExhaustionEvidence.Unknown,
    };

    [Theory]
    [InlineData(45, 90)]
    [InlineData(50, 100)]
    [InlineData(55, 110)]
    [InlineData(0, 0)]
    public void Halfway_through_the_week(double used, double expected) =>
        Assert.Equal(expected, UsagePace.PacePercent(Week(used, hoursLeft: 84), Now)!.Value, precision: 6);

    [Fact]
    public void A_window_barely_open_says_nothing() =>
        Assert.Null(UsagePace.PacePercent(Week(2, hoursLeft: 167), Now));

    [Fact]
    public void No_reading_or_no_reset_says_nothing()
    {
        Assert.Null(UsagePace.PacePercent(Week(null, hoursLeft: 84), Now));
        Assert.Null(UsagePace.PacePercent(Week(40, hoursLeft: -1), Now));
    }
}
