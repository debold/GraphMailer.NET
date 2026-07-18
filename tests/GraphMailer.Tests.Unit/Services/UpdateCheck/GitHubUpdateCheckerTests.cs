using System.Net;
using FluentAssertions;
using GraphMailer.Service.Services.UpdateCheck;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphMailer.Tests.Unit.Services.UpdateCheck;

public sealed class GitHubUpdateCheckerTests
{
    private const string Current = "1.2.0.196";

    private static string ReleaseJson(string tag) => $$"""
        {
            "tag_name": "{{tag}}",
            "name": "GraphMailer {{tag}}",
            "html_url": "https://github.com/debold/GraphMailer.NET/releases/tag/{{tag}}",
            "published_at": "2026-07-10T12:00:00Z"
        }
        """;

    // ── Evaluate: version comparison ─────────────────────────────────────

    [Fact]
    public void Evaluate_NewerRelease_UpdateAvailable()
    {
        var r = GitHubUpdateChecker.Evaluate(ReleaseJson("v1.3.0.210"), Current);

        r.Success.Should().BeTrue();
        r.UpdateAvailable.Should().BeTrue();
        r.LatestVersion.Should().Be("1.3.0.210");
        r.CurrentVersion.Should().Be(Current);
    }

    [Fact]
    public void Evaluate_SameVersion_NoUpdate()
    {
        var r = GitHubUpdateChecker.Evaluate(ReleaseJson("v1.2.0.196"), Current);

        r.Success.Should().BeTrue();
        r.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_OlderRelease_NoUpdate()
    {
        // Running a build that is newer than the latest published release (e.g. a dev build)
        // must not be reported as an update.
        var r = GitHubUpdateChecker.Evaluate(ReleaseJson("v1.1.0.150"), Current);

        r.Success.Should().BeTrue();
        r.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_TagWithoutVPrefix_IsParsed()
    {
        var r = GitHubUpdateChecker.Evaluate(ReleaseJson("2.0.0.300"), Current);

        r.Success.Should().BeTrue();
        r.UpdateAvailable.Should().BeTrue();
        r.LatestVersion.Should().Be("2.0.0.300");
    }

    [Fact]
    public void Evaluate_NewerBuildOfSameSemVer_IsAnUpdate()
    {
        // Hotfix releases share the SemVer but carry a higher build number.
        var r = GitHubUpdateChecker.Evaluate(ReleaseJson("v1.2.0.200"), Current);

        r.Success.Should().BeTrue();
        r.UpdateAvailable.Should().BeTrue();
    }

    // ── Evaluate: release metadata ───────────────────────────────────────

    [Fact]
    public void Evaluate_ParsesUrlNameAndPublishedDate()
    {
        var r = GitHubUpdateChecker.Evaluate(ReleaseJson("v1.3.0.210"), Current);

        r.ReleaseUrl.Should().Be("https://github.com/debold/GraphMailer.NET/releases/tag/v1.3.0.210");
        r.ReleaseName.Should().Be("GraphMailer v1.3.0.210");
        r.PublishedUtc.Should().Be(new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc));
    }

    // ── Evaluate: error paths ────────────────────────────────────────────

    [Fact]
    public void Evaluate_UnparseableTag_IsError()
    {
        var r = GitHubUpdateChecker.Evaluate(ReleaseJson("latest-stable"), Current);

        r.Success.Should().BeFalse();
        r.Error.Should().Contain("latest-stable");
    }

    [Fact]
    public void Evaluate_MissingTagName_IsError()
    {
        var r = GitHubUpdateChecker.Evaluate("""{ "name": "no tag here" }""", Current);

        r.Success.Should().BeFalse();
        r.Error.Should().Contain("tag_name");
    }

    [Fact]
    public void Evaluate_InvalidJson_IsError()
    {
        var r = GitHubUpdateChecker.Evaluate("{ not json", Current);

        r.Success.Should().BeFalse();
        r.Error.Should().Contain("Invalid release response");
    }

    // ── CheckAsync: HTTP behaviour (fake handler) ────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static GitHubUpdateChecker CreateChecker(FakeHandler handler)
        => new(NullLogger<GitHubUpdateChecker>.Instance, handler);

    [Fact]
    public async Task CheckAsync_SuccessResponse_ReturnsSuccess_AndSendsUserAgent()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson("v999.0.0.0")),
        });
        using var sut = CreateChecker(handler);

        var r = await sut.CheckAsync();

        r.Success.Should().BeTrue();
        r.UpdateAvailable.Should().BeTrue("v999 is newer than any real build");
        handler.LastRequest!.Headers.UserAgent.Should().NotBeEmpty("GitHub rejects requests without a User-Agent");
        handler.LastRequest.RequestUri!.Host.Should().Be("api.github.com");
    }

    [Fact]
    public async Task CheckAsync_HttpErrorStatus_IsErrorResult()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var sut = CreateChecker(handler);

        var r = await sut.CheckAsync();

        r.Success.Should().BeFalse();
        r.Error.Should().Contain("404");
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_IsErrorResult_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        using var sut = CreateChecker(handler);

        var r = await sut.CheckAsync();

        r.Success.Should().BeFalse();
        r.Error.Should().Contain("connection refused");
    }
}
