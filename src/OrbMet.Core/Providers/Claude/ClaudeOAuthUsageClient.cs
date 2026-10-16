using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrbMet.Core.Storage;

namespace OrbMet.Core.Providers.Claude;

/// <summary>
/// Reads the plan windows from Claude's own usage endpoint with the sign-in
/// Claude Code already holds. The desktop app's Code tab never runs the status
/// line, so this is the only source that works there. The token is read from
/// disk, sent only to Anthropic, and never logged. When it has expired it is
/// renewed the way Claude Code does it and the new tokens are written back to
/// the same file, so Claude Code keeps working from what OrbMet saved.
/// </summary>
public sealed class ClaudeOAuthUsageClient
{
    private static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly Uri RefreshEndpoint = new("https://platform.claude.com/v1/oauth/token");
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(55);
    private static readonly TimeSpan RefreshBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    private readonly string? _credentialsFile;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefreshAttempt = DateTimeOffset.MinValue;
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

    /// <summary>The latest reading, asking the endpoint about once a minute; null when none is available.</summary>
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
            var token = await GetTokenAsync(now, cancellationToken).ConfigureAwait(false);
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
                Log($"usage {(int)response.StatusCode}");
                return _last;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _last = Parse(body, now) ?? _last;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No reading this time; the caller falls back to whatever it already had.
            Log($"usage failed: {ex.GetType().Name}");
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

    /// <summary>The access token, renewed first when it has expired or is about to.</summary>
    private async Task<string?> GetTokenAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_credentialsFile is null || !File.Exists(_credentialsFile))
        {
            return null;
        }

        // Re-read right before deciding: Claude Code may have renewed it since.
        var root = JsonNode.Parse(File.ReadAllText(_credentialsFile));
        if (root?["claudeAiOauth"] is not JsonObject oauth || oauth["accessToken"]?.GetValue<string>() is not { Length: > 0 } token)
        {
            return null;
        }

        DateTimeOffset? expires = oauth["expiresAt"] is JsonValue stamp && stamp.TryGetValue<long>(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
        if (expires is null || expires > now + ExpirySkew)
        {
            return token;
        }

        if (oauth["refreshToken"]?.GetValue<string>() is not { Length: > 0 } refreshToken
            || now - _lastRefreshAttempt < RefreshBackoff)
        {
            return null;
        }

        _lastRefreshAttempt = now;
        using var mutex = new Mutex(false, @"Local\OrbMet.ClaudeTokenRefresh");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
        {
            return null;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(
                RefreshEndpoint,
                new { grant_type = "refresh_token", refresh_token = refreshToken, client_id = ClientId },
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Leave the file exactly as it was; never clear a token on a failed renewal.
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Log($"refresh {(int)response.StatusCode} {error.ReplaceLineEndings(" ")}");
                return null;
            }

            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var newAccess = reply?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(newAccess))
            {
                return null;
            }

            // The refresh token may rotate: keep the new one, or the old one if none came.
            var newRefresh = reply?["refresh_token"]?.GetValue<string>();
            var lifetime = reply?["expires_in"] is JsonValue life && life.TryGetValue<long>(out var seconds) ? seconds : 3600;

            // Write what the server issued before anything else uses it, onto the
            // file as it is now so its other fields survive.
            var current = JsonNode.Parse(File.ReadAllText(_credentialsFile));
            if (current?["claudeAiOauth"] is JsonObject target)
            {
                target["accessToken"] = newAccess;
                if (!string.IsNullOrEmpty(newRefresh))
                {
                    target["refreshToken"] = newRefresh;
                }

                target["expiresAt"] = now.AddSeconds(lifetime).ToUnixTimeMilliseconds();
                AtomicFile.WriteAllText(_credentialsFile, current.ToJsonString());
            }

            return newAccess;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    // Status codes and error names only, never a token or a response body that holds one.
    private static void Log(string line)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "OrbMet", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "claude-usage.log"), $"{DateTimeOffset.Now:u} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort.
        }
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
