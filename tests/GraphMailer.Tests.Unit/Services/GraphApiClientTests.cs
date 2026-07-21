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
}
