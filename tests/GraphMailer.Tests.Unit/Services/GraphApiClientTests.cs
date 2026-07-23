using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;
using MimeKit;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Static delivery logic of <see cref="GraphApiClient"/>:
/// permanent-vs-transient classification of Graph rejections, the total-request-size
/// rebalancing that keeps direct sendMail requests under Graph's hard 4 MB cap, the
/// recipient-count guard, and the MIME → Graph fidelity mapping (inline CID images,
/// importance, custom x-headers, Message-ID/threading).
/// </summary>
public sealed class GraphApiClientTests
{
    // =========================================================================
    // IsPermanentRejection — classification
    // =========================================================================

    [Theory]
    [InlineData(400, "BadRequest")]                       // malformed request — identical resend cannot succeed
    [InlineData(404, "ErrorItemNotFound")]                // sender mailbox/user does not exist
    [InlineData(413, "PayloadTooLarge")]                  // exceeds the 4 MB request cap
    [InlineData(409, "MailboxNotEnabledForRESTAPI")]      // hybrid mailbox without EXO REST — code-based
    [InlineData(403, "ErrorInvalidRecipients")]           // code-based regardless of status
    [InlineData(422, "ErrorRecipientLimitExceeded")]
    [InlineData(500, "ErrorMessageSizeExceeded")]
    [InlineData(200, "ErrorInvalidUser")]
    public void IsPermanentRejection_PermanentCases_ReturnsTrue(int status, string code)
        => GraphApiClient.IsPermanentRejection(status, code).Should().BeTrue();

    [Theory]
    [InlineData(429, "TooManyRequests")]                  // throttling — retry after back-off
    [InlineData(500, "InternalServerError")]              // outage
    [InlineData(503, "ServiceUnavailable")]
    [InlineData(401, "InvalidAuthenticationToken")]       // operator-fixable config problem
    [InlineData(403, "Authorization_RequestDenied")]      // missing permission — operator can grant it
    [InlineData(408, "Timeout")]
    public void IsPermanentRejection_TransientCases_ReturnsFalse(int status, string code)
        => GraphApiClient.IsPermanentRejection(status, code).Should().BeFalse();

    // =========================================================================
    // RebalanceForRequestCap — total-size routing
    // =========================================================================

    private static FileAttachment Att(string name, int sizeBytes) => new()
    {
        Name = name,
        ContentType = "application/octet-stream",
        ContentBytes = new byte[sizeBytes],
    };

    [Fact]
    public void Rebalance_TotalUnderCap_MovesNothing()
    {
        var small = new List<FileAttachment> { Att("a", 1_000_000), Att("b", 1_000_000) };
        var large = new List<GraphApiClient.LargeAttachment>();

        var moved = GraphApiClient.RebalanceForRequestCap(bodyLength: 10_000, small, large);

        moved.Should().Be(0);
        small.Should().HaveCount(2);
        large.Should().BeEmpty();
    }

    [Fact]
    public void Rebalance_IndividuallySmallButCollectivelyLarge_MovesLargestUntilFit()
    {
        // Three attachments below the 3 MB per-attachment threshold, but ~4.6 MB total
        // after base64 — the old per-attachment routing sent this directly and Graph
        // rejected it with 413.
        var small = new List<FileAttachment> { Att("big", 2_000_000), Att("mid", 1_000_000), Att("tiny", 500_000) };
        var large = new List<GraphApiClient.LargeAttachment>();

        var moved = GraphApiClient.RebalanceForRequestCap(bodyLength: 10_000, small, large);

        moved.Should().BeGreaterThan(0);
        large.Select(a => a.Name).Should().Contain("big", "the largest attachment is moved first");
        // The remaining direct payload must fit the budget.
        var estimate = 10_000 + small.Sum(a => (long)a.ContentBytes!.Length * 4 / 3);
        estimate.Should().BeLessThanOrEqualTo(GraphApiClient.MaxDirectRequestBytes);
    }

