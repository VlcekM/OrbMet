using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OrbMet.Core.Providers.Claude;
using Xunit;

namespace OrbMet.Core.Tests;

public sealed class ClaudeOAuthRefreshTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T10:00:00Z");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "orbmet-oauth-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public ClaudeOAuthRefreshTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, ".credentials.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteCredentials(long expiresAtMs) => File.WriteAllText(
        _file,
        $$"""{"claudeAiOauth":{"accessToken":"old-access","refreshToken":"old-refresh","expiresAt":{{expiresAtMs}},"scopes":["user:inference"],"subscriptionType":"max"},"other":1}""");

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.Host}");
            return Task.FromResult(reply(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string UsageBody = """{"five_hour":{"utilization":10,"resets_at":"2026-10-16T14:00:00+00:00"}}""";

    [Fact]
    public async Task Expired_token_is_renewed_and_written_back()
    {
        WriteCredentials(Now.AddHours(-1).ToUnixTimeMilliseconds());
        var handler = new FakeHandler(request => request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":28800}""")
            : request.Headers.Authorization?.Parameter == "new-access"
                ? Json(HttpStatusCode.OK, UsageBody)
                : Json(HttpStatusCode.Unauthorized, "{}"));
        var client = new ClaudeOAuthUsageClient(_file, new HttpClient(handler), () => Now);

        var record = await client.ReadAsync(CancellationToken.None);

        Assert.NotNull(record);
        var saved = JsonNode.Parse(File.ReadAllText(_file))!;
        Assert.Equal("new-access", (string?)saved["claudeAiOauth"]!["accessToken"]);
        Assert.Equal("new-refresh", (string?)saved["claudeAiOauth"]!["refreshToken"]);
        Assert.Equal(Now.AddSeconds(28800).ToUnixTimeMilliseconds(), (long?)saved["claudeAiOauth"]!["expiresAt"]);
        Assert.Equal("max", (string?)saved["claudeAiOauth"]!["subscriptionType"]);
        Assert.Equal(1, (int?)saved["other"]);
    }

    [Fact]
    public async Task Failed_renewal_leaves_the_file_untouched()
    {
        WriteCredentials(Now.AddHours(-1).ToUnixTimeMilliseconds());
        var before = File.ReadAllText(_file);
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        var client = new ClaudeOAuthUsageClient(_file, new HttpClient(handler), () => Now);

        var record = await client.ReadAsync(CancellationToken.None);

        Assert.Null(record);
        Assert.Equal(before, File.ReadAllText(_file));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Valid_token_is_used_without_renewing()
    {
        WriteCredentials(Now.AddHours(2).ToUnixTimeMilliseconds());
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, UsageBody));
        var client = new ClaudeOAuthUsageClient(_file, new HttpClient(handler), () => Now);

        Assert.NotNull(await client.ReadAsync(CancellationToken.None));
        Assert.Equal(new[] { "GET api.anthropic.com" }, handler.Calls);
    }
}
