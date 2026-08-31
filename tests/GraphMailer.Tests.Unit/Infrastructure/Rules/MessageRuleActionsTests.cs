using System.Text;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Rules;
using GraphMailer.Service.Services;
using MimeKit;
using static GraphMailer.Tests.Unit.Infrastructure.Rules.RuleTestFactory;

namespace GraphMailer.Tests.Unit.Infrastructure.Rules;

/// <summary>
/// The MimeKit mutations behind the action types.
///
/// Two invariants carry most of the weight here:
///   • Recipient actions must touch the envelope as well as the headers. Delivery follows the
///     envelope, so removing a recipient from the header alone would turn them into a Bcc —
///     still delivered, now invisibly. That is a privacy defect, not a cosmetic one.
///   • Body actions must target the parts the delivery path picks, and must never invent a
///     missing one: adding an HTML part to a text-only message changes how every recipient sees
///     the mail.
/// </summary>
public sealed class MessageRuleActionsTests
{
    private static ActionEffect Apply(RuleAction action, MessageRuleContext ctx)
        => MessageRuleActions.Apply(action, ctx);

    // =========================================================================
    // Recipients — headers AND envelope
    // =========================================================================

    [Fact]
    public void AddRecipient_To_AddsToHeaderAndEnvelope()
    {
        var ctx = Context(recipients: ["rcpt@example.com"]);

        var effect = Apply(new RuleAction
        {
            Type = RuleActionType.AddRecipient,
            Recipient = RecipientKind.To,
            Value = "extra@example.com",
        }, ctx);

        effect.Changed.Should().BeTrue();
        effect.EnvelopeChanged.Should().BeTrue();
        ctx.Message.To.Mailboxes.Select(m => m.Address).Should().Contain("extra@example.com");
        ctx.EnvelopeRecipients.Should().Contain("extra@example.com");
    }

    [Fact]
    public void AddRecipient_Cc_UsesTheCcHeader()
    {
        var ctx = Context();

        Apply(new RuleAction
        {
            Type = RuleActionType.AddRecipient,
            Recipient = RecipientKind.Cc,
            Value = "watch@example.com",
        }, ctx);

        ctx.Message.Cc.Mailboxes.Select(m => m.Address).Should().Contain("watch@example.com");
        ctx.Message.To.Mailboxes.Select(m => m.Address).Should().NotContain("watch@example.com");
        ctx.EnvelopeRecipients.Should().Contain("watch@example.com");
    }

    [Fact]
    public void AddRecipient_Bcc_TouchesTheEnvelopeOnlyAndWritesNoHeader()
    {
        // Graph derives Bcc as "envelope minus To/Cc headers", so a Bcc header would be ignored
        // on delivery AND would leak the blind copy into the archived message.
        var ctx = Context();

        Apply(new RuleAction
        {
            Type = RuleActionType.AddRecipient,
            Recipient = RecipientKind.Bcc,
            Value = "archive@example.com",
        }, ctx);

        ctx.EnvelopeRecipients.Should().Contain("archive@example.com");
        ctx.Message.Bcc.Count.Should().Be(0);
        AsText(ctx.Message).Should().NotContain("archive@example.com");
    }

    [Fact]
    public void AddRecipient_AlreadyPresent_IsNotDuplicated()
    {
        var ctx = Context(recipients: ["rcpt@example.com"]);

        Apply(new RuleAction
        {
            Type = RuleActionType.AddRecipient,
            Recipient = RecipientKind.To,
            Value = "RCPT@EXAMPLE.COM",
        }, ctx);

        ctx.EnvelopeRecipients.Should().ContainSingle();
        ctx.Message.To.Mailboxes.Should().ContainSingle();
    }

    [Fact]
    public void AddRecipient_InvalidAddress_ChangesNothingAndWarns()
    {
        var ctx = Context();

        var effect = Apply(new RuleAction
        {
            Type = RuleActionType.AddRecipient,
            Recipient = RecipientKind.To,
            Value = "not an address",
        }, ctx);

        effect.Changed.Should().BeFalse();
        effect.Warning.Should().Contain("not a valid mail address");
        ctx.EnvelopeRecipients.Should().ContainSingle();
    }

