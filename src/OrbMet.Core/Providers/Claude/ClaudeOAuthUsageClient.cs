using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OrbMet.Core.Providers.Claude;

/// <summary>
/// Reads the plan windows from Claude's own usage endpoint with the sign-in
/// Claude Code already holds. The desktop app's Code tab never runs the status
/// line, so this is the only source that works there. The token is read from
/// disk per call, sent only to api.anthropic.com, never refreshed, stored or
/// logged: an expired token simply yields no reading until Claude renews it.
/// </summary>
public sealed class ClaudeOAuthUsageClient
{
    private static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    private readonly string? _credentialsFile;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private ClaudeCaptureRecord? _last;

    public ClaudeOAuthUsageClient(
        string? credentialsFile = null,
        HttpClient? http = null,
        Func<DateTimeOffset>? clock = null)
    {
        _credentialsFile = credentialsFile ?? ResolveCredentialsFile();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The latest reading, asking the endpoint at most once a minute; null when none is available.</summary>
    public async Task<ClaudeCaptureRecord?> ReadAsync(CancellationToken cancellationToken)
    {
        var now = _clock();
        if (now - _lastAttempt < MinInterval)
        {
            return _last;
        }

        _lastAttempt = now;
        try
        {
            var token = ReadToken(now);
            if (token is null)
            {
                return _last;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return _last;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _last = Parse(body, now) ?? _last;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or IOException or UnauthorizedAccessException)
        {
            // No reading this time; the caller falls back to whatever it already had.
        }

        return _last;
    }

    internal static ClaudeCaptureRecord? Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var windows = new List<ClaudeCaptureWindow>();
        foreach (var key in new[] { "five_hour", "seven_day" })
        {
            if (!document.RootElement.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double? percent = node.TryGetProperty("utilization", out var used) && used.ValueKind == JsonValueKind.Number
                ? used.GetDouble()
                : null;
            DateTimeOffset? resets = node.TryGetProperty("resets_at", out var at)
                && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed.ToUniversalTime()
                    : null;
            windows.Add(new ClaudeCaptureWindow { Key = key, UsedPercent = percent, ResetsAtUtc = resets });
        }

        return windows.Count == 0
            ? null
            : new ClaudeCaptureRecord { CapturedAtUtc = now, Windows = windows };
    }

    private string? ReadToken(DateTimeOffset now)
    {
        if (_credentialsFile is null || !File.Exists(_credentialsFile))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(_credentialsFile));
        if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
            || !oauth.TryGetProperty("accessToken", out var token)
            || token.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (oauth.TryGetProperty("expiresAt", out var expires)
            && expires.ValueKind == JsonValueKind.Number
            && DateTimeOffset.FromUnixTimeMilliseconds(expires.GetInt64()) <= now)
        {
            return null;
        }

        return token.GetString();
    }

    private static string? ResolveCredentialsFile()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return Path.Combine(configDir, ".credentials.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".claude", ".credentials.json");
    }
}
