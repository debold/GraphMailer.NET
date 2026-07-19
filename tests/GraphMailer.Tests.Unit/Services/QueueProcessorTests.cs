using System.Text.Json;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GraphMailer.Tests.Unit.Services;

public sealed class QueueProcessorTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "queueprocessor-tests-" + Guid.NewGuid().ToString("N"));

    public QueueProcessorTests()
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

    private static readonly GraphDeliveryResult SendOk =
        new(GraphDeliveryResult.VariantSendMail, AttachmentCount: 0, AttachmentBytes: 0);

    /// <summary>IGraphApiClient substitute whose SendAsync succeeds with a real delivery result.</summary>
    private static IGraphApiClient SucceedingClient()
    {
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SendOk);
        return client;
    }

    private QueueProcessor CreateProcessor(
        MailQueueOptions? queueOpts = null,
        GraphApiOptions? graphOpts = null,
        IGraphApiClient? graphClient = null,
        IMetricsService? metrics = null,
        IAdminNotificationService? notify = null)
    {
        var opts = queueOpts ?? new MailQueueOptions { MailDir = _tempDir };
        // Ensure MailDir is set when caller provides opts without it
        if (string.IsNullOrEmpty(opts.MailDir))
            opts = new MailQueueOptions
            {
                MailDir = _tempDir,
                PollingIntervalSeconds = opts.PollingIntervalSeconds,
                TransientRetryCount = opts.TransientRetryCount,
                TransientRetryIntervalSeconds = opts.TransientRetryIntervalSeconds,
                RetryIntervalSeconds = opts.RetryIntervalSeconds,
                MessageExpirationHours = opts.MessageExpirationHours,
                BatchSize = opts.BatchSize,
                ArchiveSentEmails = opts.ArchiveSentEmails,
                SentEmailRetentionDays = opts.SentEmailRetentionDays,
                FailedEmailRetentionDays = opts.FailedEmailRetentionDays
            };
        var gOpts = graphOpts ?? new GraphApiOptions
        {
            TenantId = "tenant-id",
            ClientId = "client-id",
            ClientSecret = "s3cr3t"
        };

        return new QueueProcessor(
            Monitor(opts),
            Monitor(gOpts),
            graphClient ?? SucceedingClient(),
            Substitute.For<ITenantSenderDirectory>(),
            metrics ?? Substitute.For<IMetricsService>(),
            notify ?? Substitute.For<IAdminNotificationService>(),
            NullLogger<QueueProcessor>.Instance);
    }

    /// <summary>Writes a meta.json and an .eml file pair into queue/.</summary>
    private async Task<string> EnqueueMessageAsync(
        string messageId,
        int retryCount = 0,
        DateTime? nextRetryAt = null,
        DateTime? receivedAt = null,
        DateTime? sentAt = null,
        bool isNotification = false)
    {
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.CreateDirectory(queueDir);

        var meta = new MailMetadata
        {
            MessageId = messageId,
            From = "sender@example.com",
            To = ["rcpt@example.com"],
            ReceivedAt = receivedAt ?? DateTime.UtcNow,
            ClientIp = "127.0.0.1",
            RetryCount = retryCount,
            Status = "queued",
            NextRetryAt = nextRetryAt,
            SentAt = sentAt,
            IsNotification = isNotification
        };

        var metaPath = Path.Combine(queueDir, $"{messageId}.meta.json");
        var emlPath = Path.Combine(queueDir, $"{messageId}.eml");

        var json = JsonSerializer.Serialize(meta, MailMetadataJsonContext.Default.MailMetadata);
        await File.WriteAllTextAsync(metaPath, json);
        await File.WriteAllTextAsync(emlPath, "Subject: Test\r\n\r\nHello");

        return metaPath;
    }

    // =========================================================================
    // Graph API not configured
    // =========================================================================

    [Fact]
    public async Task ProcessBatch_GraphApiNotConfigured_DoesNotProcessFiles()
    {
        // Arrange – an unconfigured GraphApiOptions (empty credentials)
        var sut = CreateProcessor(graphOpts: new GraphApiOptions());
        await EnqueueMessageAsync("msg-not-configured");

        // Act
        await sut.ProcessBatchAsync();

        // Assert – both files still in queue
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.GetFiles(queueDir, "*.meta.json").Should().HaveCount(1);
        Directory.GetFiles(queueDir, "*.eml").Should().HaveCount(1);
    }

    // =========================================================================
    // Successful delivery
    // =========================================================================

    [Fact]
    public async Task ProcessBatch_EmptyQueue_DoesNotCallGraphClient()
    {
        var client = Substitute.For<IGraphApiClient>();
        var sut = CreateProcessor(graphClient: client);

        Directory.CreateDirectory(Path.Combine(_tempDir, "queue"));

        await sut.ProcessBatchAsync();

        await client.DidNotReceive().SendAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessMessage_DeliverySucceeds_DeletesBothFiles()
    {
        var client = Substitute.For<IGraphApiClient>();
        var sut = CreateProcessor(graphClient: client);
        await EnqueueMessageAsync("msg-delete");

        await sut.ProcessBatchAsync();

        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.GetFiles(queueDir, "*").Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessBatch_BackoffMessagesDoNotConsumeBatchBudget_EligibleMessageIsStillDelivered()
    {
        // Regression: a prefix of BatchSize messages still in their back-off window must not
        // block later, ready messages. Enumeration is by GUID filename, so the back-off
        // entries ("aaa…") sort before the eligible one ("zzz…"); the old Take(BatchSize)
        // logic only ever looked at the back-off prefix and never delivered the ready mail.
        var client = Substitute.For<IGraphApiClient>();
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions { MailDir = _tempDir, BatchSize = 10 },
            graphClient: client);

        for (int i = 0; i < 10; i++)
            await EnqueueMessageAsync($"aaa-backoff-{i:00}", retryCount: 1, nextRetryAt: DateTime.UtcNow.AddMinutes(5));
        await EnqueueMessageAsync("zzz-ready");

        await sut.ProcessBatchAsync();

        // The ready message was delivered (both files removed) …
        var queueDir = Path.Combine(_tempDir, "queue");
        File.Exists(Path.Combine(queueDir, "zzz-ready.meta.json")).Should().BeFalse();
        await client.Received(1).SendAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), "zzz-ready", Arg.Any<CancellationToken>());
        // … while the back-off messages were skipped, not attempted.
        await client.DidNotReceive().SendAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Is<string>(id => id.StartsWith("aaa-backoff")), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // Delivery commit / duplicate-send protection
    // =========================================================================

    [Fact]
    public async Task ProcessMessage_MetaAlreadyMarkedSent_CompletesCleanupWithoutResending()
    {
        // Regression: SentAt set means a previous run delivered the message but was
        // interrupted before removing the queue files. A restart must finish the
        // cleanup, never send again (duplicate delivery at the recipient).
        var client = Substitute.For<IGraphApiClient>();
        var sut = CreateProcessor(graphClient: client);
        await EnqueueMessageAsync("msg-already-sent", sentAt: DateTime.UtcNow.AddMinutes(-5));

        await sut.ProcessBatchAsync();

        await client.DidNotReceive().SendAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Directory.GetFiles(Path.Combine(_tempDir, "queue"), "*").Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessMessage_MetaMarkedSentAndEmlAlreadyGone_ResumedArchiveCommitCompletes()
    {
        // Crash window in archive mode: eml already moved to sent/, queue meta still
        // present. The resumed commit must tolerate the missing eml and finish.
        var client = Substitute.For<IGraphApiClient>();
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions { ArchiveSentEmails = true },
            graphClient: client);
        await EnqueueMessageAsync("msg-resume", sentAt: DateTime.UtcNow.AddMinutes(-5));
        File.Delete(Path.Combine(_tempDir, "queue", "msg-resume.eml"));

        await sut.ProcessBatchAsync();

        await client.DidNotReceive().SendAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        File.Exists(Path.Combine(_tempDir, "sent", "msg-resume.meta.json")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_tempDir, "queue"), "*").Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessMessage_ShutdownStartsDuringSend_CommitStillCompletes()
    {
        // Regression: the post-send commit (metrics, archive/delete) must not run on the
        // shutdown token. A stop request arriving while Graph accepts the message must
        // not leave the files in queue/ (they would be re-sent on the next start).
        using var cts = new CancellationTokenSource();
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();   // shutdown begins while the send is in flight
                return Task.FromResult(SendOk);
            });
        var sut = CreateProcessor(graphClient: client);
        await EnqueueMessageAsync("msg-shutdown");

        await sut.ProcessBatchAsync(cts.Token);

        Directory.GetFiles(Path.Combine(_tempDir, "queue"), "*").Should().BeEmpty(
            "a message accepted by Graph must be committed even when shutdown started during the send");
    }

    [Fact]
    public async Task ProcessMessage_MetricsFailAfterSend_MessageIsStillCompletedAndNotRetried()
    {
        // Metrics are telemetry: a failing metrics write after a successful send must
        // neither reschedule the message (duplicate) nor block the queue cleanup.
        var metrics = Substitute.For<IMetricsService>();
        metrics.RecordEmailSentAsync(Arg.Any<SentEmailEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("metrics db locked"));
        var client = SucceedingClient();
        var sut = CreateProcessor(graphClient: client, metrics: metrics);
        await EnqueueMessageAsync("msg-metrics-fail");

        await sut.ProcessBatchAsync();

        await client.Received(1).SendAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Directory.GetFiles(Path.Combine(_tempDir, "queue"), "*").Should().BeEmpty();
    }

    // =========================================================================
    // Last attempt / last error persistence (shown in the ConfigTool Messages page)
    // =========================================================================

    [Fact]
    public async Task ProcessMessage_FailedAttempt_PersistsLastAttemptAndError()
    {
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("HTTP 404 ErrorInvalidUser: mailbox not found"));
        var sut = CreateProcessor(graphClient: client);
        var metaPath = await EnqueueMessageAsync("msg-lasterror");

        var before = DateTime.UtcNow;
        await sut.ProcessBatchAsync();

        var meta = JsonSerializer.Deserialize<MailMetadata>(await File.ReadAllTextAsync(metaPath))!;
        meta.RetryCount.Should().Be(1);
        meta.LastError.Should().Be("HTTP 404 ErrorInvalidUser: mailbox not found");
        meta.LastAttemptAt.Should().NotBeNull();
        meta.LastAttemptAt!.Value.Should().BeOnOrAfter(before.AddSeconds(-1));
    }

    [Fact]
    public async Task ProcessMessage_PermanentFailure_FailedMetaContainsLastAttemptAndError()
    {
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("permanent boom"));
        // MessageExpirationHours = 0 → a message is given up on its first failure
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions { MessageExpirationHours = 0 },
            graphClient: client);
        await EnqueueMessageAsync("msg-permanent");

        await sut.ProcessBatchAsync();

        var failedMetaPath = Path.Combine(_tempDir, "failed", "msg-permanent.meta.json");
        File.Exists(failedMetaPath).Should().BeTrue();
        var meta = JsonSerializer.Deserialize<MailMetadata>(await File.ReadAllTextAsync(failedMetaPath))!;
        meta.Status.Should().Be("failed");
        meta.LastError.Should().Be("permanent boom");
        meta.LastAttemptAt.Should().NotBeNull();
        meta.NextRetryAt.Should().BeNull("a permanently failed message has no scheduled retry");
    }

    [Fact]
    public async Task ProcessMessage_DeliveredAfterRetry_SentMetaHasSentAtAndNoNextRetry()
    {
        var client = SucceedingClient();
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions { ArchiveSentEmails = true },
            graphClient: client);
        // Simulate a message that already failed once: retry window elapsed
        await EnqueueMessageAsync("msg-retry-ok", retryCount: 1,
            nextRetryAt: DateTime.UtcNow.AddMinutes(-1));

        var before = DateTime.UtcNow;
        await sut.ProcessBatchAsync();

        var sentMetaPath = Path.Combine(_tempDir, "sent", "msg-retry-ok.meta.json");
        File.Exists(sentMetaPath).Should().BeTrue();
        var meta = JsonSerializer.Deserialize<MailMetadata>(await File.ReadAllTextAsync(sentMetaPath))!;
        meta.Status.Should().Be("sent");
        meta.SentAt.Should().NotBeNull();
        meta.SentAt!.Value.Should().BeOnOrAfter(before.AddSeconds(-1));
        meta.NextRetryAt.Should().BeNull("the delivered message must not look like another attempt is pending");
        meta.RetryCount.Should().Be(1, "the failed-attempt history is kept");
    }

    [Fact]
    public async Task ProcessMessage_EmlMissing_FailedMetaContainsReason()
    {
        var sut = CreateProcessor();
        await EnqueueMessageAsync("msg-noeml");
        File.Delete(Path.Combine(_tempDir, "queue", "msg-noeml.eml"));

        await sut.ProcessBatchAsync();

        var failedMetaPath = Path.Combine(_tempDir, "failed", "msg-noeml.meta.json");
        File.Exists(failedMetaPath).Should().BeTrue();
        var meta = JsonSerializer.Deserialize<MailMetadata>(await File.ReadAllTextAsync(failedMetaPath))!;
        meta.LastError.Should().Be("EML file missing from queue directory");
        meta.LastAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessMessage_DeliverySucceeds_ArchivesFilesWhenEnabled()
    {
        var client = Substitute.For<IGraphApiClient>();
        var opts = new MailQueueOptions { ArchiveSentEmails = true };
        var sut = CreateProcessor(queueOpts: opts, graphClient: client);
        await EnqueueMessageAsync("msg-archive");

        await sut.ProcessBatchAsync();

        var sentDir = Path.Combine(_tempDir, "sent");
        Directory.GetFiles(sentDir, "*.eml").Should().HaveCount(1);
        Directory.GetFiles(sentDir, "*.meta.json").Should().HaveCount(1);

        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.GetFiles(queueDir, "*").Should().BeEmpty();
    }

    // =========================================================================
    // Retry logic
    // =========================================================================

    [Fact]
    public async Task ProcessMessage_FirstDeliveryFails_IncrementsRetryCountAndSetsBackoff()
    {
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new InvalidOperationException("Simulated failure"));

        var sut = CreateProcessor(graphClient: client);
        await EnqueueMessageAsync("msg-retry");

        await sut.ProcessBatchAsync();

        // File must still be in queue (not moved to failed)
        var metaPath = Path.Combine(_tempDir, "queue", "msg-retry.meta.json");
        File.Exists(metaPath).Should().BeTrue();

        var json = await File.ReadAllTextAsync(metaPath);
        var meta = JsonSerializer.Deserialize(json, MailMetadataJsonContext.Default.MailMetadata)!;
        meta.RetryCount.Should().Be(1);
        meta.NextRetryAt.Should().NotBeNull();
        meta.NextRetryAt!.Value.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task ProcessMessage_ExpirationReached_MovesToFailed()
    {
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new InvalidOperationException("Simulated failure"));

        // 24 h expiration; received 25 h ago → the next failure is past the budget → failed
        var opts = new MailQueueOptions { MessageExpirationHours = 24 };
        var sut = CreateProcessor(queueOpts: opts, graphClient: client);
        await EnqueueMessageAsync("msg-failed", retryCount: 4, receivedAt: DateTime.UtcNow.AddHours(-25));

        await sut.ProcessBatchAsync();

        var failedDir = Path.Combine(_tempDir, "failed");
        Directory.GetFiles(failedDir, "*.meta.json").Should().HaveCount(1);

        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.GetFiles(queueDir, "*").Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessMessage_AfterTransientPhase_UsesSteadyInterval()
    {
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("still down"));
        // RetryCount 7 (beyond the 6 transient retries) → next gap is the steady interval (900 s)
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions
            {
                TransientRetryCount = 6, TransientRetryIntervalSeconds = 300,
                RetryIntervalSeconds = 900, MessageExpirationHours = 24,
            },
            graphClient: client);
        var metaPath = await EnqueueMessageAsync("msg-steady", retryCount: 7,
            nextRetryAt: DateTime.UtcNow.AddMinutes(-1));

        var before = DateTime.UtcNow;
        await sut.ProcessBatchAsync();

        var meta = JsonSerializer.Deserialize<MailMetadata>(await File.ReadAllTextAsync(metaPath))!;
        meta.RetryCount.Should().Be(8);
        meta.NextRetryAt.Should().NotBeNull();
        meta.NextRetryAt!.Value.Should().BeCloseTo(before.AddSeconds(900), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ProcessMessage_TransientPhase_UsesTransientInterval()
    {
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("transient"));
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions
            {
                TransientRetryCount = 6, TransientRetryIntervalSeconds = 300,
                RetryIntervalSeconds = 900, MessageExpirationHours = 24,
            },
            graphClient: client);
        var metaPath = await EnqueueMessageAsync("msg-transient"); // first failure → retryCount 1

        var before = DateTime.UtcNow;
        await sut.ProcessBatchAsync();

        var meta = JsonSerializer.Deserialize<MailMetadata>(await File.ReadAllTextAsync(metaPath))!;
        meta.RetryCount.Should().Be(1);
        meta.NextRetryAt!.Value.Should().BeCloseTo(before.AddSeconds(300), TimeSpan.FromSeconds(15));
    }

    // =========================================================================
    // Permanent vs. transient Graph rejections
    // =========================================================================

    [Fact]
    public async Task ProcessMessage_PermanentGraphRejection_FailsImmediatelyAndSendsNdr()
    {
        // A permanent rejection (e.g. invalid recipient, 404 mailbox) must not churn
        // through the 24 h retry window — the sender gets the NDR right away.
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GraphDeliveryException(
                "HTTP 404 ErrorInvalidUser: mailbox not found", isPermanent: true,
                new InvalidOperationException("inner")));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions { MessageExpirationHours = 24 },   // window NOT expired
            graphClient: client, notify: notify);
        await EnqueueMessageAsync("msg-permanent-reject");

        await sut.ProcessBatchAsync();

        Directory.GetFiles(Path.Combine(_tempDir, "failed"), "*.meta.json").Should().HaveCount(1);
        Directory.GetFiles(Path.Combine(_tempDir, "queue"), "*").Should().BeEmpty();
        await notify.Received(1).SendNdrAsync(Arg.Any<MailMetadata>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessMessage_TransientGraphRejection_IsRetriedNotFailed()
    {
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GraphDeliveryException(
                "HTTP 503 ServiceUnavailable", isPermanent: false,
                new InvalidOperationException("inner")));
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions { MessageExpirationHours = 24 },
            graphClient: client);
        var metaPath = await EnqueueMessageAsync("msg-transient-reject");

        await sut.ProcessBatchAsync();

        File.Exists(metaPath).Should().BeTrue("a transient rejection stays on the retry schedule");
        var meta = JsonSerializer.Deserialize<MailMetadata>(await File.ReadAllTextAsync(metaPath))!;
        meta.RetryCount.Should().Be(1);
        meta.NextRetryAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessMessage_NotificationMetaFailsPermanently_NoNdrForTheNdr()
    {
        // Loop guard: a failed NDR/notification must not generate an NDR for itself,
        // but the admin delivery-failure notification still fires.
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("still down"));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateProcessor(
            queueOpts: new MailQueueOptions { MessageExpirationHours = 0 },   // give up immediately
            graphClient: client, notify: notify);
        await EnqueueMessageAsync("msg-ndr-loop", isNotification: true);

        await sut.ProcessBatchAsync();

        Directory.GetFiles(Path.Combine(_tempDir, "failed"), "*.meta.json").Should().HaveCount(1);
        await notify.DidNotReceive().SendNdrAsync(Arg.Any<MailMetadata>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await notify.Received(1).NotifyEmailDeliveryFailedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // Back-off window
    // =========================================================================

    [Fact]
    public async Task ProcessMessage_BackoffNotElapsed_SkipsMessage()
    {
        var client = Substitute.For<IGraphApiClient>();
        var sut = CreateProcessor(graphClient: client);

        // Back-off window expires far in the future
        await EnqueueMessageAsync("msg-backoff", retryCount: 1, nextRetryAt: DateTime.UtcNow.AddHours(1));

        await sut.ProcessBatchAsync();

        await client.DidNotReceive().SendAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // Missing EML
    // =========================================================================

    [Fact]
    public async Task ProcessMessage_MissingEmlFile_MovesToFailed()
    {
        var sut = CreateProcessor();

        // Write only the meta.json, no .eml
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.CreateDirectory(queueDir);
        var meta = new MailMetadata
        {
            MessageId = "msg-noeml",
            From = "s@example.com",
            To = ["r@example.com"],
            Status = "queued"
        };
        await File.WriteAllTextAsync(
            Path.Combine(queueDir, "msg-noeml.meta.json"),
            JsonSerializer.Serialize(meta, MailMetadataJsonContext.Default.MailMetadata));

        await sut.ProcessBatchAsync();

        var failedDir = Path.Combine(_tempDir, "failed");
        Directory.GetFiles(failedDir, "*.meta.json").Should().HaveCount(1);
        Directory.GetFiles(queueDir, "*").Should().BeEmpty();
    }

    // =========================================================================
    // Orphaned EML cleanup
    // =========================================================================

    [Fact]
    public async Task CleanupOrphanedEmls_EmlWithoutMeta_MovesToFailed()
    {
        var sut = CreateProcessor();
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.CreateDirectory(queueDir);

        // Write only an .eml, no matching .meta.json; back-date it past the grace period
        var orphanPath = Path.Combine(queueDir, "orphan.eml");
        await File.WriteAllTextAsync(orphanPath, "Subject: Orphan\r\n\r\nBody");
        File.SetCreationTimeUtc(orphanPath, DateTime.UtcNow.AddMinutes(-10));

        await sut.CleanupOrphanedEmlsAsync();

        var failedDir = Path.Combine(_tempDir, "failed");
        File.Exists(Path.Combine(failedDir, "orphan.eml")).Should().BeTrue();
        File.Exists(Path.Combine(failedDir, "orphan.meta.json")).Should().BeTrue();
        Directory.GetFiles(queueDir, "*").Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupOrphanedEmls_FreshOrphan_IsLeftForTheNextPass()
    {
        // The cleanup now also runs hourly: an .eml younger than the grace period may be
        // a message whose meta rename is still in flight — it must not be quarantined.
        var sut = CreateProcessor();
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.CreateDirectory(queueDir);
        var orphanPath = Path.Combine(queueDir, "fresh-orphan.eml");
        await File.WriteAllTextAsync(orphanPath, "Subject: Fresh\r\n\r\nBody");

        await sut.CleanupOrphanedEmlsAsync();

        File.Exists(orphanPath).Should().BeTrue("orphans inside the grace period are left alone");
    }

    [Fact]
    public async Task CleanupOrphanedEmls_EmlWithMatchingMeta_IsNotTouched()
    {
        var sut = CreateProcessor();
        await EnqueueMessageAsync("msg-complete");

        await sut.CleanupOrphanedEmlsAsync();

        // Both files must remain in queue – the pair is complete
        var queueDir = Path.Combine(_tempDir, "queue");
        File.Exists(Path.Combine(queueDir, "msg-complete.eml")).Should().BeTrue();
        File.Exists(Path.Combine(queueDir, "msg-complete.meta.json")).Should().BeTrue();
    }

    // =========================================================================
    // Corrupt files
    // =========================================================================

    [Fact]
    public async Task ProcessBatch_CorruptMetaFile_MovesToFailed()
    {
        var sut = CreateProcessor();
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.CreateDirectory(queueDir);

        // Write invalid JSON as the meta file
        await File.WriteAllTextAsync(Path.Combine(queueDir, "msg-corrupt.meta.json"), "{ not valid json }}}");
        await File.WriteAllTextAsync(Path.Combine(queueDir, "msg-corrupt.eml"), "Subject: Corrupt\r\n\r\nBody");

        await sut.ProcessBatchAsync();

        var failedDir = Path.Combine(_tempDir, "failed");
        File.Exists(Path.Combine(failedDir, "msg-corrupt.meta.json")).Should().BeTrue();
        File.Exists(Path.Combine(failedDir, "msg-corrupt.eml")).Should().BeTrue();
        Directory.GetFiles(queueDir, "*").Should().BeEmpty();
    }

    // =========================================================================
    // Quarantine paths must not be silent (the SMTP client already got 250 OK)
    // =========================================================================

    [Fact]
    public async Task ProcessBatch_CorruptMetaFile_NotifiesAdmin()
    {
        // Regression: quarantining used to be log-only — the mail vanished without
        // any NDR or admin notification.
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateProcessor(notify: notify);
        var queueDir = Path.Combine(_tempDir, "queue");
        Directory.CreateDirectory(queueDir);
        await File.WriteAllTextAsync(Path.Combine(queueDir, "msg-corrupt-notify.meta.json"), "{ not json }}}");
        await File.WriteAllTextAsync(Path.Combine(queueDir, "msg-corrupt-notify.eml"), "Subject: X\r\n\r\nBody");

        await sut.ProcessBatchAsync();

        await notify.Received(1).NotifyEmailDeliveryFailedAsync(
            "msg-corrupt-notify", Arg.Is<string>(s => s.Contains("Corrupt")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessMessage_EmlMissing_SendsNdrAndNotifiesAdmin()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateProcessor(notify: notify);
        await EnqueueMessageAsync("msg-noeml-notify");
        File.Delete(Path.Combine(_tempDir, "queue", "msg-noeml-notify.eml"));

        await sut.ProcessBatchAsync();

        await notify.Received(1).NotifyEmailDeliveryFailedAsync(
            "msg-noeml-notify", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await notify.Received(1).SendNdrAsync(
            Arg.Is<MailMetadata>(m => m.MessageId == "msg-noeml-notify"), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // FIFO ordering
    // =========================================================================

    [Fact]
    public async Task ProcessBatch_DeliversMessagesInArrivalOrder_NotFilenameOrder()
    {
        // Regression: enumeration by GUID filename is random order — "zzz" queued first
        // must be sent before "aaa" queued later.
        var deliveredOrder = new List<string>();
        var client = Substitute.For<IGraphApiClient>();
        client.SendAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => { deliveredOrder.Add(ci.ArgAt<string>(3)); return Task.FromResult(SendOk); });
        var sut = CreateProcessor(graphClient: client);

        await EnqueueMessageAsync("zzz-first");
        await EnqueueMessageAsync("aaa-second");
        var queueDir = Path.Combine(_tempDir, "queue");
        File.SetCreationTimeUtc(Path.Combine(queueDir, "zzz-first.eml"), DateTime.UtcNow.AddMinutes(-10));
        File.SetCreationTimeUtc(Path.Combine(queueDir, "aaa-second.eml"), DateTime.UtcNow.AddMinutes(-5));

        await sut.ProcessBatchAsync();

        deliveredOrder.Should().Equal("zzz-first", "aaa-second");
    }

    // =========================================================================
    // Failed-email retention cleanup
    // =========================================================================

    [Fact]
    public void CleanupFailedEmails_ExpiredFile_DeletesBothFiles()
    {
        var sut = CreateProcessor(queueOpts: new MailQueueOptions { FailedEmailRetentionDays = 30 });
        var failedDir = Path.Combine(_tempDir, "failed");
        var metaPath = Path.Combine(failedDir, "old.meta.json");
        var emlPath = Path.Combine(failedDir, "old.eml");
        File.WriteAllText(metaPath, "{}");
        File.WriteAllText(emlPath, "data");
        File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow.AddDays(-31));

        sut.CleanupFailedEmails();

        File.Exists(metaPath).Should().BeFalse();
        File.Exists(emlPath).Should().BeFalse();
    }

    [Fact]
    public void CleanupFailedEmails_FileWithinRetention_IsKept()
    {
        var sut = CreateProcessor(queueOpts: new MailQueueOptions { FailedEmailRetentionDays = 30 });
        var metaPath = Path.Combine(_tempDir, "failed", "recent.meta.json");
        File.WriteAllText(metaPath, "{}");

        sut.CleanupFailedEmails();

        File.Exists(metaPath).Should().BeTrue();
    }

    [Fact]
    public void CleanupFailedEmails_RetentionZero_KeepsEverything()
    {
        var sut = CreateProcessor(queueOpts: new MailQueueOptions { FailedEmailRetentionDays = 0 });
        var metaPath = Path.Combine(_tempDir, "failed", "ancient.meta.json");
        File.WriteAllText(metaPath, "{}");
        File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow.AddYears(-1));

        sut.CleanupFailedEmails();

        File.Exists(metaPath).Should().BeTrue("0 disables the failed-mail cleanup (keep forever)");
    }

    // =========================================================================
    // Sent-email retention cleanup
    // =========================================================================

    [Fact]
    public async Task CleanupSentEmails_ArchiveDisabled_DoesNothing()
    {
        var sut = CreateProcessor(queueOpts: new MailQueueOptions { ArchiveSentEmails = false });
        var sentDir = Path.Combine(_tempDir, "sent");
        Directory.CreateDirectory(sentDir);

        await File.WriteAllTextAsync(Path.Combine(sentDir, "old.meta.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(sentDir, "old.eml"), "data");

        await sut.CleanupSentEmailsAsync();

        // Nothing deleted – archiving is off
        File.Exists(Path.Combine(sentDir, "old.meta.json")).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupSentEmails_FileWithinRetention_IsKept()
    {
        var opts = new MailQueueOptions { ArchiveSentEmails = true, SentEmailRetentionDays = 7 };
        var sut = CreateProcessor(queueOpts: opts);

        var sentDir = Path.Combine(_tempDir, "sent");
        var metaPath = Path.Combine(sentDir, "recent.meta.json");
        await File.WriteAllTextAsync(metaPath, "{}");
        // mtime = now → well within 7-day window, no manipulation needed

        await sut.CleanupSentEmailsAsync();

        File.Exists(metaPath).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupSentEmails_ExpiredFile_DeletesBothFiles()
    {
        var opts = new MailQueueOptions { ArchiveSentEmails = true, SentEmailRetentionDays = 7 };
        var sut = CreateProcessor(queueOpts: opts);

        var sentDir = Path.Combine(_tempDir, "sent");
        var metaPath = Path.Combine(sentDir, "old.meta.json");
        var emlPath = Path.Combine(sentDir, "old.eml");
        await File.WriteAllTextAsync(metaPath, "{}");
        await File.WriteAllTextAsync(emlPath, "data");

        // Back-date the modification time beyond the retention window
        var expired = DateTime.UtcNow.AddDays(-8);
        File.SetLastWriteTimeUtc(metaPath, expired);

        await sut.CleanupSentEmailsAsync();

        File.Exists(metaPath).Should().BeFalse();
        File.Exists(emlPath).Should().BeFalse();
    }
}