    [Fact]
    public void RemoveRecipient_RemovesFromEnvelopeAndEveryHeaderList()
    {
        // Regression guard: a header-only removal leaves the address in the envelope, and Graph
        // then delivers it as a Bcc — the recipient still gets the mail, just invisibly.
        var message = TextMessage(to: "keep@example.com");
        message.To.Add(MailboxAddress.Parse("drop@example.com"));
        message.Cc.Add(MailboxAddress.Parse("drop@example.com"));
        var ctx = Context(message, recipients: ["keep@example.com", "drop@example.com"]);

        var effect = Apply(new RuleAction
        {
            Type = RuleActionType.RemoveRecipient,
            Match = "drop@example.com",
        }, ctx);

        effect.EnvelopeChanged.Should().BeTrue();
        ctx.EnvelopeRecipients.Should().ContainSingle().Which.Should().Be("keep@example.com");
        ctx.Message.To.Mailboxes.Select(m => m.Address).Should().NotContain("drop@example.com");
        ctx.Message.Cc.Mailboxes.Should().BeEmpty();
    }

    [Fact]
    public void RemoveRecipient_DomainWildcard_RemovesEveryAddressAtThatDomain()
    {
        var message = TextMessage(to: "a@partner.test");
        message.To.Add(MailboxAddress.Parse("b@partner.test"));
        message.To.Add(MailboxAddress.Parse("c@example.com"));
        var ctx = Context(message, recipients: ["a@partner.test", "b@partner.test", "c@example.com"]);

        Apply(new RuleAction { Type = RuleActionType.RemoveRecipient, Match = "@partner.test" }, ctx);

        ctx.EnvelopeRecipients.Should().ContainSingle().Which.Should().Be("c@example.com");
    }

    [Fact]
    public void RemoveRecipient_NoMatch_ReportsSoAndChangesNothing()
    {
        var ctx = Context(recipients: ["rcpt@example.com"]);

        var effect = Apply(new RuleAction { Type = RuleActionType.RemoveRecipient, Match = "nobody@x.test" }, ctx);

        effect.Changed.Should().BeFalse();
        effect.EnvelopeChanged.Should().BeFalse();
        effect.Detail.Should().Contain("no recipient matched");
        ctx.EnvelopeRecipients.Should().ContainSingle();
    }

    [Fact]
    public void ReplaceRecipient_SwapsInBothPlacesAndKeepsTheOriginalList()
    {
        var message = TextMessage(to: "old@example.com");
        message.Cc.Add(MailboxAddress.Parse("cc-old@example.com"));
        var ctx = Context(message, recipients: ["old@example.com", "cc-old@example.com"]);

        Apply(new RuleAction
        {
            Type = RuleActionType.ReplaceRecipient,
            Match = "cc-old@example.com",
            Value = "cc-new@example.com",
        }, ctx);

        ctx.EnvelopeRecipients.Should().BeEquivalentTo(["old@example.com", "cc-new@example.com"]);
        ctx.Message.Cc.Mailboxes.Select(m => m.Address).Should().ContainSingle()
            .Which.Should().Be("cc-new@example.com", "the replacement stays in the list the original sat in");
    }

    // =========================================================================
    // Subject
    // =========================================================================

    [Fact]
    public void SetSubject_ReplacesTheSubject()
    {
        var ctx = Context(TextMessage(subject: "Original"));

        Apply(new RuleAction { Type = RuleActionType.SetSubject, Value = "Replaced" }, ctx);

        ctx.Message.Subject.Should().Be("Replaced");
    }

    [Fact]
    public void PrefixSubject_AndSuffixSubject_WrapTheExistingSubject()
    {
        var ctx = Context(TextMessage(subject: "Report"));

        Apply(new RuleAction { Type = RuleActionType.PrefixSubject, Value = "[EXTERNAL] " }, ctx);
        Apply(new RuleAction { Type = RuleActionType.SuffixSubject, Value = " (unverified)" }, ctx);

        ctx.Message.Subject.Should().Be("[EXTERNAL] Report (unverified)");
    }

