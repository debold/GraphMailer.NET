using System.Buffers;
using System.Text;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Infrastructure.Smtp;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;

namespace GraphMailer.Tests.Unit.Infrastructure.Smtp;

/// <summary>
/// SMTP DATA response contract of <see cref="SmtpMessageStore"/>:
/// once the message is durably queued the client must get 250 — a telemetry failure
/// must not turn into an error reply (the client would re-send the already-queued
/// message → duplicate delivery). A failed local queue write must be answered with a
/// transient 451 — a permanent 554 would make conforming clients discard the mail
/// (silent mail loss on a temporary disk/IO condition).
/// </summary>
public sealed class SmtpMessageStoreTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "smtpmessagestore-tests-" + Guid.NewGuid().ToString("N"));

    public SmtpMessageStoreTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private SmtpMessageStore CreateStore(IMetricsService? metrics = null, ILogger<SmtpMessageStore>? logger = null)
    {
        var queue = new MailQueueWriter(
            Monitor(new MailQueueOptions { MailDir = _tempDir }),
            NullLogger<MailQueueWriter>.Instance);
        var ipBlocking = new IpBlockingService(
            Monitor(new IpBlockingProtectionOptions()),
            NullLogger<IpBlockingService>.Instance);

        return new SmtpMessageStore(
            queue,
            ipBlocking,
            Monitor(new SmtpAccessOptions()),
            metrics ?? Substitute.For<IMetricsService>(),
            logger ?? NullLogger<SmtpMessageStore>.Instance);
    }

    private static (ISessionContext Context, IMessageTransaction Transaction, ReadOnlySequence<byte> Buffer) CreateSaveArgs()
    {
        var context = Substitute.For<ISessionContext>();
        context.Properties.Returns(new Dictionary<string, object>());

        var transaction = Substitute.For<IMessageTransaction>();
        transaction.From.Returns(new Mailbox("sender", "example.com"));
        transaction.To.Returns(new List<IMailbox> { new Mailbox("rcpt", "example.com") });

        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("Subject: Test\r\n\r\nBody"));
        return (context, transaction, buffer);
    }

    // =========================================================================
    // DATA response contract
    // =========================================================================

    [Fact]
    public async Task SaveAsync_QueueWriteSucceeds_Returns250AndQueuesPair()
    {
        var sut = CreateStore();
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        response.ReplyCode.Should().Be(SmtpReplyCode.Ok);
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.GetFiles(queueDir, "*.eml").Should().HaveCount(1);
        Directory.GetFiles(queueDir, "*.meta.json").Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveAsync_MetricsThrowAfterQueueWrite_StillReturns250()
    {
        // Regression: the metrics write runs after the message is durably queued.
        // A metrics failure must not produce an error reply — the client would
        // re-send the already-queued message and the recipient would get it twice.
        var metrics = Substitute.For<IMetricsService>();
        metrics.RecordEmailReceivedAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("metrics db locked"));
        var sut = CreateStore(metrics);
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        response.ReplyCode.Should().Be(SmtpReplyCode.Ok,
            "the message is durably queued — telemetry failures must not make the client re-send it");
        Directory.GetFiles(Path.Combine(_tempDir, "queue"), "*.eml").Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveAsync_BlockedIp_ReturnsPermanent550AndQueuesNothing()
    {
        // Documents the audit verification: a deliberately blocked IP gets the PERMANENT
        // 550 (SmtpResponse.MailboxUnavailable), not a transient 4xx that would invite
        // the abusive client to retry.
        using var ipBlocking = new IpBlockingService(
            Monitor(new IpBlockingProtectionOptions
            {
                Enabled = true, FailureThreshold = 1, TimeframeSeconds = 600, BlockDurationSeconds = 600
            }),
            NullLogger<IpBlockingService>.Instance);
        ipBlocking.RecordFailure("unknown", "authFailure");   // substituted contexts resolve to remote IP "unknown"

        var queue = new MailQueueWriter(
            Monitor(new MailQueueOptions { MailDir = _tempDir }),
            NullLogger<MailQueueWriter>.Instance);
        var sut = new SmtpMessageStore(
            queue, ipBlocking, Monitor(new SmtpAccessOptions()),
            Substitute.For<IMetricsService>(), NullLogger<SmtpMessageStore>.Instance);
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        ((int)response.ReplyCode).Should().Be(550);
        Directory.GetFiles(Path.Combine(_tempDir, "queue"), "*").Should().BeEmpty(
            "nothing is queued for a blocked IP");
    }

    [Fact]
    public async Task SaveAsync_QueueWriteFails_ReturnsTransient451()
    {
        // Regression: a failed local queue write (disk full, IO error) must be answered
        // with a transient 4xx so the client keeps the message and retries. The old
        // permanent 554 made conforming clients discard the mail — silent mail loss.
        var sut = CreateStore();
        // Sabotage the queue directory: replace it with a file so the write throws.
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.Delete(queueDir, recursive: true);
        await File.WriteAllTextAsync(queueDir, "blocks the directory");
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        ((int)response.ReplyCode).Should().Be(451,
            "a failed local queue write is transient — 554 would make the client discard the mail");
    }

    [Fact]
    public async Task SaveAsync_QueueWriteFails_LogsErrorWithException()
    {
        // The Error log is the operator's only notification for a failed queue write
        // (the client just sees a generic 451). It must carry the exception object —
        // that is what makes Serilog write the stack trace to the log file.
        var logger = new FakeLogger<SmtpMessageStore>();
        var sut = CreateStore(logger: logger);
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.Delete(queueDir, recursive: true);
        await File.WriteAllTextAsync(queueDir, "blocks the directory");
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        var entry = logger.EntriesAt(LogLevel.Error).Should().ContainSingle().Subject;
        entry.Message.Should().Contain("Failed to queue");
        entry.Exception.Should().NotBeNull("the attached exception is what carries the stack trace into the log");
    }
}
