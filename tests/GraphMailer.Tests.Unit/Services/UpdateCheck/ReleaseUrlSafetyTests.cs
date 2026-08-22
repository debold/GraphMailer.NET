using GraphMailer.Service.Services.UpdateCheck;

namespace GraphMailer.Tests.Unit.Services.UpdateCheck;

/// <summary>
/// The release URL is the one string in the product that travels from the public internet
/// (GitHub's release JSON) through the service's status file into ShellExecute — in the ConfigTool,
/// which runs elevated. Because the only page this application ever has reason to open is a release
/// of its own repository, the filter is an allow-list of exactly that, and every way past it gets a
/// test: wrong scheme, wrong host, host look-alikes, and paths outside the repository.
/// </summary>
public sealed class ReleaseUrlSafetyTests
{
    private const string ReleasePage = "https://github.com/debold/GraphMailer.NET/releases/tag/v1.3.3.1069";

    // ── The real thing still works ───────────────────────────────────────────

    [Fact]
    public void SafeReleaseUrl_ReleasePageOfThisRepository_IsAccepted()
        => GitHubUpdateChecker.SafeReleaseUrl(ReleasePage).Should().Be(ReleasePage);

    [Fact]
    public void SafeReleaseUrl_RepositoryRootWithoutTrailingSlash_IsAccepted()
    {
        // "/owner/repo" is the repository page itself — inside the allow-list, not below it.
        GitHubUpdateChecker.SafeReleaseUrl("https://github.com/debold/GraphMailer.NET")
            .Should().Be("https://github.com/debold/GraphMailer.NET");
    }

    [Fact]
    public void SafeReleaseUrl_OwnerAndRepoCasing_IsIgnored()
    {
        // GitHub resolves owner and repository case-insensitively, so a differently cased
        // html_url is the same page — rejecting it would only hide a working link.
        GitHubUpdateChecker.SafeReleaseUrl("https://GitHub.com/DeBold/graphmailer.net/releases")
            .Should().NotBeNull();
    }

    // ── Scheme ───────────────────────────────────────────────────────────────

    [Fact]
    public void SafeReleaseUrl_PlainHttp_IsRejected()
        => GitHubUpdateChecker.SafeReleaseUrl("http://github.com/debold/GraphMailer.NET/releases")
            .Should().BeNull();

    [Fact]
    public void SafeReleaseUrl_LocalExecutablePath_IsRejected()
    {
        // Uri.TryCreate parses a drive path as scheme "file" — the scheme check, not the
        // parse, is what stops it.
        GitHubUpdateChecker.SafeReleaseUrl(@"C:\Windows\System32\cmd.exe").Should().BeNull();
    }

    [Fact]
    public void SafeReleaseUrl_UncPath_IsRejected()
        => GitHubUpdateChecker.SafeReleaseUrl(@"\attacker\share\payload.exe").Should().BeNull();

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://github.com/debold/GraphMailer.NET/releases")]
    public void SafeReleaseUrl_OtherProtocolHandlers_AreRejected(string url)
    {
        // ShellExecute resolves any registered scheme, so the rule is "https", not a
        // blocklist of the schemes we happened to think of.
        GitHubUpdateChecker.SafeReleaseUrl(url).Should().BeNull();
    }

    // ── Host ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.org/debold/GraphMailer.NET/releases")]
    [InlineData("https://github.com.example.org/debold/GraphMailer.NET/releases")]
    [InlineData("https://notgithub.com/debold/GraphMailer.NET/releases")]
    [InlineData("https://evil.github.com.example.org/debold/GraphMailer.NET/releases")]
    public void SafeReleaseUrl_LookAlikeHost_IsRejected(string url)
    {
        // Exact host equality is the point: EndsWith(".github.com") or Contains("github.com")
        // would wave every one of these through.
        GitHubUpdateChecker.SafeReleaseUrl(url).Should().BeNull();
    }

    [Fact]
    public void SafeReleaseUrl_HostInUserInfoNotInAuthority_IsRejected()
    {
        // The classic misread: everything before the "@" is credentials, the real host is
        // example.org. A human skimming the link sees github.com first.
        GitHubUpdateChecker.SafeReleaseUrl("https://github.com@example.org/debold/GraphMailer.NET")
            .Should().BeNull();
    }

    [Fact]
    public void SafeReleaseUrl_CredentialsOnTheRealHost_IsRejected()
        => GitHubUpdateChecker.SafeReleaseUrl("https://user:pw@github.com/debold/GraphMailer.NET/releases")
            .Should().BeNull();

    [Fact]
    public void SafeReleaseUrl_NonDefaultPort_IsRejected()
        => GitHubUpdateChecker.SafeReleaseUrl("https://github.com:8443/debold/GraphMailer.NET/releases")
            .Should().BeNull();

    // ── Path inside the repository ───────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/someone-else/malware/releases")]
    [InlineData("https://github.com/debold/GraphMailer.NET.evil/releases")]
    [InlineData("https://github.com/debold")]
    [InlineData("https://github.com/")]
    public void SafeReleaseUrl_PathOutsideThisRepository_IsRejected(string url)
        => GitHubUpdateChecker.SafeReleaseUrl(url).Should().BeNull();

    [Fact]
    public void SafeReleaseUrl_TraversalBackOutOfTheRepository_IsRejected()
    {
        // Uri normalises the path before we see it, so this ends up as "/elsewhere" —
        // the assertion pins that the check runs on the normalised form.
        GitHubUpdateChecker.SafeReleaseUrl("https://github.com/debold/GraphMailer.NET/../../elsewhere")
            .Should().BeNull();
    }

    // ── Missing values ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("releases/latest")]
    public void SafeReleaseUrl_MissingOrRelativeValue_IsRejected(string? url)
        => GitHubUpdateChecker.SafeReleaseUrl(url).Should().BeNull();
}
