using OrbMet.Core.Usage;
using Xunit;

namespace OrbMet.Core.Tests;

public class PaceLevelTests
{
    [Theory]
    [InlineData(0, PaceLevel.Calm)]
    [InlineData(62, PaceLevel.Calm)]
    [InlineData(79.9, PaceLevel.Calm)]
    [InlineData(80, PaceLevel.Watch)]
    [InlineData(89.9, PaceLevel.Watch)]
    [InlineData(90, PaceLevel.Hot)]
    [InlineData(100, PaceLevel.Hot)]
    [InlineData(140, PaceLevel.Hot)]
    public void Semaphore_levels(double pace, PaceLevel expected) =>
        Assert.Equal(expected, UsagePace.LevelOf(pace));
}
