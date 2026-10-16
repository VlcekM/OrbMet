using OrbMet.Core.Usage;
using Xunit;

namespace OrbMet.Core.Tests;

public class PaceLevelTests
{
    [Theory]
    [InlineData(0, PaceLevel.Calm)]
    [InlineData(62, PaceLevel.Calm)]
    [InlineData(90, PaceLevel.Calm)]
    [InlineData(94.9, PaceLevel.Calm)]
    [InlineData(95, PaceLevel.Watch)]
    [InlineData(100, PaceLevel.Watch)]
    [InlineData(100.1, PaceLevel.High)]
    [InlineData(125, PaceLevel.High)]
    [InlineData(125.1, PaceLevel.Hot)]
    [InlineData(140, PaceLevel.Hot)]
    public void Semaphore_levels(double pace, PaceLevel expected) =>
        Assert.Equal(expected, UsagePace.LevelOf(pace));
}