    [Fact]
    public void PrefixSubject_EncodedSubject_KeepsTheDecodedText()
    {
        // MimeKit decodes RFC 2047 on read and re-encodes on write, so the rule works on the
        // human-readable subject rather than on the encoded word.
        var message = TextMessage(subject: "Grüße aus München");
        var ctx = Context(message);

        Apply(new RuleAction { Type = RuleActionType.PrefixSubject, Value = "[EXT] " }, ctx);

        ctx.Message.Subject.Should().Be("[EXT] Grüße aus München");
        MimeMessage.Load(new MemoryStream(Serialise(ctx.Message))).Subject
            .Should().Be("[EXT] Grüße aus München", "the subject survives a serialise/parse round-trip");
    }

    // =========================================================================
    // Body
    // =========================================================================

    [Fact]
    public void PrependBody_TextOnlyMessage_InsertsBeforeTheBody()
    {
        var ctx = Context(TextMessage(body: "Original body"));

        var effect = Apply(new RuleAction
        {
            Type = RuleActionType.PrependBody,
            Value = "*** EXTERNAL ***",
        }, ctx);

        effect.Changed.Should().BeTrue();
        ctx.Split.TextBody!.Text.Should().StartWith("*** EXTERNAL ***").And.EndWith("Original body");
        effect.Warning.Should().Contain("no HTML part");
    }

    [Fact]
    public void AppendBody_TextOnlyMessage_InsertsAfterTheBody()
    {
        var ctx = Context(TextMessage(body: "Original body"));

        Apply(new RuleAction { Type = RuleActionType.AppendBody, Value = "-- disclaimer" }, ctx);

        ctx.Split.TextBody!.Text.Should().StartWith("Original body").And.EndWith("-- disclaimer");
    }

    [Fact]
    public void PrependBody_HtmlMessage_InsertsAfterTheBodyTag()
    {
        var ctx = Context(HtmlMessage("<html><body><p>Original</p></body></html>"));

        Apply(new RuleAction
        {
            Type = RuleActionType.PrependBody,
            Value = "banner",
            Html = "<b>banner</b>",
        }, ctx);

        ctx.Split.HtmlBody!.Text.Should().Be("<html><body><div><b>banner</b></div><p>Original</p></body></html>");
    }

    [Fact]
    public void AppendBody_HtmlMessage_InsertsBeforeTheClosingBodyTag()
    {
        var ctx = Context(HtmlMessage("<html><body><p>Original</p></body></html>"));

        Apply(new RuleAction { Type = RuleActionType.AppendBody, Value = "footer", Html = "<i>footer</i>" }, ctx);

        ctx.Split.HtmlBody!.Text.Should().Be("<html><body><p>Original</p><div><i>footer</i></div></body></html>");
    }

    [Fact]
    public void PrependBody_HtmlFragmentWithoutBodyTag_InsertsAtTheStart()
    {
        var ctx = Context(HtmlMessage("<p>Original</p>"));

        Apply(new RuleAction { Type = RuleActionType.PrependBody, Value = "x", Html = "<b>x</b>" }, ctx);

        ctx.Split.HtmlBody!.Text.Should().Be("<div><b>x</b></div><p>Original</p>");
    }

    [Fact]
    public void PrependBody_Alternative_ChangesBothRenderings()
    {
        var ctx = Context(AlternativeMessage("Original text", "<html><body>Original html</body></html>"));

        var effect = Apply(new RuleAction
        {
            Type = RuleActionType.PrependBody,
            Value = "BANNER",
            Html = "<b>BANNER</b>",
        }, ctx);

        ctx.Split.TextBody!.Text.Should().StartWith("BANNER");
        ctx.Split.HtmlBody!.Text.Should().Contain("<div><b>BANNER</b></div>");
        effect.Warning.Should().BeNull("both parts exist, so nothing was skipped");
    }

