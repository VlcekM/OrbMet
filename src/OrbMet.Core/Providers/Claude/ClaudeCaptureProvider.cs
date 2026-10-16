using OrbMet.Core.Models;

namespace OrbMet.Core.Providers.Claude;

/// <summary>
/// Reads the last Claude capture from disk. This is a push source: the button
/// re-reads the file, it does not ask Claude for a new measurement, and a
/// missing file means no data rather than zero usage.
/// </summary>
public sealed class ClaudeCaptureProvider : IUsageProvider
{
    private readonly ClaudeCaptureStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _freshFor;
    private readonly ClaudeOAuthUsageClient? _live;
    private bool _sweptLeftovers;

    /// <param name="live">
    /// Asked when the status line has not reported lately (for instance in the
    /// desktop app, which never runs it); the newer of the two readings wins.
    /// </param>
    public ClaudeCaptureProvider(
        string captureDirectory,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? freshFor = null,
        ClaudeOAuthUsageClient? live = null)
    {
        _live = live;
        _store = new ClaudeCaptureStore(captureDirectory);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _freshFor = freshFor ?? ClaudeCaptureNormalizer.DefaultFreshFor;
    }

    public ProviderId ProviderId => ProviderId.ClaudeCode;

    public SourceKind SourceKind => SourceKind.ClaudeStatusLine;

    public async Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var now = _clock();

        ClaudeCaptureRecord? record;
        try
        {
            if (!_sweptLeftovers)
            {
                _sweptLeftovers = true;
                _store.RemoveLeftovers(now, TimeSpan.FromHours(1));
            }

            record = _store.ReadNewest(now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProviderSnapshot
            {
                ProviderId = ProviderId.ClaudeCode,
                SourceKind = SourceKind.ClaudeStatusLine,
                AccountScopeKey = "claude-code",
                ReceivedAtUtc = now,
                LastAttemptAtUtc = now,
                ConnectionState = ConnectionState.Error,
                Freshness = Freshness.Missing,
                ErrorCode = "claude-capture-read-failed",
            };
        }

        if (_live is not null && (record is null || now - record.CapturedAtUtc > TimeSpan.FromMinutes(1)))
        {
            var live = await _live.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (live is not null && (record is null || live.CapturedAtUtc > record.CapturedAtUtc))
            {
                record = live;
            }
        }

        return ClaudeCaptureNormalizer.Normalize(record, now, _freshFor);
    }
}
