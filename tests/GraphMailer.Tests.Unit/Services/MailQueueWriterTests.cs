using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

public sealed class MailQueueWriterTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "mailqueue-tests-" + Guid.NewGuid().ToString("N"));

    public MailQueueWriterTests()
    {
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    private MailQueueWriter CreateSut()
    {
        var monitor = Substitute.For<IOptionsMonitor<MailQueueOptions>>();
        monitor.CurrentValue.Returns(new MailQueueOptions { MailDir = _workDir });
        return new MailQueueWriter(monitor, NullLogger<MailQueueWriter>.Instance);
    }

    [Fact]
    public async Task WriteAsync_CreatesEmlAndMetaFiles()
    {
        var sut = CreateSut();
        var body = "Subject: Test\r\n\r\nHello"u8.ToArray();

        await sut.WriteAsync("sender@example.com", ["rcpt@example.com"], "127.0.0.1", body);

        var queuePath = Path.Combine(_workDir, "queue");
        var emlFiles = Directory.GetFiles(queuePath, "*.eml");
        var metaFiles = Directory.GetFiles(queuePath, "*.meta.json");

        emlFiles.Should().HaveCount(1);
        metaFiles.Should().HaveCount(1);
    }

    [Fact]
    public async Task WriteAsync_EmlContainsCorrectBytes()
    {
        var sut = CreateSut();
        var body = "Subject: Hello\r\n\r\nBody text"u8.ToArray();

        await sut.WriteAsync("a@b.com", ["x@y.com"], "10.0.0.1", body);

        var queuePath = Path.Combine(_workDir, "queue");
        var eml = File.ReadAllBytes(Directory.GetFiles(queuePath, "*.eml")[0]);
        eml.Should().Equal(body);
    }

    [Fact]
    public async Task WriteAsync_MetaContainsCorrectFields()
    {
        var sut = CreateSut();

        await sut.WriteAsync("from@test.com", ["to1@test.com", "to2@test.com"], "1.2.3.4", "data"u8.ToArray());

        var queuePath = Path.Combine(_workDir, "queue");
        var metaJson = File.ReadAllText(Directory.GetFiles(queuePath, "*.meta.json")[0]);

        metaJson.Should().Contain("from@test.com");
        metaJson.Should().Contain("to1@test.com");
        metaJson.Should().Contain("to2@test.com");
        metaJson.Should().Contain("1.2.3.4");
        metaJson.Should().Contain("\"Status\": \"queued\"");
    }

    [Fact]
    public async Task WriteAsync_MultipleMessages_EachGetUniqueId()
    {
        var sut = CreateSut();
        var body = "test"u8.ToArray();

        await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", body);
        await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", body);

        var queuePath = Path.Combine(_workDir, "queue");
        Directory.GetFiles(queuePath, "*.eml").Should().HaveCount(2);
        Directory.GetFiles(queuePath, "*.meta.json").Should().HaveCount(2);
    }

    [Fact]
    public async Task WriteAsync_NoTempFilesLeftOnSuccess()
    {
        var sut = CreateSut();

        await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", "body"u8.ToArray());

        var queuePath = Path.Combine(_workDir, "queue");
        Directory.GetFiles(queuePath, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_ReturnsMailMetadataWithCorrectFields()
    {
        var sut = CreateSut();
        var body = "Subject: Test\r\nMessage-ID: <abc@example.com>\r\n\r\nHello"u8.ToArray();

        var meta = await sut.WriteAsync("from@example.com", ["to@example.com"], "10.0.0.1", body);

        meta.From.Should().Be("from@example.com");
        meta.To.Should().ContainSingle().Which.Should().Be("to@example.com");
        meta.ClientIp.Should().Be("10.0.0.1");
        meta.Status.Should().Be("queued");
        meta.MessageId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task WriteAsync_ExtractsSubjectFromEmlHeaders()
    {
        var sut = CreateSut();
        var body = "Subject: Invoice #42\r\nFrom: sender@x.com\r\n\r\nBody"u8.ToArray();

        var meta = await sut.WriteAsync("sender@x.com", ["rcpt@x.com"], "127.0.0.1", body);

        meta.Subject.Should().Be("Invoice #42");
    }

    [Fact]
    public async Task WriteAsync_ExtractsSmtpMessageIdFromEmlHeaders()
    {
        var sut = CreateSut();
        var body = "Message-ID: <unique-id-123@mail.example.com>\r\nSubject: Hi\r\n\r\nBody"u8.ToArray();

        var meta = await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", body);

        meta.SmtpMessageId.Should().Be("unique-id-123@mail.example.com");
    }

    [Fact]
    public async Task WriteAsync_HandlesHeaderFolding()
    {
        var sut = CreateSut();
        var body = "Subject: Long subject\r\n that continues here\r\n\r\nBody"u8.ToArray();

        var meta = await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", body);

        meta.Subject.Should().Be("Long subject that continues here");
    }

    [Fact]
    public async Task WriteAsync_DecodesEncodedWordSubject()
    {
        // Regression: RFC 2047 encoded words were stored raw — UTF-8 subjects were
        // unreadable in logs, metrics and the ConfigTool Messages page.
        var sut = CreateSut();
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Grüße aus München"));
        var body = System.Text.Encoding.ASCII.GetBytes($"Subject: =?utf-8?B?{encoded}?=\r\n\r\nBody");

        var meta = await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", body);

        meta.Subject.Should().Be("Grüße aus München");
    }

    [Fact]
    public async Task WriteAsync_SubjectAfterLargeHeaderBlock_IsStillExtracted()
    {
        // Regression: the old extractor scanned only the first 8 KB — messages with
        // many Received/DKIM headers lost their Subject and Message-ID in the metadata.
        var sut = CreateSut();
        var received = string.Concat(Enumerable.Repeat(
            "Received: from relay.example.com by mx.example.com with ESMTP id abcdef1234567890abcdef; Thu, 10 Jul 2026 12:00:00 +0000\r\n", 100));
        var body = System.Text.Encoding.ASCII.GetBytes(received + "Subject: After the wall\r\n\r\nBody");

        var meta = await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", body);

        meta.Subject.Should().Be("After the wall");
    }

    [Fact]
    public async Task WriteAsync_SubjectLookalikeInBody_IsNotExtracted()
    {
        // The parser must stop at the blank line — a "Subject:" in the body is content.
        var sut = CreateSut();
        var body = "From: a@b.com\r\n\r\nSubject: not a header\r\n"u8.ToArray();

        var meta = await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", body);

        meta.Subject.Should().BeEmpty();
    }

    // =========================================================================
    // Reception statistics (attachments, To/Cc/Bcc) — metrics.db schema v2
    // =========================================================================

    private static byte[] BuildMimeMessage(
        string[] to, string[] cc, params (string Name, byte[] Content)[] attachments)
    {
        var message = new MimeKit.MimeMessage();
        message.From.Add(MimeKit.MailboxAddress.Parse("sender@example.com"));
        foreach (var addr in to) message.To.Add(MimeKit.MailboxAddress.Parse(addr));
        foreach (var addr in cc) message.Cc.Add(MimeKit.MailboxAddress.Parse(addr));
        message.Subject = "Attachment test";

        var builder = new MimeKit.BodyBuilder { TextBody = "Body" };
        foreach (var (name, content) in attachments)
            builder.Attachments.Add(name, content);
        message.Body = builder.ToMessageBody();

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task WriteAsync_CountsAttachmentsAndBytes()
    {
        var sut = CreateSut();
        var body = BuildMimeMessage(
            to: ["rcpt@example.com"], cc: [],
            ("report.pdf", new byte[2048]), ("data.csv", new byte[512]));

        var meta = await sut.WriteAsync("sender@example.com", ["rcpt@example.com"], "127.0.0.1", body);

        meta.AttachmentCount.Should().Be(2);
        meta.AttachmentBytes.Should().BeGreaterThan(0, "raw encoded attachment size is recorded for statistics");
    }

    [Fact]
    public async Task WriteAsync_MalformedAttachmentDisposition_IsCounted()
    {
        // Regression for the 2026-07-22 incident: a Content-Disposition whose type is
        // the file name instead of "attachment" (SecureBlackbox 16). MimeKit's own
        // Attachments view does not count it — the shared splitter must.
        var sut = CreateSut();
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

        var meta = await sut.WriteAsync("sender@example.com", ["rcpt@example.com"], "127.0.0.1",
            System.Text.Encoding.ASCII.GetBytes(eml));

        meta.AttachmentCount.Should().Be(1,
            "the reception statistics must count what Graph delivery will actually send");
        meta.AttachmentBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WriteAsync_InlineCidImage_IsCountedAsAttachment()
    {
        // Inline images travel in the Graph attachment list (IsInline), so the reception
        // count includes them — keeping received == sent semantics.
        var sut = CreateSut();
        var message = new MimeKit.MimeMessage();
        message.From.Add(MimeKit.MailboxAddress.Parse("sender@example.com"));
        message.To.Add(MimeKit.MailboxAddress.Parse("rcpt@example.com"));
        message.Subject = "Inline image";
        var builder = new MimeKit.BodyBuilder();
        var logo = builder.LinkedResources.Add("logo.png", new byte[128],
            new MimeKit.ContentType("image", "png"));
        logo.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        builder.HtmlBody = $"""<img src="cid:{logo.ContentId}">""";
        message.Body = builder.ToMessageBody();
        using var stream = new MemoryStream();
        message.WriteTo(stream);

        var meta = await sut.WriteAsync("sender@example.com", ["rcpt@example.com"], "127.0.0.1",
            stream.ToArray());

        meta.AttachmentCount.Should().Be(1);
    }

    [Fact]
    public async Task WriteAsync_NoAttachments_CountsZero()
    {
        var sut = CreateSut();
        var body = BuildMimeMessage(to: ["rcpt@example.com"], cc: []);

        var meta = await sut.WriteAsync("sender@example.com", ["rcpt@example.com"], "127.0.0.1", body);

        meta.AttachmentCount.Should().Be(0);
        meta.AttachmentBytes.Should().Be(0);
    }

    [Fact]
    public async Task WriteAsync_DerivesCcAndBccFromHeadersAndEnvelope()
    {
        // BCC has no header by design: an envelope recipient that appears in neither
        // the To nor the Cc header was blind-copied.
        var sut = CreateSut();
        var body = BuildMimeMessage(to: ["visible@example.com"], cc: ["copy@example.com"]);

        var meta = await sut.WriteAsync(
            "sender@example.com",
            ["visible@example.com", "copy@example.com", "hidden@example.com"],
            "127.0.0.1", body);

        meta.CcCount.Should().Be(1);
        meta.BccCount.Should().Be(1, "hidden@example.com is in the envelope but in no header");
    }

    [Fact]
    public async Task WriteAsync_BccDerivation_IsCaseInsensitive()
    {
        var sut = CreateSut();
        var body = BuildMimeMessage(to: ["visible@example.com"], cc: []);

        var meta = await sut.WriteAsync(
            "sender@example.com", ["VISIBLE@EXAMPLE.COM"], "127.0.0.1", body);

        meta.BccCount.Should().Be(0, "address matching between envelope and headers ignores case");
    }

    [Fact]
    public async Task WriteAsync_UnparsableMime_DegradesToZeroCountsAndStillQueues()
    {
        // The statistics parse is metadata only — a broken message must still be queued.
        var sut = CreateSut();

        var meta = await sut.WriteAsync("a@b.com", ["c@d.com"], "127.0.0.1", Array.Empty<byte>());

        meta.AttachmentCount.Should().Be(0);
        meta.CcCount.Should().Be(0);
        meta.BccCount.Should().Be(0);
        Directory.GetFiles(Path.Combine(_workDir, "queue"), "*.eml").Should().HaveCount(1);
    }
}