    [Fact]
    public void PrependBody_Mixed_ChangesTheBodyPartNotTheAttachments()
    {
        var ctx = Context(WithAttachments(
            new TextPart("plain") { Text = "Original body" },
            ("report.pdf", "application/pdf", 128)));

        Apply(new RuleAction { Type = RuleActionType.PrependBody, Value = "BANNER" }, ctx);

        ctx.Split.TextBody!.Text.Should().StartWith("BANNER");
        ctx.Split.Attachments.Should().ContainSingle();
    }

    [Fact]
    public void PrependBody_MissingHtmlPart_IsNeverSynthesised()
    {
        // Adding an HTML part would flip the delivered body from plain text to HTML for every
        // recipient — a far bigger change than the banner the rule asked for.
        var ctx = Context(TextMessage());

        Apply(new RuleAction { Type = RuleActionType.PrependBody, Value = "x", Html = "<b>x</b>" }, ctx);

        ctx.Split.HtmlBody.Should().BeNull();
    }

    [Fact]
    public void PrependBody_AttachmentOnlyMessage_ChangesNothingAndWarns()
    {
        var message = TextMessage();
        message.Body = new Multipart("mixed")
        {
            new MimePart("application", "pdf")
            {
                Content = new MimeContent(new MemoryStream(new byte[64])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "only.pdf" },
                FileName = "only.pdf",
            },
        };
        var ctx = Context(message);

        var effect = Apply(new RuleAction { Type = RuleActionType.PrependBody, Value = "x" }, ctx);

        effect.Changed.Should().BeFalse();
        effect.Warning.Should().Contain("no text or HTML body part");
    }

    [Fact]
    public void PrependBody_NonAsciiBanner_SurvivesAnIso88591Body()
    {
        // SetText updates the part's charset; assigning Text alone would leave the part claiming
        // iso-8859-1 while holding characters that encoding cannot represent.
        var message = TextMessage();
        var part = new TextPart("plain");
        part.SetText(Encoding.Latin1, "Original");
        message.Body = part;
        var ctx = Context(message);

        Apply(new RuleAction { Type = RuleActionType.PrependBody, Value = "Grüße 日本" }, ctx);

        var reloaded = MimeMessage.Load(new MemoryStream(Serialise(ctx.Message)));
        MimeMessageSplitter.Split(reloaded).TextBody!.Text.Should().Contain("Grüße 日本");
    }

    [Fact]
    public void BuildHtmlFragment_WithoutHtml_EscapesTheTextAndConvertsNewlines()
    {
        // Escaping first is what stops an operator's '<' from becoming markup.
        var fragment = MessageRuleActions.BuildHtmlFragment(new RuleAction
        {
            Type = RuleActionType.PrependBody,
            Value = "a <b>bold</b> claim\nsecond line",
        });

        fragment.Should().Be("<div>a &lt;b&gt;bold&lt;/b&gt; claim<br>second line</div>");
    }

    [Fact]
    public void BuildHtmlFragment_WithHtml_WrapsItInASingleBlock()
    {
        var fragment = MessageRuleActions.BuildHtmlFragment(new RuleAction
        {
            Type = RuleActionType.PrependBody,
            Value = "plain",
            Html = "<span style=\"color:red\">warn</span>",
        });

        fragment.Should().Be("<div><span style=\"color:red\">warn</span></div>");
    }

    // =========================================================================
    // Headers
    // =========================================================================

    [Fact]
    public void SetHeader_ReplacesEveryExistingOccurrence()
    {
        var message = TextMessage();
        message.Headers.Add("X-Tag", "one");
        message.Headers.Add("X-Tag", "two");
        var ctx = Context(message);

        Apply(new RuleAction { Type = RuleActionType.SetHeader, HeaderName = "X-Tag", Value = "final" }, ctx);

        ctx.Message.Headers.Where(h => h.Field == "X-Tag").Should().ContainSingle()
            .Which.Value.Should().Be("final");
    }