    [Fact]
    public void Rebalance_HugeBodyAloneOverCap_MovesAllAttachmentsAndStops()
    {
        // A body that alone exceeds the cap cannot be fixed by rebalancing —
        // the loop must terminate (no infinite loop) with every attachment moved.
        var small = new List<FileAttachment> { Att("a", 100_000) };
        var large = new List<GraphApiClient.LargeAttachment>();

        var moved = GraphApiClient.RebalanceForRequestCap(bodyLength: 5_000_000, small, large);

        moved.Should().Be(1);
        small.Should().BeEmpty();
    }

    [Fact]
    public void Rebalance_MovedInlineAttachment_KeepsContentIdAndInlineFlag()
    {
        var inline = Att("logo.png", 3_000_000);
        inline.ContentId = "logo@example.com";
        inline.IsInline = true;
        var small = new List<FileAttachment> { inline };
        var large = new List<GraphApiClient.LargeAttachment>();

        GraphApiClient.RebalanceForRequestCap(bodyLength: 1_000_000, small, large);

        large.Should().ContainSingle();
        large[0].ContentId.Should().Be("logo@example.com");
        large[0].IsInline.Should().BeTrue("a moved inline image must not become a visible attachment");
    }

    // =========================================================================
    // Recipient-count guard
    // =========================================================================

    [Fact]
    public async Task SendAsync_TooManyRecipients_ThrowsPermanentWithoutGraphCall()
    {
        var opts = Substitute.For<IOptionsMonitor<GraphApiOptions>>();
        opts.CurrentValue.Returns(new GraphApiOptions
        {
            TenantId = "tenant", ClientId = "client", ClientSecret = "s3cr3t"
        });
        var sut = new GraphApiClient(
            opts,
            new GraphClientProvider(NullLogger<GraphClientProvider>.Instance),
            NullLogger<GraphApiClient>.Instance);
        var recipients = Enumerable.Range(0, GraphApiClient.MaxRecipients + 1)
            .Select(i => $"rcpt{i}@example.com").ToList();

        var act = () => sut.SendAsync([1, 2, 3], "sender@example.com", recipients, "msg-too-many", saveToSentItems: true);

        (await act.Should().ThrowAsync<GraphDeliveryException>(
                "over-limit messages must fail fast instead of a doomed Graph call"))
            .Which.IsPermanent.Should().BeTrue();
    }

    // =========================================================================
    // BuildMessage — MIME → Graph fidelity
    // =========================================================================

