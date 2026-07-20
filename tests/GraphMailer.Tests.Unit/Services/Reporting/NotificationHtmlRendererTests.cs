using FluentAssertions;
using GraphMailer.Service.Services.Reporting;

namespace GraphMailer.Tests.Unit.Services.Reporting;

public sealed class NotificationHtmlRendererTests
{
    private static NotificationEmail Sample() => new()
    {
        Severity = NotificationSeverity.Warning,
        Title = "Certificate is expiring soon",
        Intro = "A certificate used by GraphMailer expires shortly.",
        Fields = [new("Subject", "CN=smtp.contoso.com"), new("Expires", "Fri, 24 Jul 2026 00:00:00 GMT")],
    };

    [Fact]
    public void Render_ProducesWellFormedHtmlDocument_WithTitleIntroAndFields()
    {
        var html = NotificationHtmlRenderer.Render(Sample());

        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().Contain("</html>");
        html.Should().Contain("Certificate is expiring soon");
        html.Should().Contain("A certificate used by GraphMailer expires shortly.");
        html.Should().Contain("CN=smtp.contoso.com");
        html.Should().Contain("System Notification");   // default kicker
        html.Should().NotContain("<svg");               // SVG is stripped by Outlook — must not be used
    }

    [Fact]
    public void Render_BannerBackground_MatchesSeverity()
    {
        // NotificationSeverity is internal, so this cannot be an InlineData theory.
        var expected = new Dictionary<NotificationSeverity, string>
        {
            [NotificationSeverity.Success] = "#dafbe1",
            [NotificationSeverity.Info] = "#ddf4ff",
            [NotificationSeverity.Warning] = "#fff8c5",
            [NotificationSeverity.Critical] = "#ffebe9",
        };

        foreach (var (severity, bg) in expected)
        {
            var html = NotificationHtmlRenderer.Render(Sample() with { Severity = severity });
            html.Should().Contain(bg, $"severity {severity} must use its banner background");
        }
    }

    [Fact]
    public void Render_HtmlEncodesUntrustedText()
    {
        // Notification content carries attacker-influenced values (SMTP usernames,
        // subjects, error strings) — they must never land unencoded in the HTML.
        var html = NotificationHtmlRenderer.Render(new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = "<script>alert(1)</script>",
            Intro = "Intro with <b>markup</b>",
            Fields = [new("Username", "\"><img src=x onerror=alert(1)>")],
            Items = ["msg-1: <iframe>"],
            Note = "Note & more",
        });

        html.Should().NotContain("<script>");
        html.Should().NotContain("<iframe>");
        html.Should().NotContain("<img src=x");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;img src=x onerror=alert(1)&gt;");
    }

    [Fact]
    public void Render_WithItems_RendersItemsTitleAndLines()
    {
        var html = NotificationHtmlRenderer.Render(Sample() with
        {
            ItemsTitle = "Failed messages",
            Items = ["msg-1: timeout", "msg-2: mailbox full"],
        });

        html.Should().Contain("Failed messages");
        html.Should().Contain("msg-1: timeout");
        html.Should().Contain("msg-2: mailbox full");
    }

    [Fact]
    public void Render_WithLink_RendersButtonWithUrl()
    {
        var html = NotificationHtmlRenderer.Render(Sample() with
        {
            LinkUrl = "https://github.com/x/releases/tag/v1.3.0",
            LinkLabel = "Release notes",
        });

        html.Should().Contain("href=\"https://github.com/x/releases/tag/v1.3.0\"");
        html.Should().Contain("Release notes");
    }

    [Fact]
    public void Render_WithoutLink_OmitsButton()
    {
        var html = NotificationHtmlRenderer.Render(Sample());

        html.Should().NotContain("href=");
    }

    [Fact]
    public void Render_CustomKickerAndFooterNote_OverrideDefaults()
    {
        var html = NotificationHtmlRenderer.Render(Sample() with
        {
            Kicker = "Non-Delivery Report",
            FooterNote = "This is an automatically generated Non-Delivery Report from GraphMailer.",
        });

        html.Should().Contain("Non-Delivery Report");
        html.Should().Contain("automatically generated Non-Delivery Report");
        html.Should().NotContain("ConfigTool → Notifications");
    }
}