    [Fact]
    public void AddHeader_KeepsExistingOccurrences()
    {
        var message = TextMessage();
        message.Headers.Add("X-Tag", "one");
        var ctx = Context(message);

        Apply(new RuleAction { Type = RuleActionType.AddHeader, HeaderName = "X-Tag", Value = "two" }, ctx);

        ctx.Message.Headers.Where(h => h.Field == "X-Tag").Select(h => h.Value)
            .Should().BeEquivalentTo(["one", "two"]);
    }

    [Fact]
    public void RemoveHeader_RemovesEveryOccurrence()
    {
        var message = TextMessage();
        message.Headers.Add("X-Tag", "one");
        message.Headers.Add("X-Tag", "two");
        var ctx = Context(message);

        var effect = Apply(new RuleAction { Type = RuleActionType.RemoveHeader, HeaderName = "X-Tag" }, ctx);

        effect.Detail.Should().Contain("2 occurrence");
        ctx.Message.Headers.Any(h => h.Field == "X-Tag").Should().BeFalse();
    }

    [Fact]
    public void SetHeader_ValueWithNewlines_IsStripped()
    {
        // A newline in a header value ends the header and lets the rest be read as headers of
        // its own — header injection, straight from a config file.
        var ctx = Context();

        Apply(new RuleAction
        {
            Type = RuleActionType.SetHeader,
            HeaderName = "X-Injected",
            Value = "ok\r\nBcc: attacker@evil.test",
        }, ctx);

        var value = ctx.Message.Headers["X-Injected"];
        value.Should().Be("okBcc: attacker@evil.test");
        value.Should().NotContain("\r").And.NotContain("\n");
        ctx.Message.Bcc.Count.Should().Be(0);
    }

    // =========================================================================
    // Attachments
    // =========================================================================

    [Fact]
    public void RemoveAttachments_ByExtension_RemovesOnlyMatchingOnes()
    {
        var ctx = Context(WithAttachments(null,
            ("report.pdf", "application/pdf", 128),
            ("macro.docm", "application/msword", 128)));

        var effect = Apply(new RuleAction
        {
            Type = RuleActionType.RemoveAttachments,
            AttachmentMatch = AttachmentMatchMode.Extension,
            Value = ".docm;.xlsm",
        }, ctx);

        effect.Changed.Should().BeTrue();
        MessageRuleEvaluator.AttachmentNames(ctx).Should().BeEquivalentTo(["report.pdf"]);
    }

    [Fact]
    public void RemoveAttachments_ByExtension_AcceptsEntriesWithoutALeadingDot()
    {
        var ctx = Context(WithAttachments(null, ("macro.docm", "application/msword", 128)));

        Apply(new RuleAction
        {
            Type = RuleActionType.RemoveAttachments,
            AttachmentMatch = AttachmentMatchMode.Extension,
            Value = "docm",
        }, ctx);

        MessageRuleEvaluator.AttachmentNames(ctx).Should().BeEmpty();
    }

    [Fact]
    public void RemoveAttachments_ByNamePattern_UsesWildcards()
    {
        var ctx = Context(WithAttachments(null,
            ("invoice-4711.pdf", "application/pdf", 128),
            ("contract.pdf", "application/pdf", 128)));

        Apply(new RuleAction
        {
            Type = RuleActionType.RemoveAttachments,
            AttachmentMatch = AttachmentMatchMode.NamePattern,
            Value = "invoice-*",
        }, ctx);

        MessageRuleEvaluator.AttachmentNames(ctx).Should().BeEquivalentTo(["contract.pdf"]);
    }

    [Fact]
    public void RemoveAttachments_ByMinSize_RemovesTheLargeOnes()
    {
        var ctx = Context(WithAttachments(null,
            ("small.pdf", "application/pdf", 64),
            ("large.pdf", "application/pdf", 8192)));

        Apply(new RuleAction
        {
            Type = RuleActionType.RemoveAttachments,
            AttachmentMatch = AttachmentMatchMode.MinSizeBytes,
            Value = "4096",
        }, ctx);

        MessageRuleEvaluator.AttachmentNames(ctx).Should().BeEquivalentTo(["small.pdf"]);
    }

