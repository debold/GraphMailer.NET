using System.Text;
using GraphMailer.Service.Infrastructure.Rules;
using GraphMailer.Service.Services;
using MimeKit;
using static GraphMailer.Tests.Unit.Infrastructure.Rules.RuleTestFactory;

namespace GraphMailer.Tests.Unit.Infrastructure.Rules;

/// <summary>
/// The input snapshot: how the message is parsed, what the derived views contain, and how the
/// context behaves when the message cannot be parsed at all.
/// </summary>
public sealed class MessageRuleContextTests
{
    [Fact]
    public void Create_ValidMessage_ExposesTheEnvelopeAndTheParsedMessage()
    {
        var eml = Serialise(TextMessage(subject: "Hello", body: "Body"));

        var ctx = MessageRuleContext.Create(
            eml, "sender@example.com", ["a@example.com", "b@example.com"], Session(clientIp: "10.1.2.3"), "msg-1");

        ctx.ParseFailed.Should().BeFalse();
        ctx.EnvelopeFrom.Should().Be("sender@example.com");
        ctx.EnvelopeRecipients.Should().BeEquivalentTo(["a@example.com", "b@example.com"]);
        ctx.Message.Subject.Should().Be("Hello");
        ctx.MessageSizeBytes.Should().Be(eml.LongLength);
        ctx.MessageId.Should().Be("msg-1");
        ctx.Session.ClientIp.Should().Be("10.1.2.3");
    }

    [Fact]
    public void Create_UnparsableBytes_FlagsParseFailedInsteadOfThrowing()
    {
        var garbage = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };

        var act = () => MessageRuleContext.Create(garbage, "s@example.com", ["r@example.com"], Session());

        act.Should().NotThrow();
    }

    [Fact]
    public void EnvelopeRecipients_IsMutable_AndIsWhatDecidesDelivery()
    {
        var ctx = Context(recipients: ["a@example.com"]);

        ctx.EnvelopeRecipients.Add("b@example.com");

        ctx.EnvelopeRecipients.Should().BeEquivalentTo(["a@example.com", "b@example.com"]);
    }

    [Fact]
    public void Split_MatchesTheSplitterTheDeliveryPathUses()
    {
        // The attachment view must agree with MimeMessageSplitter exactly — GraphApiClient and
        // the reception statistics use it, so a second notion of "what is an attachment" would
        // make a rule act on parts the delivery path classifies differently.
        var message = WithAttachments(null, ("a.pdf", "application/pdf", 64), ("b.txt", "text/plain", 32));
        var ctx = Context(message);

        var expected = MimeMessageSplitter.Split(message);

        ctx.Split.Attachments.Should().HaveCount(expected.Attachments.Count);
        MessageRuleEvaluator.AttachmentNames(ctx).Should().BeEquivalentTo(["a.pdf", "b.txt"]);
    }

    [Fact]
    public void BodyText_And_BodyHtml_ReturnTheChosenRenderings()
    {
        var ctx = Context(AlternativeMessage("plain text", "<html><body>html text</body></html>"));

        ctx.BodyText.Should().Be("plain text");
        ctx.BodyHtml.Should().Contain("html text");
    }

    [Fact]
    public void BodyText_MissingPart_IsEmptyRatherThanNull()
    {
        var ctx = Context(HtmlMessage());

        ctx.BodyText.Should().BeEmpty();
        ctx.BodyHtml.Should().NotBeEmpty();
    }

    [Fact]
    public void BodyText_OverTheCap_IsTruncatedAndFlagged()
    {
        var ctx = Context(TextMessage(body: new string('x', 5_000)), maxBodyScanBytes: 100);

        ctx.BodyTruncated.Should().BeFalse("nothing has asked for the body yet");
        ctx.BodyText.Should().HaveLength(100);
        ctx.BodyTruncated.Should().BeTrue();
    }

    [Fact]
    public void BodyTruncated_StaysFalse_WhenNothingReadsTheBody()
    {
        // Body and attachment views are materialised on demand, so a rule set that never asks
        // about content never decodes any.
        var ctx = Context(TextMessage(body: new string('x', 5_000)), maxBodyScanBytes: 10);

        ctx.BodyTruncated.Should().BeFalse();
    }

    [Theory]
    [InlineData("signed", "application/pkcs7-signature", MimeProtectionKind.Signed)]
    [InlineData("signed", "application/pgp-signature", MimeProtectionKind.Signed)]
    [InlineData("encrypted", "application/pgp-encrypted", MimeProtectionKind.Encrypted)]
    internal void Protection_ClassifiesTheStandardShapes(
        string subtype, string protocol, MimeProtectionKind expected)
    {
        Context(ProtectedMessage(subtype, protocol)).Protection.Should().Be(expected);
    }

    [Fact]
    public void Protection_PlainMessage_IsNone()
    {
        Context(TextMessage()).Protection.Should().Be(MimeProtectionKind.None);
    }

    [Fact]
    public void Protection_Pkcs7Mime_UsesTheSmimeTypeParameter()
    {
        var signed = TextMessage();
        var signedPart = new MimePart("application", "pkcs7-mime")
        {
            Content = new MimeContent(new MemoryStream(new byte[32])),
        };
        signedPart.ContentType.Parameters.Add("smime-type", "signed-data");
        signed.Body = signedPart;

        var encrypted = TextMessage();
        var encryptedPart = new MimePart("application", "pkcs7-mime")
        {
            Content = new MimeContent(new MemoryStream(new byte[32])),
        };
        encryptedPart.ContentType.Parameters.Add("smime-type", "enveloped-data");
        encrypted.Body = encryptedPart;

        Context(signed).Protection.Should().Be(MimeProtectionKind.Signed);
        Context(encrypted).Protection.Should().Be(MimeProtectionKind.Encrypted);
    }

    [Fact]
    public void Protection_NestedSignedPart_IsFound()
    {
        // Many mailers wrap a signed body inside a multipart/mixed to carry an attachment.
        var message = TextMessage();
        message.Body = new Multipart("mixed")
        {
            new Multipart("signed")
            {
                new TextPart("plain") { Text = "content" },
                new MimePart("application", "pkcs7-signature")
                {
                    Content = new MimeContent(new MemoryStream(new byte[16])),
                },
            },
        };

        Context(message).Protection.Should().Be(MimeProtectionKind.Signed);
    }

    [Fact]
    public void InvalidateDerived_RefreshesTheCachedViews()
    {
        var ctx = Context(TextMessage(body: "before"));
        ctx.BodyText.Should().Be("before");

        ctx.Split.TextBody!.SetText(Encoding.UTF8, "after");
        ctx.InvalidateDerived();

        ctx.BodyText.Should().Be("after");
    }
}
