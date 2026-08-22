using System.Net.Http;
using System.Text.Json;
using GraphMailer.Service.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GraphMailer.Service.Services.UpdateCheck;

/// <summary>Outcome of a single release check. <see cref="Error"/> is set iff <see cref="Success"/> is false.</summary>
internal sealed record UpdateCheckResult(
    bool Success,
    string CurrentVersion,
    string? LatestVersion = null,
    bool UpdateAvailable = false,
    string? ReleaseUrl = null,
    string? ReleaseName = null,
    DateTime? PublishedUtc = null,
    string? Error = null);

internal interface IUpdateChecker
{
    /// <summary>Queries the latest release and compares it to the running version. Never throws.</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Queries the GitHub Releases API (<c>/releases/latest</c> — newest non-draft, non-prerelease
/// release) and compares its tag (<c>v&lt;FileVersion&gt;</c>, e.g. <c>v1.2.0.196</c>) against
/// the running <see cref="BuildInfo.FileVersion"/> as four-part <see cref="Version"/> values.
/// </summary>
internal sealed class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    private const string RepoOwner = "debold";
    private const string RepoName = "GraphMailer.NET";
    private const string ReleaseHost = "github.com";
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

    /// <summary>
    /// Reduces a release URL to one that can only point at this repository's own release pages,
    /// or to <see langword="null"/>.
    ///
    /// The value arrives from outside the process — GitHub's JSON, then the status file — and ends
    /// up in ShellExecute inside the *elevated* ConfigTool, where a non-web string (a UNC path,
    /// <c>file://</c>, any registered protocol handler) would run with full privileges. Since the
    /// only URL this application ever has reason to open is a release page of its own repository,
    /// the check is against that repository rather than merely for a plausible-looking scheme:
    /// https, host <c>github.com</c> exactly, and a path inside <c>/{owner}/{repo}</c>.
    ///
    /// Exact host equality on purpose — <c>EndsWith(".github.com")</c> would accept
    /// <c>github.com.example.org</c>, and <c>Contains</c> would accept anything at all. Credentials
    /// in the URL and a non-default port are refused too: GitHub never sends either, so their only
    /// use here would be to make a link read as something it is not.
    /// </summary>
    internal static string? SafeReleaseUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(ReleaseHost, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0)
            return null;

        // AbsolutePath is already normalised, so "/owner/repo/../../elsewhere" cannot slip through.
        var repoPath = $"/{RepoOwner}/{RepoName}";
        var path = uri.AbsolutePath;
        var insideRepo =
            path.Equals(repoPath, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith($"{repoPath}/", StringComparison.OrdinalIgnoreCase);

        return insideRepo ? uri.AbsoluteUri : null;
    }

    private readonly HttpClient _http;
    private readonly ILogger<GitHubUpdateChecker> _logger;

    public GitHubUpdateChecker(ILogger<GitHubUpdateChecker> logger)
        : this(logger, new HttpClientHandler()) { }

    /// <summary>Test constructor: inject a fake <see cref="HttpMessageHandler"/>.</summary>
    internal GitHubUpdateChecker(ILogger<GitHubUpdateChecker> logger, HttpMessageHandler handler)
    {
        _logger = logger;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects requests without a User-Agent header.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GraphMailer/{BuildInfo.FileVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = BuildInfo.FileVersion;
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                // 404 also covers "repository has no releases yet".
                var error = $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger.LogWarning("[UpdateCheck] Release query failed — {Error}", error);
                return new UpdateCheckResult(false, current, Error: error);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = Evaluate(json, current);
            if (!result.Success)
                _logger.LogWarning("[UpdateCheck] Release response could not be evaluated — {Error}", result.Error);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Timeout, DNS failure, proxy error, TLS problem — all map to "check failed".
            _logger.LogWarning(ex, "[UpdateCheck] Release query failed — {Error}", ex.Message);
            return new UpdateCheckResult(false, current, Error: ex.Message);
        }
    }

    /// <summary>Parses a <c>/releases/latest</c> response body and compares versions. internal for unit tests.</summary>
    internal static UpdateCheckResult Evaluate(string json, string currentVersion)
    {
        string? tag, url, name;
        DateTime? published = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var dt))
                published = dt.ToUniversalTime();
        }
        catch (JsonException ex)
        {
            return new UpdateCheckResult(false, currentVersion, Error: $"Invalid release response: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(tag))
            return new UpdateCheckResult(false, currentVersion, Error: "Release response contains no tag_name");

        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            return new UpdateCheckResult(false, currentVersion, Error: $"Release tag '{tag}' is not a version number");

        if (!Version.TryParse(currentVersion, out var current))
            return new UpdateCheckResult(false, currentVersion, Error: $"Running version '{currentVersion}' is not a version number");

        return new UpdateCheckResult(
            true,
            currentVersion,
            LatestVersion: latest.ToString(),
            UpdateAvailable: latest > current,
            // Filtered at the source, so a URL that is not ours never reaches the status file.
            ReleaseUrl: SafeReleaseUrl(url),
            ReleaseName: name,
            PublishedUtc: published);
    }

    public void Dispose() => _http.Dispose();
}