    [Fact]
    public void RemoveAttachments_InlineParts_AreKept()
    {
        // Removing a cid:-referenced image leaves a dangling reference and a visibly broken body.
        var ctx = Context(WithInlineImage("logo.png"));

        var effect = Apply(new RuleAction
        {
            Type = RuleActionType.RemoveAttachments,
            AttachmentMatch = AttachmentMatchMode.NamePattern,
            Value = "*.png",
        }, ctx);

        effect.Changed.Should().BeFalse();
        effect.Warning.Should().Contain("inline");
        MessageRuleEvaluator.AttachmentNames(ctx).Should().BeEquivalentTo(["logo.png"]);
    }

    [Fact]
    public void RemoveAttachments_LastAttachmentGone_CollapsesTheMultipartWrapper()
    {
        var ctx = Context(WithAttachments(
            new TextPart("plain") { Text = "Body" },
            ("only.pdf", "application/pdf", 128)));

        Apply(new RuleAction
        {
            Type = RuleActionType.RemoveAttachments,
            AttachmentMatch = AttachmentMatchMode.Extension,
            Value = ".pdf",
        }, ctx);

        ctx.Message.Body.Should().BeOfType<TextPart>("a mixed container around a single body part is noise");
        ctx.Split.TextBody!.Text.Should().Be("Body");
    }

    [Fact]
    public void RemoveAttachments_NoMatch_ChangesNothing()
    {
        var ctx = Context(WithAttachments(null, ("report.pdf", "application/pdf", 128)));

        var effect = Apply(new RuleAction
        {
            Type = RuleActionType.RemoveAttachments,
            AttachmentMatch = AttachmentMatchMode.Extension,
            Value = ".exe",
        }, ctx);

        effect.Changed.Should().BeFalse();
        MessageRuleEvaluator.AttachmentNames(ctx).Should().BeEquivalentTo(["report.pdf"]);
    }

    // =========================================================================
    // Importance, From, Reply-To
    // =========================================================================

    [Theory]
    [InlineData("High", MessageImportance.High)]
    [InlineData("low", MessageImportance.Low)]
    [InlineData("Normal", MessageImportance.Normal)]
    public void SetImportance_AcceptsTheDocumentedTokens(string token, MessageImportance expected)
    {
        var ctx = Context();

        Apply(new RuleAction { Type = RuleActionType.SetImportance, Value = token }, ctx);

        ctx.Message.Importance.Should().Be(expected);
    }

    [Fact]
    public void SetImportance_UnknownToken_ChangesNothingAndWarns()
    {
        var ctx = Context();

        var effect = Apply(new RuleAction { Type = RuleActionType.SetImportance, Value = "Urgent!" }, ctx);

        effect.Changed.Should().BeFalse();
        effect.Warning.Should().Contain("Low, Normal or High");
    }

    [Fact]
    public void SetFrom_ChangesTheHeaderAndTheEnvelopeSender()
    {
        // The sending mailbox comes from the envelope sender, not the header — changing only the
        // header would send as the original mailbox with a mismatched From, which Exchange
        // refuses as ErrorSendAsDenied.
        var ctx = Context(TextMessage(from: "old@example.com"), envelopeFrom: "old@example.com");

        var effect = Apply(new RuleAction { Type = RuleActionType.SetFrom, Value = "new@example.com" }, ctx);

        effect.EnvelopeChanged.Should().BeTrue();
        ctx.EnvelopeFrom.Should().Be("new@example.com");
        ctx.Message.From.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("new@example.com");
    }

    [Fact]
    public void SetReplyTo_TouchesOnlyTheReplyToHeader()
    {
        var ctx = Context(TextMessage(from: "sender@example.com"), envelopeFrom: "sender@example.com");

        Apply(new RuleAction { Type = RuleActionType.SetReplyTo, Value = "support@example.com" }, ctx);

        ctx.Message.ReplyTo.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("support@example.com");
        ctx.EnvelopeFrom.Should().Be("sender@example.com");
        ctx.Message.From.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("sender@example.com");
    }

