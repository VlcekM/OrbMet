using OrbMet.Core.Models;
using OrbMet.Core.Providers.Claude;
using OrbMet.Core.Usage;
using Xunit;

namespace OrbMet.Core.Tests;

public class CustomColourAndLiveUsageTests
{
    [Theory]
    [InlineData("#1a2B3c", "#1A2B3C")]
    [InlineData("1a2b3c", "#1A2B3C")]
    [InlineData("#fa0", "#FFAA00")]
    [InlineData(" #000000 ", "#000000")]
    public void Hex_is_normalised(string input, string expected) =>
        Assert.Equal(expected, HexColour.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    public void Bad_hex_is_not_a_colour(string? input) =>
        Assert.Null(HexColour.Normalize(input));

    [Fact]
    public void Custom_colour_wins_until_a_preset_is_picked()
    {
        var settings = new DisplaySettings().WithHex(ProviderId.ClaudeCode, "#d97757");
        Assert.Equal("#D97757", settings.HexFor(ProviderId.ClaudeCode));
        Assert.Null(settings.HexFor(ProviderId.Codex));
        Assert.Null(settings.WithColour(ProviderId.ClaudeCode, RingColour.Blue).HexFor(ProviderId.ClaudeCode));
    }

    [Fact]
    public void Invalid_stored_hex_is_ignored() =>
        Assert.Null((new DisplaySettings { ClaudeHex = "nope" }).HexFor(ProviderId.ClaudeCode));

    [Fact]
    public void Usage_endpoint_reply_becomes_windows()
    {
        var now = DateTimeOffset.Parse("2026-10-16T10:00:00Z");
        var record = ClaudeOAuthUsageClient.Parse(
            """{"five_hour":{"utilization":37.5,"resets_at":"2026-10-16T14:59:59.9+00:00"},"seven_day":{"utilization":12,"resets_at":null},"other":1}""",
            now);

        Assert.NotNull(record);
        Assert.Equal(now, record!.CapturedAtUtc);
        var five = Assert.Single(record.Windows, window => window.Key == "five_hour");
        Assert.Equal(37.5, five.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T14:59:59.9Z"), five.ResetsAtUtc);
        var seven = Assert.Single(record.Windows, window => window.Key == "seven_day");
        Assert.Equal(12, seven.UsedPercent);
        Assert.Null(seven.ResetsAtUtc);
    }

    [Fact]
    public void Reply_without_windows_is_no_reading() =>
        Assert.Null(ClaudeOAuthUsageClient.Parse("""{"error":"x"}""", DateTimeOffset.UtcNow));
}
