using System.Buffers;
using System.Net;
using System.Text;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Infrastructure.Security.Amsi;
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

    /// <summary>
    /// Scanner stub. Defaults to unavailable, which is what the machine looks like with no AMSI
    /// provider — so every pre-existing test keeps exercising the unscanned path unchanged.
    /// </summary>
    private sealed class FakeScanner(ScanResult result, bool available = true) : IMailContentScanner
    {
        public bool IsAvailable { get; } = available;
        public IReadOnlyList<AmsiProvider> Providers { get; } = [];
        public int ScanCount { get; private set; }

        public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> eml, string messageId, CancellationToken ct = default)
        {
            ScanCount++;
            return Task.FromResult(result);
        }
    }

    private static readonly IMailContentScanner NoScanner =
        new FakeScanner(ScanResult.Unavailable(), available: false);

    private BlockedMessageRecorder CreateRecorder(MalwareScanOptions? scanOptions = null)
        => new(Monitor(new MailQueueOptions { MailDir = _tempDir }),
               Monitor(scanOptions ?? new MalwareScanOptions()),
               NullLogger<BlockedMessageRecorder>.Instance);

    private string BlockedDir => Path.Combine(_tempDir, "blocked");
    private string QueueDir => Path.Combine(_tempDir, "queue");

    private SmtpMessageStore CreateStore(
        IMetricsService? metrics = null,
        ILogger<SmtpMessageStore>? logger = null,
        IMailContentScanner? scanner = null,
        MalwareScanOptions? scanOptions = null,
        IAdminNotificationService? notifications = null)
    {
        var queue = new MailQueueWriter(
            Monitor(new MailQueueOptions { MailDir = _tempDir }),
            NullLogger<MailQueueWriter>.Instance);
        var ipBlocking = new IpBlockingService(
            Monitor(new IpBlockingProtectionOptions()),
            NullLogger<IpBlockingService>.Instance);
        scanOptions ??= new MalwareScanOptions();

        return new SmtpMessageStore(
            queue,
            ipBlocking,
            Monitor(new SmtpAccessOptions()),
            metrics ?? Substitute.For<IMetricsService>(),
            scanner ?? NoScanner,
            Monitor(scanOptions),
            CreateRecorder(scanOptions),
            notifications ?? Substitute.For<IAdminNotificationService>(),
            logger ?? NullLogger<SmtpMessageStore>.Instance);
    }

    private static (ISessionContext Context, IMessageTransaction Transaction, ReadOnlySequence<byte> Buffer) CreateSaveArgs(
        string? remoteIp = null, string? authUser = null)
    {
        var context = Substitute.For<ISessionContext>();
        var properties = new Dictionary<string, object>();
        if (remoteIp is not null)
            properties[IpFilterService.RemoteEndPointKey] = new IPEndPoint(IPAddress.Parse(remoteIp), 12345);
        context.Properties.Returns(properties);

        // AuthenticationContext(string) is the library's own "authenticated as" value —
        // no substitute needed, and it keeps IsAuthenticated consistent with User.
        if (authUser is not null)
            context.Authentication.Returns(new AuthenticationContext(authUser));

        var transaction = Substitute.For<IMessageTransaction>();
        transaction.From.Returns(new Mailbox("sender", "example.com"));
        transaction.To.Returns(new List<IMailbox> { new Mailbox("rcpt", "example.com") });

        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("Subject: Test\r\n\r\nBody"));
        return (context, transaction, buffer);
    }

    private static ScanResult MalwareInAttachment(string hash = "abc123") =>
        new(ScanOutcome.Malware, "invoice.docm", hash, 4096, 32768);

    private static ScanResult MalwareInBody() =>
        new(ScanOutcome.Malware, "message body (html)", null, 512, 32768);

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
        metrics.RecordEmailReceivedAsync(Arg.Any<ReceivedEmailEvent>(), Arg.Any<CancellationToken>())
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
            Substitute.For<IMetricsService>(), NoScanner, Monitor(new MalwareScanOptions()),
            CreateRecorder(), Substitute.For<IAdminNotificationService>(),
            NullLogger<SmtpMessageStore>.Instance);
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

    // =========================================================================
    // Malware scan – mode matrix
    // =========================================================================

    [Fact]
    public async Task SaveAsync_MalwareDetected_EnforceMode_Returns554AndQueuesNothing()
    {
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        ((int)response.ReplyCode).Should().Be(554,
            "the content is the problem – retrying the same message would be pointless");
        Directory.GetFiles(QueueDir, "*").Should().BeEmpty("a rejected message is never queued");
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_EnforceMode_ResponseDoesNotLeakDetectionDetail()
    {
        // The file name and hash are operator information. Echoing them to the client would
        // tell a probing sender exactly what tripped the scanner and what did not.
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment("deadbeef")),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        response.Message.Should().NotContain("invoice.docm").And.NotContain("deadbeef");
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_AuditMode_Returns250AndQueuesMessage()
    {
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Audit });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        response.ReplyCode.Should().Be(SmtpReplyCode.Ok, "audit mode observes, it does not block");
        Directory.GetFiles(QueueDir, "*.eml").Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_AuditMode_RecordsDetectionAsNotBlocked()
    {
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Audit });
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        var record = Directory.GetFiles(BlockedDir, "*.meta.json").Should().ContainSingle().Subject;
        var json = await File.ReadAllTextAsync(record);
        json.Should().Contain("\"Blocked\": false").And.Contain("Audit");
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_AuditMode_RecordSharesTheQueuedMessageId()
    {
        // Correlation: the blocked record and the delivered message must carry the same id,
        // otherwise an operator cannot tie an audit finding to the mail that went out.
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Audit });
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        var recordId = Path.GetFileName(Directory.GetFiles(BlockedDir, "*.meta.json")[0]).Replace(".meta.json", "");
        var queuedId = Path.GetFileNameWithoutExtension(Directory.GetFiles(QueueDir, "*.eml")[0]);
        recordId.Should().Be(queuedId);
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_EnforceMode_RecordsRejectionMetric()
    {
        var metrics = Substitute.For<IMetricsService>();
        var sut = CreateStore(
            metrics: metrics,
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce });
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await metrics.Received(1).RecordRejectionAsync(
            RejectionReasons.MalwareDetected, Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_AuditMode_RecordsNoRejectionMetric()
    {
        // Audit detections must not inflate the rejection statistics – nothing was rejected.
        var metrics = Substitute.For<IMetricsService>();
        var sut = CreateStore(
            metrics: metrics,
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Audit });
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await metrics.DidNotReceive().RecordRejectionAsync(
            RejectionReasons.MalwareDetected, Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_EnforceMode_NotifiesTheAdmin()
    {
        var notifications = Substitute.For<IAdminNotificationService>();
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce },
            notifications: notifications);
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await notifications.Received(1).NotifyMalwareDetectedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            "invoice.docm", Arg.Any<string>(), blocked: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_AuditMode_NotifiesTheAdminAsNotBlocked()
    {
        // Audit findings must be reported too — that is the whole point of the observation
        // phase — but flagged as delivered so nobody reads them as blocked threats.
        var notifications = Substitute.For<IAdminNotificationService>();
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Audit },
            notifications: notifications);
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await notifications.Received(1).NotifyMalwareDetectedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), blocked: false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_AllowlistedHash_DoesNotNotify()
    {
        // A known false positive is not an event worth an email.
        var notifications = Substitute.For<IAdminNotificationService>();
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment("aabbcc")),
            scanOptions: new MalwareScanOptions
            {
                Mode = MalwareScanMode.Enforce,
                AllowedContentHashes = [new AllowedContentHash { Sha256 = "aabbcc" }],
            },
            notifications: notifications);
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await notifications.DidNotReceive().NotifyMalwareDetectedAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_EnforceMode_CountsTheDetectionAsBlocked()
    {
        var metrics = Substitute.For<IMetricsService>();
        var sut = CreateStore(
            metrics: metrics,
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce });
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await metrics.Received(1).RecordMalwareDetectionAsync(
            blocked: true, "attachment", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_MalwareDetected_AuditMode_CountsTheDetectionAsNotBlocked()
    {
        // The audit counter is what feeds the Status tile, the Metrics card and the report —
        // without it, an installation in audit mode would look like it had found nothing.
        var metrics = Substitute.For<IMetricsService>();
        var sut = CreateStore(
            metrics: metrics,
            scanner: new FakeScanner(MalwareInAttachment()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Audit });
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await metrics.Received(1).RecordMalwareDetectionAsync(
            blocked: false, "attachment", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_BodyDetection_IsCountedAsBody()
    {
        var metrics = Substitute.For<IMetricsService>();
        var sut = CreateStore(
            metrics: metrics,
            scanner: new FakeScanner(MalwareInBody()),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce });
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await metrics.Received(1).RecordMalwareDetectionAsync(
            Arg.Any<bool>(), "body", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_ModeOff_DoesNotScan()
    {
        var scanner = new FakeScanner(MalwareInAttachment());
        var sut = CreateStore(scanner: scanner, scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Off });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        scanner.ScanCount.Should().Be(0);
        response.ReplyCode.Should().Be(SmtpReplyCode.Ok);
    }

    [Fact]
    public async Task SaveAsync_ScannerUnavailable_DoesNotScanAndDelivers()
    {
        var scanner = new FakeScanner(MalwareInAttachment(), available: false);
        var sut = CreateStore(scanner: scanner, scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        scanner.ScanCount.Should().Be(0);
        response.ReplyCode.Should().Be(SmtpReplyCode.Ok, "a scanner that cannot run must not block mail");
    }

    // =========================================================================
    // Malware scan – fail-open
    // =========================================================================

    // The central fail-open contract: only a confirmed detection stops mail. A timeout, an
    // error or an oversized part must never turn a scanner problem into a mail outage.
    // ScanOutcome is internal, so these are separate Facts rather than one Theory.

    [Fact]
    public async Task SaveAsync_ScanFailed_DeliversEvenInEnforceMode()
        => await AssertFailOpen(ScanResult.Failed("boom"));

    [Fact]
    public async Task SaveAsync_ScanSkippedOversizedPart_DeliversEvenInEnforceMode()
        => await AssertFailOpen(ScanResult.Skipped("big.zip", 99_000_000));

    private async Task AssertFailOpen(ScanResult outcome)
    {
        var sut = CreateStore(
            scanner: new FakeScanner(outcome),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        response.ReplyCode.Should().Be(SmtpReplyCode.Ok);
        Directory.GetFiles(QueueDir, "*.eml").Should().HaveCount(1);
        Directory.GetFiles(BlockedDir, "*").Should().BeEmpty("nothing was detected, so there is nothing to record");
    }

    [Fact]
    public async Task SaveAsync_ScanFailed_NotifiesScanFailure()
        => await AssertScanFailureNotified(ScanResult.Failed("boom"));

    [Fact]
    public async Task SaveAsync_ScanSkippedOversizedPart_NotifiesScanFailure()
        => await AssertScanFailureNotified(ScanResult.Skipped("big.zip", 99_000_000));

    private async Task AssertScanFailureNotified(ScanResult outcome)
    {
        // Fail-open is invisible by design; this notification is the operator's only signal
        // that mail is flowing past a scanner that is not doing its job.
        var notifications = Substitute.For<IAdminNotificationService>();
        var sut = CreateStore(
            scanner: new FakeScanner(outcome),
            scanOptions: new MalwareScanOptions { Mode = MalwareScanMode.Enforce },
            notifications: notifications);
        var (context, transaction, buffer) = CreateSaveArgs();

        await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        await notifications.Received(1).NotifyMalwareScanFailureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // Malware scan – hash allowlist
    // =========================================================================

    [Fact]
    public async Task SaveAsync_AllowlistedAttachmentHash_DeliversDespiteDetection()
    {
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment("AABBCC")),
            scanOptions: new MalwareScanOptions
            {
                Mode = MalwareScanMode.Enforce,
                AllowedContentHashes = [new AllowedContentHash { Sha256 = "aabbcc", Note = "known false positive" }],
            });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        response.ReplyCode.Should().Be(SmtpReplyCode.Ok, "hash comparison is case-insensitive");
        Directory.GetFiles(QueueDir, "*.eml").Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveAsync_DifferentAttachmentHash_StillBlocked()
    {
        // The allowlist exempts one exact byte sequence, not a file name or a sender.
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInAttachment("0123456789")),
            scanOptions: new MalwareScanOptions
            {
                Mode = MalwareScanMode.Enforce,
                AllowedContentHashes = [new AllowedContentHash { Sha256 = "aabbcc" }],
            });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        ((int)response.ReplyCode).Should().Be(554);
    }

    [Fact]
    public async Task SaveAsync_BodyDetection_CannotBeAllowlisted()
    {
        // A body differs on every message, so it carries no hash and no entry can match it.
        var sut = CreateStore(
            scanner: new FakeScanner(MalwareInBody()),
            scanOptions: new MalwareScanOptions
            {
                Mode = MalwareScanMode.Enforce,
                AllowedContentHashes = [new AllowedContentHash { Sha256 = "aabbcc" }],
            });
        var (context, transaction, buffer) = CreateSaveArgs();

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        ((int)response.ReplyCode).Should().Be(554);
    }

    // =========================================================================
    // Malware scan – source bypass
    // =========================================================================

    [Fact]
    public async Task SaveAsync_BypassedAuthenticatedUser_DoesNotScan()
    {
        var scanner = new FakeScanner(MalwareInAttachment());
        var sut = CreateStore(
            scanner: scanner,
            scanOptions: new MalwareScanOptions
            {
                Mode = MalwareScanMode.Enforce,
                BypassAuthenticatedUsers = ["LegacyApp"],
            });
        var (context, transaction, buffer) = CreateSaveArgs(authUser: "legacyapp");

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        scanner.ScanCount.Should().Be(0, "the user is exempt, so nothing is scanned at all");
        response.ReplyCode.Should().Be(SmtpReplyCode.Ok);
    }

    [Fact]
    public async Task SaveAsync_BypassedIpRange_DoesNotScan()
    {
        var scanner = new FakeScanner(MalwareInAttachment());
        var sut = CreateStore(
            scanner: scanner,
            scanOptions: new MalwareScanOptions
            {
                Mode = MalwareScanMode.Enforce,
                BypassIpAddresses = ["10.1.0.0/16"],
            });
        var (context, transaction, buffer) = CreateSaveArgs(remoteIp: "10.1.2.3");

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        scanner.ScanCount.Should().Be(0);
        response.ReplyCode.Should().Be(SmtpReplyCode.Ok);
    }

    [Fact]
    public async Task SaveAsync_UnlistedIp_IsStillScanned()
    {
        var scanner = new FakeScanner(MalwareInAttachment());
        var sut = CreateStore(
            scanner: scanner,
            scanOptions: new MalwareScanOptions
            {
                Mode = MalwareScanMode.Enforce,
                BypassIpAddresses = ["10.1.0.0/16"],
            });
        var (context, transaction, buffer) = CreateSaveArgs(remoteIp: "192.168.5.5");

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        scanner.ScanCount.Should().Be(1);
        ((int)response.ReplyCode).Should().Be(554);
    }

    [Fact]
    public async Task SaveAsync_EnvelopeSenderIsNeverABypass()
    {
        // Regression guard for the obvious wrong extension of this feature: MAIL FROM is chosen
        // freely by the client, so an address-based exemption would let anyone opt out of
        // scanning. Only the authenticated user and the client IP may exempt a message.
        var scanner = new FakeScanner(MalwareInAttachment());
        var sut = CreateStore(
            scanner: scanner,
            scanOptions: new MalwareScanOptions
            {
                Mode = MalwareScanMode.Enforce,
                BypassAuthenticatedUsers = ["sender@example.com"],   // matches MAIL FROM, not a user
            });
        var (context, transaction, buffer) = CreateSaveArgs();       // unauthenticated session

        var response = await sut.SaveAsync(context, transaction, buffer, CancellationToken.None);

        scanner.ScanCount.Should().Be(1, "an unauthenticated session can never match a user bypass");
        ((int)response.ReplyCode).Should().Be(554);
    }
}