    // =========================================================================
    // Address matching
    // =========================================================================

    [Theory]
    [InlineData("user@example.com", "user@example.com", true)]
    [InlineData("USER@EXAMPLE.COM", "user@example.com", true)]
    [InlineData("user@example.com", "@example.com", true)]
    [InlineData("user@sub.example.com", "@example.com", false)]
    [InlineData("invoice@example.com", "invoice*", true)]
    [InlineData("user@example.com", "a@x.test;user@example.com", true)]
    [InlineData("user@example.com", "other@example.com", false)]
    public void AddressMatches_HandlesExactDomainAndWildcardEntries(string address, string pattern, bool expected)
    {
        MessageRuleActions.AddressMatches(address, pattern).Should().Be(expected);
    }

    // =========================================================================
    // Whitespace in prose values
    // =========================================================================

    [Theory]
    [InlineData(RuleActionType.SetSubject, true)]
    [InlineData(RuleActionType.PrefixSubject, true)]
    [InlineData(RuleActionType.SuffixSubject, true)]
    [InlineData(RuleActionType.PrependBody, true)]
    [InlineData(RuleActionType.AppendBody, true)]
    [InlineData(RuleActionType.AddRecipient, false)]
    [InlineData(RuleActionType.SetHeader, false)]
    [InlineData(RuleActionType.SetImportance, false)]
    [InlineData(RuleActionType.RemoveAttachments, false)]
    internal void PreservesWhitespace_CoversTheProseActionsOnly(RuleActionType type, bool expected)
    {
        // The prose actions splice their value into the message verbatim, so a trailing space is
        // part of what the operator wrote. Everywhere else the value is a token and trimming is
        // what the rest of the tool does.
        RuleActionSchema.PreservesWhitespace(type).Should().Be(expected);
    }

    [Fact]
    public void IsValueMissing_ProseAction_AcceptsAWhitespaceOnlyValue()
    {
        RuleActionSchema.IsValueMissing(RuleActionType.PrefixSubject, " ").Should().BeFalse(
            "a prefix of a single space is a deliberate choice, not an empty field");
        RuleActionSchema.IsValueMissing(RuleActionType.PrefixSubject, "").Should().BeTrue();
        RuleActionSchema.IsValueMissing(RuleActionType.PrefixSubject, null).Should().BeTrue();
    }

    [Fact]
    public void IsValueMissing_TokenAction_RejectsAWhitespaceOnlyValue()
    {
        RuleActionSchema.IsValueMissing(RuleActionType.SetImportance, "  ").Should().BeTrue();
    }

    [Fact]
    public void PrefixSubject_TrailingSpace_IsKept()
    {
        // The reported defect: "[Prefix] " must not become "[Prefix]", or the prefix runs into
        // the subject.
        var ctx = Context(TextMessage(subject: "Quarterly report"));

        Apply(new RuleAction { Type = RuleActionType.PrefixSubject, Value = "[EXTERNAL] " }, ctx);

        ctx.Message.Subject.Should().Be("[EXTERNAL] Quarterly report");
    }

    [Fact]
    public void SuffixSubject_LeadingSpace_IsKept()
    {
        var ctx = Context(TextMessage(subject: "Quarterly report"));

        Apply(new RuleAction { Type = RuleActionType.SuffixSubject, Value = " (unverified)" }, ctx);

        ctx.Message.Subject.Should().Be("Quarterly report (unverified)");
    }

    [Fact]
    public void PrependBody_LeadingAndTrailingWhitespace_IsKept()
    {
        var ctx = Context(TextMessage(body: "Original body"));

        Apply(new RuleAction { Type = RuleActionType.PrependBody, Value = "  indented banner  " }, ctx);

        ctx.Split.TextBody!.Text.Should().StartWith("  indented banner  ");
    }
}
