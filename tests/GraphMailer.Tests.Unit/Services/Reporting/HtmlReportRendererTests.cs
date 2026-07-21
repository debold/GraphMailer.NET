using FluentAssertions;
using GraphMailer.Service.Services.Reporting;

namespace GraphMailer.Tests.Unit.Services.Reporting;

public sealed class HtmlReportRendererTests
{
    private static ReportData Sample(
        IReadOnlyList<FailedQueueItem>? failed = null,
        IReadOnlyList<NamedCount>? topSenders = null) => new()
    {
        Host = "SMTP-RELAY-01",
        Version = "1.4.2.99",
        GeneratedAt = new DateTimeOffset(2026, 6, 13, 6, 0, 0, TimeSpan.Zero),
        PeriodStart = new DateOnly(2026, 6, 6),
        PeriodEnd = new DateOnly(2026, 6, 13),
        Title = "Weekly Operations Report",
        PeriodLabel = "last 7 days",
        QueuedNow = 3,
        FailedQueueCount = failed?.Count ?? 0,
        FailedQueueItems = failed ?? [],
        Health = [new HealthItem("SMTP Service", HealthStatus.Ok, "Running")],
        Delivered = 4812,
        Failed = 17,
        PrevDelivered = 4530,
        PrevFailed = 24,
        SuccessRatePercent = 99.6,
        AvgDeliveryMs = 842,
        DistinctSenders = 23,
        VolumeBytes = 1_932_735_283,
        Daily =
        [
            new DailyPoint(new DateOnly(2026, 6, 6), 540, 1),
            new DailyPoint(new DateOnly(2026, 6, 7), 632, 1),
            new DailyPoint(new DateOnly(2026, 6, 8), 680, 3),
        ],
        TopSenders = topSenders ?? [new NamedCount("noreply@contoso.com", 2104)],
        TopHosts = [new NamedCount("10.20.4.11", 2510)],
        Uptime = "6d 4h",
    };

    [Fact]
    public void Render_ProducesWellFormedHtmlDocument_WithCidChartImage()
    {
        var email = HtmlReportRenderer.Render(Sample());
        var html = email.Html;

        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().Contain("</html>");
        html.Should().Contain("Weekly Operations Report");
        html.Should().Contain("SMTP-RELAY-01");
        html.Should().Contain("4,812");                                  // delivered, thousands-formatted
        html.Should().Contain("Daily Volume");                          // daily chart section present
        html.Should().NotContain("<svg");                               // SVG is stripped by Outlook — must not be used
        html.Should().Contain($"cid:{HtmlReportRenderer.ChartContentId}"); // chart referenced as a CID inline image
        email.ChartPng.Should().NotBeNull();                            // and the PNG is produced for attachment
        email.ChartPng!.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Render_NoFailedQueue_OmitsActionRequiredSection()
    {
        var html = HtmlReportRenderer.Render(Sample()).Html;

        html.Should().NotContain("Action Required");
    }

    [Fact]
    public void Render_WithFailedQueue_ShowsActionRequiredSection()
    {
        var failed = new[]
        {
            new FailedQueueItem(new DateTime(2026, 6, 12, 9, 15, 0, DateTimeKind.Utc),
                "crm@contoso.com", "legacy@onprem.contoso.com", "Daily export",
                "MailboxNotEnabledForRESTAPI", 10),
        };

        var html = HtmlReportRenderer.Render(Sample(failed)).Html;

        html.Should().Contain("Action Required");
        html.Should().Contain("MailboxNotEnabledForRESTAPI");
    }

    [Fact]
    public void Render_WithoutRecommendations_OmitsTheBoxEntirely()
    {
        var html = HtmlReportRenderer.Render(Sample()).Html;

        html.Should().NotContain("Recommendations",
            "an install with every opt-in feature enabled must not see the hint box at all");
    }

    [Fact]
    public void Render_WithRecommendations_ShowsThemWithoutWarningStyling()
    {
        var data = Sample() with
        {
            Recommendations = [new Recommendation("Turn on the update check", "Security fixes go unnoticed.")],
        };

        var html = HtmlReportRenderer.Render(data).Html;

        html.Should().Contain("Recommendations");
        html.Should().Contain("Turn on the update check");
        html.Should().Contain("Security fixes go unnoticed.");
        html.Should().Contain(EmailTheme.InfoBg, "hints use the neutral info palette, never the warning colours");
        html.Should().Contain("This is switched on", "the singular wording is used for a single hint");
    }

    [Fact]
    public void Render_WithTwoRecommendations_UsesPluralWording()
    {
        var data = Sample() with
        {
            Recommendations =
            [
                new Recommendation("Turn on the update check", "…"),
                new Recommendation("Consider sharing anonymous usage telemetry", "…"),
            ],
        };

        HtmlReportRenderer.Render(data).Html.Should().Contain("Both are switched on");
    }

    [Fact]
    public void Render_HtmlEncodesUserControlledText()
    {
        var failed = new[]
        {
            new FailedQueueItem(DateTime.UtcNow, "<b>spoof@x</b>", "victim@y",
                "<script>alert(1)</script>", "boom <img src=x>", 1),
        };

        var html = HtmlReportRenderer.Render(Sample(failed,
            topSenders: [new NamedCount("<script>evil</script>", 5)])).Html;

        html.Should().NotContain("<script>alert(1)</script>");
        html.Should().NotContain("<script>evil</script>");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.Should().Contain("&lt;b&gt;spoof@x&lt;/b&gt;");
    }
}
