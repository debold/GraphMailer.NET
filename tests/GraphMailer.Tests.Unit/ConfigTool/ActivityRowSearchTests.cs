using GraphMailer.ConfigTool.Views;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Live-search predicate of the Metrics page's "Recent Activity" grid. The grid is
/// re-filled by a 5 s auto-refresh, so the filter must be a pure function of the row
/// and the search term — no UI state involved.
/// </summary>
public sealed class ActivityRowSearchTests
{
    private static ActivityRow Row(
        string from = "app@contoso.com",
        string to = "ops@contoso.com",
        string subject = "Nightly Backup Report",
        string evt = "sent",
        string auth = "relay-user",
        string detail = "a1b2c3d4") =>
        new(
            Timestamp: "2026-07-21 08:15:00",
            Event: evt,
            From: from,
            To: to,
            Subject: subject,
            Attachments: "2",
            Listener: "587",
            Tls: "Yes",
            Auth: auth,
            Size: "12.4 KB",
            Duration: "310 ms",
            Detail: detail);

    [Fact]
    public void Matches_EmptySearch_MatchesEveryRow()
    {
        Row().Matches("").Should().BeTrue();
        Row().Matches("   ").Should().BeTrue("a whitespace-only box is an empty box to the user");
    }

    [Theory]
    [InlineData("backup")]        // subject
    [InlineData("app@contoso")]   // sender
    [InlineData("ops@")]          // recipient
    [InlineData("sent")]          // event type
    [InlineData("relay-user")]    // authenticated user
    [InlineData("587")]           // listener port
    [InlineData("a1b2c3d4")]      // detail / message id
    public void Matches_TermInAnyDisplayedColumn_ReturnsTrue(string term)
    {
        Row().Matches(term).Should().BeTrue();
    }

    [Fact]
    public void Matches_IsCaseInsensitive()
    {
        Row(subject: "Nightly Backup Report").Matches("NIGHTLY").Should().BeTrue();
    }

    [Fact]
    public void Matches_SurroundingWhitespaceIsIgnored()
    {
        Row().Matches("  backup  ").Should().BeTrue("the search box is not trimmed by the user");
    }

    [Fact]
    public void Matches_TermInNoColumn_ReturnsFalse()
    {
        Row().Matches("fabrikam").Should().BeFalse();
    }

    [Fact]
    public void Matches_ErrorDetail_FindsFailedDeliveries()
    {
        Row(evt: "failed", detail: "MailboxNotEnabledForRESTAPI")
            .Matches("mailboxnotenabled").Should().BeTrue(
                "searching for an error string is the main reason to filter the activity grid");
    }
}
