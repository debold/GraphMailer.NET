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
}
