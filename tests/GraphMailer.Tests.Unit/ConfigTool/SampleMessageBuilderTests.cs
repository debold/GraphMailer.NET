using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure.Rules;
using GraphMailer.Service.Services;
using MimeKit;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// The rule tester's sample message.
///
/// The shapes matter more than the values: which body parts exist decides what a body rule can
/// do at all, so a builder that always produced an alternative would let the tester report an
/// effect the real message would never see.
/// </summary>
public sealed class SampleMessageBuilderTests
{
    [Fact]
    public void Build_TextOnly_ProducesASinglePlainPart()
    {
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "body text", null);

        var split = MimeMessageSplitter.Split(message);
        split.TextBody.Should().NotBeNull();
        split.HtmlBody.Should().BeNull();
        split.TextBody!.Text.Should().Be("body text");
    }

    [Fact]
    public void Build_HtmlOnly_ProducesASingleHtmlPart()
    {
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", null, "<p>html</p>");

        var split = MimeMessageSplitter.Split(message);
        split.HtmlBody.Should().NotBeNull();
        split.TextBody.Should().BeNull();
    }

    [Fact]
    public void Build_BothBodies_ProducesAnAlternative()
    {
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "text", "<p>html</p>");

        var split = MimeMessageSplitter.Split(message);
        split.TextBody.Should().NotBeNull();
        split.HtmlBody.Should().NotBeNull();
    }

    [Fact]
    public void Build_WithAttachments_WrapsTheBodyInAMixedContainer()
    {
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "text", null,
            [new SampleAttachment("report.pdf", 512), new SampleAttachment("macro.docm", 256)]);

        var split = MimeMessageSplitter.Split(message);
        split.TextBody.Should().NotBeNull();
        split.Attachments.Should().HaveCount(2);
    }

    [Fact]
    public void Build_SetsSenderRecipientsAndSubject()
    {
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["a@example.com", "b@example.com"], "Quarterly", "text", null);

        message.From.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("sender@example.com");
        message.To.Mailboxes.Select(m => m.Address).Should().BeEquivalentTo(["a@example.com", "b@example.com"]);
        message.Subject.Should().Be("Quarterly");
    }

    [Fact]
    public void Build_InvalidAddress_IsSkippedRatherThanThrowing()
    {
        // The tester runs on whatever is typed into the form, so a half-finished address must
        // not take the dialog down.
        var act = () => SampleMessageBuilder.Build(
            "not an address", ["also not one"], "Subject", "text", null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Build_WithHeaders_AddsThem()
    {
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "text", null,
            headers: [("X-Origin", "erp")]);

        message.Headers["X-Origin"].Should().Be("erp");
    }

    [Theory]
    [InlineData("a@x.com\r\nb@x.com", 2)]
    [InlineData("a@x.com, b@x.com", 2)]
    [InlineData("a@x.com; b@x.com", 2)]
    [InlineData("a@x.com\n\n\nb@x.com\n", 2)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    public void ParseRecipients_HandlesTheUsualSeparatorsAndBlanks(string input, int expected)
    {
        SampleMessageBuilder.ParseRecipients(input).Should().HaveCount(expected);
    }

    // =========================================================================
    // Attachment specifications
    // =========================================================================

    [Fact]
    public void ParseAttachments_NameOnly_UsesTheDefaultSize()
    {
        var parsed = SampleMessageBuilder.ParseAttachments("report.pdf");

        parsed.Should().ContainSingle();
        parsed[0].FileName.Should().Be("report.pdf");
        parsed[0].SizeBytes.Should().Be(SampleMessageBuilder.DefaultAttachmentSizeBytes);
    }

    [Fact]
    public void ParseAttachments_NameAndSize_KeepsBoth()
    {
        // A size matters because attachment rules can match on it — without a way to say how big
        // a test attachment is, a size rule cannot be tried out at all.
        var parsed = SampleMessageBuilder.ParseAttachments("big.zip | 20480");

        parsed.Should().ContainSingle();
        parsed[0].FileName.Should().Be("big.zip");
        parsed[0].SizeBytes.Should().Be(20480);
    }

    [Fact]
    public void ParseAttachments_UnparsableSize_FallsBackInsteadOfDroppingTheAttachment()
    {
        // Dropping it would look exactly like the rule failing to match.
        var parsed = SampleMessageBuilder.ParseAttachments("report.pdf | huge");

        parsed.Should().ContainSingle();
        parsed[0].SizeBytes.Should().Be(SampleMessageBuilder.DefaultAttachmentSizeBytes);
    }

    [Fact]
    public void ParseAttachments_SeveralLinesAndBlanks_AreHandled()
    {
        var parsed = SampleMessageBuilder.ParseAttachments("a.pdf\r\n\r\n b.docm | 512 \n");

        parsed.Should().HaveCount(2);
        parsed[1].FileName.Should().Be("b.docm");
        parsed[1].SizeBytes.Should().Be(512);
    }

    [Fact]
    public void ParseAttachments_Empty_YieldsNothing()
    {
        SampleMessageBuilder.ParseAttachments("   ").Should().BeEmpty();
        SampleMessageBuilder.ParseAttachments(null).Should().BeEmpty();
    }

    [Fact]
    public void FormatAttachments_RoundTripsThroughParse()
    {
        // Loading a message fills the box; editing and re-running must see the same attachments.
        var text = SampleMessageBuilder.FormatAttachments([("report.pdf", 2048), ("macro.docm", 512)]);

        var parsed = SampleMessageBuilder.ParseAttachments(text);

        parsed.Select(a => (a.FileName, a.SizeBytes))
            .Should().Equal(("report.pdf", 2048), ("macro.docm", 512));
    }

    [Fact]
    public void Build_AttachmentWithASize_IsMatchableByASizeRule()
    {
        // End of the chain: what the box accepts has to reach the splitter as a real size.
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "text", null,
            SampleMessageBuilder.ParseAttachments("big.zip | 8192"));

        var attachment = MimeMessageSplitter.Split(message).Attachments.Should().ContainSingle().Subject;

        MimeMessageSplitter.MeasureEncodedSize(attachment.Entity).Should().Be(8192);
    }

    // =========================================================================
    // Headers, importance and protection
    // =========================================================================

    [Fact]
    public void ParseHeaders_NameAndValue_AreSplitAtTheFirstColon()
    {
        // A value may itself contain a colon (a URL, a timestamp), so only the first one counts.
        var parsed = SampleMessageBuilder.ParseHeaders("X-Origin: erp\r\nX-Link: https://example.com/x");

        parsed.Should().HaveCount(2);
        parsed[0].Should().Be(("X-Origin", "erp"));
        parsed[1].Should().Be(("X-Link", "https://example.com/x"));
    }

    [Theory]
    [InlineData("no colon here")]
    [InlineData(": value without a name")]
    [InlineData("   ")]
    public void ParseHeaders_UnusableLine_IsSkipped(string line)
    {
        SampleMessageBuilder.ParseHeaders(line).Should().BeEmpty();
    }

    [Fact]
    public void FormatHeaders_RoundTripsThroughParse()
    {
        var text = SampleMessageBuilder.FormatHeaders([("X-Origin", "erp"), ("X-Policy", "external")]);

        SampleMessageBuilder.ParseHeaders(text)
            .Should().Equal(("X-Origin", "erp"), ("X-Policy", "external"));
    }

    [Fact]
    public void Build_WithHeaders_MakesThemMatchableByAHeaderCondition()
    {
        // Without this a Header condition could not be tried out from the form at all.
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "text", null,
            headers: SampleMessageBuilder.ParseHeaders("X-Origin: erp"));

        message.Headers["X-Origin"].Should().Be("erp");
    }

    [Theory]
    [InlineData(MessageImportance.Low)]
    [InlineData(MessageImportance.Normal)]
    [InlineData(MessageImportance.High)]
    public void Build_Importance_ReachesTheMessage(MessageImportance importance)
    {
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "text", null,
            importance: importance);

        message.Importance.Should().Be(importance);
    }

    [Theory]
    [InlineData(SampleProtection.Signed, MimeProtectionKind.Signed)]
    [InlineData(SampleProtection.Encrypted, MimeProtectionKind.Encrypted)]
    [InlineData(SampleProtection.None, MimeProtectionKind.None)]
    internal void Build_Protection_IsClassifiedByTheService(
        SampleProtection protection, MimeProtectionKind expected)
    {
        // The tester's stand-in has to be recognised by the same classifier the rules use —
        // otherwise "skip the disclaimer on signed mail" could not be tried out at all.
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "text", null,
            protection: protection);

        MimeProtection.Classify(message).Should().Be(expected);
    }

    [Fact]
    public void Build_Signed_KeepsTheBodyReachable()
    {
        // The body still has to be there — the rules skip changing it, they do not stop seeing it.
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["rcpt@example.com"], "Subject", "the body", null,
            protection: SampleProtection.Signed);

        MimeMessageSplitter.Split(message).TextBody!.Text.Should().Be("the body");
    }

    // =========================================================================
    // To, Cc and Bcc
    // =========================================================================

    [Fact]
    public void Build_Cc_LandsInTheCcHeaderNotInTo()
    {
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["to@example.com"], "Subject", "text", null,
            cc: ["copy@example.com"]);

        message.To.Mailboxes.Select(m => m.Address).Should().Equal("to@example.com");
        message.Cc.Mailboxes.Select(m => m.Address).Should().Equal("copy@example.com");
    }

    [Fact]
    public void Build_HasNoBccParameter_BecauseABlindCopyIsEnvelopeOnly()
    {
        // The whole point of a blind copy: it exists in the envelope and in no header. The caller
        // adds it to the envelope; there is nothing for the builder to write into the message.
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["to@example.com"], "Subject", "text", null,
            cc: ["copy@example.com"]);

        message.Bcc.Count.Should().Be(0);
    }

    [Fact]
    public void Build_ToAndCc_AreBothVisibleToTheDeliveryView()
    {
        // What the tester then hands the engine as the envelope is To + Cc + Bcc; the snapshot
        // derives Bcc back out of it, so the three boxes have to round-trip through that view.
        var message = SampleMessageBuilder.Build(
            "sender@example.com", ["to@example.com"], "Subject", "text", null,
            cc: ["copy@example.com"]);

        var snapshot = MessageSnapshot.Capture(
            message, "sender@example.com",
            ["to@example.com", "copy@example.com", "blind@example.com"]);

        snapshot.To.Should().Equal("to@example.com");
        snapshot.Cc.Should().Equal("copy@example.com");

        // The envelope address named in neither header is the blind copy.
        snapshot.Bcc.Should().Equal("blind@example.com");
    }

    [Fact]
    public void Build_InvalidCcAddress_IsSkippedRatherThanThrowing()
    {
        var act = () => SampleMessageBuilder.Build(
            "sender@example.com", ["to@example.com"], "Subject", "text", null,
            cc: ["not an address"]);

        act.Should().NotThrow();
    }
}