    private static MimeMessage BaseMime(Action<MimeMessage>? mutate = null)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse("sender@example.com"));
        mime.To.Add(MailboxAddress.Parse("rcpt@example.com"));
        mime.Subject = "Fidelity";
        mime.Body = new TextPart("plain") { Text = "Body" };
        mutate?.Invoke(mime);
        return mime;
    }

    [Fact]
    public void BuildMessage_HighImportance_IsMapped()
    {
        var mime = BaseMime(m => m.Importance = MessageImportance.High);

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.Importance.Should().Be(Importance.High);
    }

    [Fact]
    public void BuildMessage_XPriorityFallback_IsMapped()
    {
        var mime = BaseMime(m => m.XPriority = XMessagePriority.Highest);

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.Importance.Should().Be(Importance.High,
            "legacy senders signal priority via X-Priority instead of the Importance header");
    }

    [Fact]
    public void BuildMessage_MessageIdAndThreadingHeaders_AreForwarded()
    {
        var mime = BaseMime(m =>
        {
            m.MessageId = "original-id@example.com";
            m.InReplyTo = "parent-id@example.com";
            m.References.Add("thread-root@example.com");
        });

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.InternetMessageId.Should().Be("<original-id@example.com>");
        msg.SingleValueExtendedProperties.Should().Contain(p =>
            p.Id == "String 0x1042" && p.Value == "<parent-id@example.com>");   // PidTagInReplyToId
        msg.SingleValueExtendedProperties.Should().Contain(p =>
            p.Id == "String 0x1039" && p.Value!.Contains("<thread-root@example.com>"));   // PidTagInternetReferences
    }

    [Fact]
    public void BuildMessage_CustomXHeaders_AreForwarded_ReservedOnesAreNot()
    {
        var mime = BaseMime(m =>
        {
            m.Headers.Add("X-Legacy-App", "invoice-system");
            m.Headers.Add("X-MS-Exchange-Organization-SCL", "1");   // transport-reserved
            m.Headers.Add("X-Priority", "1");                       // mapped to Importance instead
            m.Headers.Add("Reply-By", "tomorrow");                  // non-x → Graph would reject it
        });

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.InternetMessageHeaders.Should().ContainSingle(h => h.Name == "X-Legacy-App" && h.Value == "invoice-system");
        msg.InternetMessageHeaders.Should().NotContain(h => h.Name!.StartsWith("X-MS-Exchange", StringComparison.OrdinalIgnoreCase));
        msg.InternetMessageHeaders.Should().NotContain(h => h.Name == "X-Priority");
        msg.InternetMessageHeaders.Should().NotContain(h => h.Name == "Reply-By");
    }

    [Fact]
    public void BuildMessage_MoreThanFiveCustomHeaders_AreCappedAtGraphsLimit()
    {
        // Graph rejects a message carrying more than 5 custom headers with 400
        // InvalidInternetMessageHeaderCollection, and 400 is classified permanent —
        // uncapped, an ordinary mail with X-Mailer + X-Spam-* headers would be NDR'd.
        var mime = BaseMime(m =>
        {
            for (var i = 1; i <= 9; i++)
                m.Headers.Add($"X-Scanner-{i}", $"value-{i}");
        });

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.InternetMessageHeaders.Should().HaveCount(GraphApiClient.MaxCustomHeaders);
        msg.InternetMessageHeaders.Should().Contain(h => h.Name == "X-Scanner-1",
            "the first headers in message order are the ones kept");
        msg.InternetMessageHeaders.Should().NotContain(h => h.Name == "X-Scanner-9");
    }

    [Fact]
    public void BuildMessage_WithoutOptionalExtras_OmitsHeadersAndSender()
    {
        var mime = BaseMime(m =>
        {
            m.Headers.Add("X-Legacy-App", "invoice-system");
            m.Sender = MailboxAddress.Parse("shared@example.com");
        });

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"],
            GraphApiClient.MessageFidelity.WithoutOptionalExtras);

        msg.InternetMessageHeaders.Should().BeNull("the retry after a header rejection must not resend them");
        msg.Sender.Should().BeNull();
        msg.Subject.Should().Be("Fidelity", "only the extras are dropped, never the message itself");
        msg.ToRecipients.Should().ContainSingle();
    }

    // =========================================================================
    // BuildMessage — sensitivity, receipts, sender, priority
    // =========================================================================

    [Theory]
    [InlineData("Personal", "1")]
    [InlineData("Private", "2")]
    [InlineData("Company-Confidential", "3")]
    public void BuildMessage_SensitivityHeader_IsMappedToMapiProperty(string header, string expected)
    {
        // Graph's message resource has no sensitivity property (only event does), so
        // PidTagSensitivity is the only carrier for the private/confidential marking.
        var mime = BaseMime(m => m.Headers.Add("Sensitivity", header));

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.SingleValueExtendedProperties.Should().Contain(p =>
            p.Id == "Integer 0x0036" && p.Value == expected);
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("")]
    public void BuildMessage_NormalOrEmptySensitivity_SetsNoMapiProperty(string header)
    {
        var mime = BaseMime(m => { if (header.Length > 0) m.Headers.Add("Sensitivity", header); });

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        (msg.SingleValueExtendedProperties ?? []).Should().NotContain(p => p.Id == "Integer 0x0036",
            "normal is Graph's default — the property is omitted rather than set to 0");
    }

    [Fact]
    public void BuildMessage_DispositionNotificationTo_RequestsReadReceipt()
    {
        var mime = BaseMime(m => m.Headers.Add("Disposition-Notification-To", "sender@example.com"));

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.IsReadReceiptRequested.Should().BeTrue();
        msg.IsDeliveryReceiptRequested.Should().BeNull("only a read receipt was requested");
    }

    [Fact]
    public void BuildMessage_ReturnReceiptTo_RequestsDeliveryReceipt()
    {
        var mime = BaseMime(m => m.Headers.Add("Return-Receipt-To", "sender@example.com"));

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.IsDeliveryReceiptRequested.Should().BeTrue();
    }

    [Fact]
    public void BuildMessage_NoReceiptHeaders_LeavesFlagsUnset()
    {
        var msg = GraphApiClient.BuildMessage(BaseMime(), [], ["rcpt@example.com"]);

        msg.IsReadReceiptRequested.Should().BeNull();
        msg.IsDeliveryReceiptRequested.Should().BeNull();
    }

    [Fact]
    public void BuildMessage_SenderHeader_IsMapped()
    {
        var mime = BaseMime(m => m.Sender = MailboxAddress.Parse("shared@example.com"));

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.Sender!.EmailAddress!.Address.Should().Be("shared@example.com");
        msg.From!.EmailAddress!.Address.Should().Be("sender@example.com",
            "Sender and From are distinct for shared mailboxes and 'on behalf of' senders");
    }

    [Theory]
    [InlineData(MessagePriority.Urgent, Importance.High)]
    [InlineData(MessagePriority.NonUrgent, Importance.Low)]
    public void BuildMessage_PriorityHeader_IsMappedToImportance(MessagePriority priority, Importance expected)
    {
        var mime = BaseMime(m => m.Priority = priority);

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.Importance.Should().Be(expected, "RFC 2156 Priority is the third priority signal after Importance and X-Priority");
    }

    // =========================================================================
    // BuildMessage — envelope decides delivery, not the headers
    // =========================================================================

    [Fact]
    public void BuildMessage_HeaderRecipientNotInEnvelope_IsNotDeliveredTo()
    {
        // A client that RCPT TO's only a subset of its To: header (per-domain splitting)
        // must not make the relay invent recipients.
        var mime = BaseMime(m => m.To.Add(MailboxAddress.Parse("never-rcpt@example.com")));

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.ToRecipients.Should().ContainSingle()
            .Which.EmailAddress!.Address.Should().Be("rcpt@example.com");
        msg.BccRecipients.Should().BeEmpty();
    }

    [Fact]
    public void BuildMessage_EnvelopeRecipients_AreSplitAcrossToCcAndBcc_WithoutLoss()
    {
        var mime = BaseMime(m => m.Cc.Add(MailboxAddress.Parse("cc@example.com")));

        var msg = GraphApiClient.BuildMessage(mime, [],
            ["rcpt@example.com", "cc@example.com", "blind@example.com"]);

        msg.ToRecipients.Should().ContainSingle(r => r.EmailAddress!.Address == "rcpt@example.com");
        msg.CcRecipients.Should().ContainSingle(r => r.EmailAddress!.Address == "cc@example.com");
        msg.BccRecipients.Should().ContainSingle(r => r.EmailAddress!.Address == "blind@example.com",
            "an envelope address in neither header was blind-copied");
    }

    [Fact]
    public void BuildMessage_ReplyTo_IsNotFilteredAgainstTheEnvelope()
    {
        // Reply-To is not a delivery target — filtering it against the envelope the way
        // To:/Cc: are would break every reply. The two sit next to each other in
        // BuildMessage, so this guards against "making it consistent".
        var mime = BaseMime(m => m.ReplyTo.Add(new MailboxAddress("Support Desk", "support@example.com")));

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        var replyTo = msg.ReplyTo.Should().ContainSingle().Subject;
        replyTo.EmailAddress!.Address.Should().Be("support@example.com");
        replyTo.EmailAddress.Name.Should().Be("Support Desk");
    }

    [Fact]
    public void BuildMessage_RecipientDisplayNames_ArePreserved()
    {
        var mime = BaseMime();
        mime.To.Clear();
        mime.To.Add(new MailboxAddress("Jane Doe", "rcpt@example.com"));

        var msg = GraphApiClient.BuildMessage(mime, [], ["rcpt@example.com"]);

        msg.ToRecipients.Should().ContainSingle()
            .Which.EmailAddress!.Name.Should().Be("Jane Doe",
                "the envelope filter must not strip the display name off a kept address");
    }

    [Fact]
    public void FindNonEnvelopeRecipients_ReportsHeaderOnlyAddresses()
    {
        var mime = BaseMime(m =>
        {
            m.To.Add(MailboxAddress.Parse("ghost@example.com"));
            m.Cc.Add(MailboxAddress.Parse("cc@example.com"));
        });

        var dropped = GraphApiClient.FindNonEnvelopeRecipients(mime, ["rcpt@example.com", "cc@example.com"]);

        dropped.Should().BeEquivalentTo(["ghost@example.com"]);
    }

    // =========================================================================
    // Optional-property rejections — degrade instead of losing the mail
    // =========================================================================

    [Theory]
    [InlineData("InvalidInternetMessageHeaderCollection")]
    [InlineData("ErrorSendAsDenied")]
    [InlineData("ErrorInvalidSender")]
    public void IsOptionalPropertyRejection_RecoverableCodes_ReturnTrue(string code)
        => GraphApiClient.IsOptionalPropertyRejection(code).Should().BeTrue();

    [Theory]
    [InlineData("ErrorInvalidRecipients")]
    [InlineData("MailboxNotEnabledForRESTAPI")]
    [InlineData(null)]
    public void IsOptionalPropertyRejection_OtherCodes_ReturnFalse(string? code)
        => GraphApiClient.IsOptionalPropertyRejection(code).Should().BeFalse();

    [Fact]
    public void IsPermanentRejection_TooManyCustomHeaders_IsNotPermanent()
    {
        // Graph returns this as HTTP 400, which the blanket rule would classify permanent —
        // a mail must never be NDR'd because it carried one X-Spam-Level header too many.
        GraphApiClient.IsPermanentRejection(400, "InvalidInternetMessageHeaderCollection")
            .Should().BeFalse();
    }

    // =========================================================================
    // Size estimation
    // =========================================================================

    [Fact]
    public void Base64Length_MatchesFrameworkEncoding()
    {
        foreach (var size in new[] { 0, 1, 2, 3, 4, 100, 1023 })
            GraphApiClient.Base64Length(size)
                .Should().Be(Convert.ToBase64String(new byte[size]).Length, "size {0}", size);
    }

    [Fact]
    public void EstimateBodyBytes_MultiByteCharacters_CountsUtf8Bytes()
    {
        var mime = BaseMime(m => m.Body = new TextPart("plain") { Text = "Grüße 😀" });

        var estimate = GraphApiClient.EstimateBodyBytes(MimeMessageSplitter.Split(mime));

        estimate.Should().Be(System.Text.Encoding.UTF8.GetByteCount("Grüße 😀"))
            .And.BeGreaterThan("Grüße 😀".Length, "the old estimate compared UTF-16 chars against a byte budget");
    }

    // =========================================================================
    // CollectAttachments — inline CID parts
    // =========================================================================

    [Fact]
    public void CollectAttachments_InlineCidImage_KeepsContentIdAndInlineFlag()
    {
        var mime = BaseMime();
        var image = new MimePart("image", "png")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(new byte[128])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentId = "logo@example.com",
            FileName = "logo.png",
        };
        var html = new TextPart("html") { Text = """<img src="cid:logo@example.com">""" };
        mime.Body = new Multipart("related") { html, image };

        var (small, large) = GraphApiClient.CollectAttachments(mime);

        large.Should().BeEmpty();
        var att = small.Should().ContainSingle().Subject;
        att.ContentId.Should().Be("logo@example.com",
            "without the ContentId the cid: reference in the HTML body breaks");
        att.IsInline.Should().BeTrue("inline images must not show up as visible attachments");
    }

    [Fact]
    public void CollectAttachments_CidImageWithoutDisposition_IsInlineAttachment()
    {
        // Some mailers omit Content-Disposition on cid-referenced resources entirely.
        // The old filter (attachment or inline disposition required) silently dropped them.
        var mime = BaseMime();
        var image = new MimePart("image", "png")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(new byte[64])),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentId = "chart@example.com",
        };
        var html = new TextPart("html") { Text = """<img src="cid:chart@example.com">""" };
        mime.Body = new Multipart("related") { html, image };

        var (small, large) = GraphApiClient.CollectAttachments(mime);

        large.Should().BeEmpty();
        var att = small.Should().ContainSingle().Subject;
        att.ContentId.Should().Be("chart@example.com");
        att.IsInline.Should().BeTrue("a cid-referenced part without disposition is an embedded resource");
    }

    // =========================================================================
    // CollectAttachments — no silent drops (incident 2026-07-22)
    // =========================================================================

    [Fact]
    public void CollectAttachments_MalformedDispositionToken_IsAttached()
    {
        // Regression for the 2026-07-22 incident: SecureBlackbox 16 writes the file
        // name where RFC 2183 expects the disposition TYPE. §2.8 requires treating
        // any unrecognized type as "attachment" — the part must never be dropped.
        const string eml = """
            From: sender@example.com
            To: rcpt@example.com
            Subject: Anhang Test
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="B"; charset=UTF-8

            --B
            Content-Type: text/html; charset=UTF-8

            <html><body>Hi</body></html>
            --B
            Content-Type: application/octet-stream; name="scan.png"; charset=UTF-8
            Content-Disposition: scan.png; filename="scan.png"
            Content-Transfer-Encoding: base64

            iVBORw0KGgo=
            --B--
            """;
        var mime = MimeMessage.Load(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(eml)));

        var (small, large) = GraphApiClient.CollectAttachments(mime);

        large.Should().BeEmpty();
        var att = small.Should().ContainSingle().Subject;
        att.Name.Should().Be("scan.png");
        att.IsInline.Should().BeFalse();
        att.ContentBytes.Should().NotBeEmpty();

        // …and the body must stay the body: exactly one place per part.
        var msg = GraphApiClient.BuildMessage(mime, small, ["rcpt@example.com"]);
        msg.Body!.ContentType.Should().Be(BodyType.Html);
        msg.Body.Content.Should().Contain("Hi");
    }

    [Fact]
    public void CollectAttachments_TextPlainWithAttachmentDisposition_IsAttached()
    {
        // The old filter excluded text/plain and text/html by MIME type, dropping
        // genuine .txt/.html file attachments regardless of their disposition.
        var mime = BaseMime();
        var notes = new TextPart("plain")
        {
            Text = "attached notes",
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
        };
        notes.ContentDisposition.FileName = "notes.txt";
        mime.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Body" }, notes };

        var (small, large) = GraphApiClient.CollectAttachments(mime);

        large.Should().BeEmpty();
        var att = small.Should().ContainSingle().Subject;
        att.Name.Should().Be("notes.txt");
        att.ContentType.Should().Be("text/plain");
    }

    [Fact]
    public void CollectAttachments_NamedTextPartWithoutDisposition_IsAttached()
    {
        // A text part announcing a file name (Content-Type name parameter) is a file,
        // not a body candidate — even when the mailer sent no Content-Disposition.
        var mime = BaseMime();
        var csv = new TextPart("plain") { Text = "a;b;c" };
        csv.ContentType.Name = "data.csv";
        var html = new TextPart("html") { Text = "<p>Body</p>" };
        mime.Body = new Multipart("mixed") { html, csv };

        var (small, large) = GraphApiClient.CollectAttachments(mime);

        large.Should().BeEmpty();
        small.Should().ContainSingle().Which.Name.Should().Be("data.csv");

        var msg = GraphApiClient.BuildMessage(mime, small, ["rcpt@example.com"]);
        msg.Body!.Content.Should().Contain("Body").And.NotContain("a;b;c",
            "a part must end up in exactly one place — attachment, not also body");
    }

    [Fact]
    public void CollectAttachments_AttachedMessage_IsForwardedAsEmlFile()
    {
        // message/rfc822 parts are not MimeParts — the old OfType<MimePart>() filter
        // dropped the attached mail and hoisted its inner parts instead.
        var inner = BaseMime(m =>
        {
            m.Subject = "Inner mail";
            var builder = new BodyBuilder { TextBody = "inner body" };
            builder.Attachments.Add("inner.pdf", new byte[256]);
            m.Body = builder.ToMessageBody();
        });
        var mime = BaseMime();
        mime.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "see attached mail" },
            new MessagePart("rfc822") { Message = inner },
        };

        var (small, large) = GraphApiClient.CollectAttachments(mime);

        large.Should().BeEmpty();
        var att = small.Should().ContainSingle(
            "the attached mail is one unit; its inner parts must not be hoisted").Subject;
        att.Name.Should().EndWith(".eml");
        att.ContentType.Should().Be("message/rfc822");
        var roundtrip = MimeMessage.Load(new MemoryStream(att.ContentBytes!));
        roundtrip.Subject.Should().Be("Inner mail", "the forwarded .eml must be the byte-exact inner message");
    }

    [Fact]
    public void CollectAttachments_AlternativeBodies_AreNotAttached()
    {
        var mime = BaseMime();
        mime.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = "plain body" },
            new TextPart("html") { Text = "<p>html body</p>" },
        };

        var (small, large) = GraphApiClient.CollectAttachments(mime);

        small.Should().BeEmpty("alternative renderings of the body are not attachments");
        large.Should().BeEmpty();

        var msg = GraphApiClient.BuildMessage(mime, small, ["rcpt@example.com"]);
        msg.Body!.ContentType.Should().Be(BodyType.Html, "the richest alternative wins, as in every mail client");
        msg.Body.Content.Should().Contain("html body");
    }

    [Fact]
    public void CollectAttachments_SecondTextPartInMixed_IsAttachedNotDropped()
    {
        // Outside multipart/alternative a second text part is content of its own
        // (digest-style mail) — it must survive as an attachment.
        var mime = BaseMime();
        mime.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "first" },
            new TextPart("plain") { Text = "second" },
        };

        var (small, large) = GraphApiClient.CollectAttachments(mime);

        large.Should().BeEmpty();
        var att = small.Should().ContainSingle().Subject;
        System.Text.Encoding.UTF8.GetString(att.ContentBytes!).Should().Contain("second");
    }

    // =========================================================================
    // CollectAttachments — file names for parts that announce none
    // =========================================================================

    [Fact]
    public void CollectAttachments_UnnamedCalendarPart_GetsIcsFileName()
    {
        // A meeting invitation is a text/calendar alternative without a file name. Named
        // "attachment" it reaches the recipient as a nameless blob instead of an invite.
        var mime = BaseMime();
        mime.Body = new Multipart("alternative")
        {
            new TextPart("plain") { Text = "text" },
            new TextPart("calendar") { Text = "BEGIN:VCALENDAR\nEND:VCALENDAR" },
        };

        var (small, _) = GraphApiClient.CollectAttachments(mime);

        small.Should().ContainSingle()
            .Which.Name.Should().EndWith(".ics");
    }

    [Fact]
    public void CollectAttachments_MultipleUnnamedParts_GetDistinctNames()
    {
        var mime = BaseMime();
        mime.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "body" },
            new MimePart("application", "octet-stream")
            {
                Content = new MimeKit.MimeContent(new MemoryStream(new byte[16])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
            new MimePart("application", "octet-stream")
            {
                Content = new MimeKit.MimeContent(new MemoryStream(new byte[16])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            },
        };

        var (small, _) = GraphApiClient.CollectAttachments(mime);

        small.Should().HaveCount(2);
        small.Select(a => a.Name).Should().OnlyHaveUniqueItems(
            "colliding names make two distinct attachments look like one");
    }

    [Fact]
    public void FallbackFileName_KnownMediaType_GetsMatchingExtension()
    {
        GraphApiClient.FallbackFileName("text/calendar", 1).Should().Be("invite-1.ics");
        GraphApiClient.FallbackFileName("image/png", 2).Should().Be("attachment-2.png");
    }
}
