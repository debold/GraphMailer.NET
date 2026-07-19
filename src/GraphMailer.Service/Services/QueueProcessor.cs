using System.Diagnostics;
using System.Text.Json;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// Background service that polls the on-disk mail queue and delivers messages
/// via the Graph API client.
///
/// Processing rules:
///   - Reads up to <see cref="MailQueueOptions.BatchSize"/> *.meta.json files per tick.
///   - Skips messages whose <see cref="MailMetadata.NextRetryAt"/> is still in the future.
///   - On success: deletes both files (or archives to mail/sent/ if configured).
///   - On failure: increments RetryCount and schedules the next attempt (transient interval
///     for the first few retries, then the steady interval — see <see cref="RetrySchedule"/>).
///   - After <see cref="MailQueueOptions.MessageExpirationHours"/> since receipt: moves to mail/failed/.
///   - When Graph API credentials are not configured the batch is silently skipped.
/// </summary>
internal sealed class QueueProcessor : BackgroundService
{
    // One hung send must not stall the queue forever. Generous on purpose: a large
    // attachment goes through an upload session whose individual slice requests have
    // the SDK's own HTTP timeout, but the whole upload can legitimately take minutes.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(15);

    private readonly IOptionsMonitor<MailQueueOptions> _options;
    private readonly IOptionsMonitor<GraphApiOptions> _graphOptions;
    private readonly IGraphApiClient _graphClient;
    private readonly ITenantSenderDirectory _senderDirectory;
    private readonly IMetricsService _metrics;
    private readonly IAdminNotificationService _notify;
    private readonly ILogger<QueueProcessor> _logger;

    private readonly string _queuePath;
    private readonly string _sentPath;
    private readonly string _failedPath;

    public QueueProcessor(
        IOptionsMonitor<MailQueueOptions> options,
        IOptionsMonitor<GraphApiOptions> graphOptions,
        IGraphApiClient graphClient,
        ITenantSenderDirectory senderDirectory,
        IMetricsService metrics,
        IAdminNotificationService notify,
        ILogger<QueueProcessor> logger)
    {
        _options = options;
        _graphOptions = graphOptions;
        _graphClient = graphClient;
        _senderDirectory = senderDirectory;
        _metrics = metrics;
        _notify = notify;
        _logger = logger;

        var opts = options.CurrentValue;
        var mailDir = string.IsNullOrEmpty(opts.MailDir)
            ? AppPaths.MailDir
            : opts.MailDir;
        _queuePath = Path.Combine(mailDir, "queue");
        _sentPath = Path.Combine(mailDir, "sent");
        _failedPath = Path.Combine(mailDir, "failed");

        Directory.CreateDirectory(_queuePath);
        Directory.CreateDirectory(_sentPath);
        Directory.CreateDirectory(_failedPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[QueueProcessor] Started, polling every {Interval}s",
            _options.CurrentValue.PollingIntervalSeconds);

        try
        {
            await CleanupOrphanedEmlsAsync(stoppingToken);
            await CleanupSentEmailsAsync(stoppingToken);   // honour retention policy on startup
            CleanupFailedEmails(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            // Startup housekeeping is best-effort — queue processing must start regardless.
            _logger.LogError(ex, "[QueueProcessor] Startup cleanup failed — continuing with queue processing");
        }

        using var pollingTimer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.CurrentValue.PollingIntervalSeconds));
        using var cleanupTimer = new PeriodicTimer(TimeSpan.FromHours(1));

        // Each tick is individually guarded: one faulted batch (e.g. an IO error while
        // enumerating the queue) must not end queue processing for the rest of the
        // process lifetime — the next tick simply tries again.
        async Task PollingLoop()
        {
            try
            {
                while (await pollingTimer.WaitForNextTickAsync(stoppingToken))
                {
                    try { await ProcessBatchAsync(stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[QueueProcessor] Queue batch failed — retrying on the next polling tick");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        async Task CleanupLoop()
        {
            try
            {
                while (await cleanupTimer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        await CleanupSentEmailsAsync(stoppingToken);
                        CleanupFailedEmails(stoppingToken);
                        await CleanupOrphanedEmlsAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[QueueProcessor] Retention cleanup failed — retrying on the next cycle");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        await Task.WhenAll(PollingLoop(), CleanupLoop());

        _logger.LogInformation("[QueueProcessor] Stopped");
    }

    // internal so that unit tests can invoke a single batch without the timer
    internal async Task ProcessBatchAsync(CancellationToken ct = default)
    {
        if (!_graphOptions.CurrentValue.IsConfigured)
        {
            _logger.LogDebug("[QueueProcessor] Graph API not configured – skipping batch");
            return;
        }

        var opts = _options.CurrentValue;

        // Process up to BatchSize messages that are actually *attempted* this tick.
        // Messages still in their back-off window are skipped and must NOT count against
        // the budget — otherwise a prefix of back-off messages (enumeration is by GUID
        // filename, not by readiness) blocks every later message forever (head-of-line
        // blocking: newly queued mail never gets a first attempt).
        // FIFO: enumeration by GUID filename is effectively random order — sort by the
        // .eml file's creation time (written once at receipt, never rewritten) so mail
        // leaves in arrival order. Processing stays deliberately sequential: parallel
        // sends would break this ordering again.
        var processed = 0;
        foreach (var metaPath in Directory.EnumerateFiles(_queuePath, "*.meta.json")
                     .OrderBy(QueueOrderKey))
        {
            if (ct.IsCancellationRequested) break;
            if (processed >= opts.BatchSize) break;
            if (await ProcessMessageAsync(metaPath, opts, ct))
                processed++;
        }
    }

    /// <summary>Receipt time of the message: creation time of the .eml (falls back to the meta file).</summary>
    private DateTime QueueOrderKey(string metaPath)
    {
        var emlPath = Path.Combine(_queuePath, Path.GetFileName(metaPath).Replace(".meta.json", ".eml"));
        try
        {
            return File.GetCreationTimeUtc(File.Exists(emlPath) ? emlPath : metaPath);
        }
        catch
        {
            return DateTime.MaxValue;   // unreadable entry sorts last; ProcessMessageAsync quarantines it
        }
    }

    /// <summary>
    /// Processes one queued message. Returns <see langword="true"/> when the message was
    /// actually attempted (delivered, failed, or quarantined) and <see langword="false"/>
    /// when it was skipped because its back-off window has not yet elapsed.
    /// </summary>
    private async Task<bool> ProcessMessageAsync(string metaPath, MailQueueOptions opts, CancellationToken ct)
    {
        MailMetadata meta;
        try
        {
            var json = await File.ReadAllTextAsync(metaPath, ct);
            meta = JsonSerializer.Deserialize(json, MailMetadataJsonContext.Default.MailMetadata)
                   ?? throw new InvalidOperationException("Deserialized MailMetadata is null");
        }
        catch (Exception ex)
        {
            // Meta file is corrupt or unreadable – derive MessageId from the filename and quarantine.
            var messageId = Path.GetFileName(metaPath).Replace(".meta.json", string.Empty);
            _logger.LogError(ex,
                "[QueueProcessor] Corrupt or unreadable meta file for {MessageId}, moving to failed",
                messageId);

            var syntheticMeta = new MailMetadata
            {
                MessageId = messageId,
                Status = "failed",
                LastAttemptAt = DateTime.UtcNow,
                LastError = $"Corrupt or unreadable meta file: {ex.Message}",
            };
            var orphanEml = Path.Combine(_queuePath, $"{messageId}.eml");
            await QuarantineAsync(metaPath, File.Exists(orphanEml) ? orphanEml : null, syntheticMeta,
                syntheticMeta.LastError, ct);
            return true;
        }

        // Idempotency guard: SentAt set means Graph already accepted this message on an
        // earlier attempt, but the process was interrupted (crash, shutdown) before the
        // queue files were removed. Finish the cleanup — never send again: Graph sendMail
        // is not idempotent and a re-send duplicates the mail at the recipient.
        if (meta.SentAt.HasValue)
        {
            _logger.LogWarning(
                "[QueueProcessor] {MessageId} was already delivered at {SentAt:u} — completing interrupted cleanup without re-sending",
                meta.MessageId, meta.SentAt.Value);
            var deliveredEml = Path.Combine(_queuePath, $"{meta.MessageId}.eml");
            await ArchiveOrDeleteAsync(deliveredEml, metaPath, meta, opts);
            return true;
        }

        // Honour exponential back-off window
        if (meta.NextRetryAt.HasValue && meta.NextRetryAt.Value > DateTime.UtcNow)
        {
            _logger.LogDebug("[QueueProcessor] {MessageId} back-off not elapsed (next: {Next:u}), skipping",
                meta.MessageId, meta.NextRetryAt.Value);
            return false;
        }

        var emlPath = Path.Combine(_queuePath, $"{meta.MessageId}.eml");
        if (!File.Exists(emlPath))
        {
            _logger.LogWarning("[QueueProcessor] EML file missing for {MessageId}, moving to failed",
                meta.MessageId);
            meta.LastAttemptAt = DateTime.UtcNow;
            meta.LastError = "EML file missing from queue directory";
            await QuarantineAsync(metaPath, emlPath: null, meta, meta.LastError, ct);
            return true;
        }

        _logger.LogDebug("[QueueProcessor] Processing {MessageId} | From: {From} | Subject: {Subject} (attempt {Attempt})",
            meta.MessageId, meta.From, meta.Subject, meta.RetryCount + 1);

        byte[] emlBytes;
        try
        {
            emlBytes = await File.ReadAllBytesAsync(emlPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[QueueProcessor] Cannot read EML for {MessageId}, moving to failed",
                meta.MessageId);
            meta.LastAttemptAt = DateTime.UtcNow;
            meta.LastError = $"Cannot read EML file: {ex.Message}";
            await QuarantineAsync(metaPath, emlPath, meta, meta.LastError, ct);
            return true;
        }

        try
        {
            // Graph's /users/{key}/sendMail accepts only UPN or object id as user key.
            // When the sender directory knows the address (incl. aliases), send as the
            // resolved object id so secondary proxyAddresses work as senders.
            var sendAs = _senderDirectory.TryResolveGraphUserKey(meta.From, out var userKey)
                ? userKey
                : meta.From;
            if (sendAs != meta.From)
                _logger.LogDebug("[QueueProcessor] {MessageId}: sender {From} resolved to Graph user {UserKey}",
                    meta.MessageId, meta.From, sendAs);

            var sw = Stopwatch.StartNew();
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            GraphDeliveryResult delivery;
            try
            {
                delivery = await _graphClient.SendAsync(emlBytes, sendAs, meta.To, meta.MessageId, sendCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The per-send timeout fired (not a shutdown) — surface it as an
                // ordinary transient failure so the message follows the retry schedule.
                throw new TimeoutException(
                    $"Graph send timed out after {SendTimeout.TotalMinutes:0} minutes");
            }
            sw.Stop();

            // Delivered: record when, and drop the retry schedule left over from
            // earlier failed attempts (LastAttemptAt/LastError stay as history).
            meta.SentAt = DateTime.UtcNow;
            meta.NextRetryAt = null;

            // Commit phase — Graph has accepted the message; from here on it must never
            // be sent again. Persist the SentAt marker first (a crash mid-commit is then
            // caught by the idempotency guard above) and run every remaining step without
            // the shutdown token: a stop between send and cleanup must not leave the
            // message looking undelivered (it would be re-sent on the next start).
            try
            {
                await WriteMetaAtomicAsync(metaPath, meta);

                _logger.LogInformation("[QueueProcessor] Delivered {MessageId} | From: {From} | To: {Recipients} | Subject: {Subject}",
                    meta.MessageId, meta.From, string.Join(", ", meta.To), meta.Subject);

                try
                {
                    await _metrics.RecordEmailSentAsync(new SentEmailEvent
                    {
                        From = meta.From,
                        To = meta.To,
                        MessageId = meta.MessageId,
                        Subject = meta.Subject,
                        SizeBytes = emlBytes.Length,
                        DurationMs = (int)sw.ElapsedMilliseconds,
                        RetryCount = meta.RetryCount,
                        DeliveryVariant = delivery.Variant,
                        QueueLatencyMs = (long)Math.Max(0, (meta.SentAt.Value - meta.ReceivedAt).TotalMilliseconds),
                        AttachmentCount = delivery.AttachmentCount,
                        AttachmentBytes = delivery.AttachmentBytes,
                    }, CancellationToken.None);
                }
                catch (Exception mex)
                {
                    _logger.LogWarning(mex,
                        "[QueueProcessor] Failed to record sent-mail metrics for {MessageId}: {Error}",
                        meta.MessageId, mex.Message);
                }

                await ArchiveOrDeleteAsync(emlPath, metaPath, meta, opts);
            }
            catch (Exception commitEx)
            {
                // The message IS delivered — never schedule a retry here. The SentAt
                // marker makes a later pass finish the cleanup instead of re-sending.
                _logger.LogError(commitEx,
                    "[QueueProcessor] Post-delivery cleanup failed for {MessageId} — will be completed on a later pass (no re-send)",
                    meta.MessageId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // our shutdown token — let the polling loop handle clean exit
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            meta.RetryCount++;
            meta.LastAttemptAt = now;
            meta.LastError = ex.Message;

            // Permanent Graph rejections (invalid recipient, sender mailbox not found,
            // request too large, hybrid MailboxNotEnabledForRESTAPI) can never succeed on
            // retry — fail fast so the sender gets the NDR now, not after the expiration
            // window. Everything else follows the time-based give-up: retried until
            // MessageExpirationHours have elapsed since receipt (Exchange-style).
            var permanentRejection = ex is GraphDeliveryException { IsPermanent: true };

            if (permanentRejection || RetrySchedule.HasExpired(meta.ReceivedAt, now, opts.MessageExpirationHours))
            {
                if (permanentRejection)
                    _logger.LogError(ex,
                        "[QueueProcessor] {MessageId} permanently rejected by Graph API — failing immediately without further retries",
                        meta.MessageId);
                else
                    _logger.LogError(ex,
                        "[QueueProcessor] {MessageId} permanently failed after {Hours}h ({Attempts} attempt(s)), moving to failed",
                        meta.MessageId, opts.MessageExpirationHours, meta.RetryCount);
                await QuarantineAsync(metaPath, emlPath, meta, ex.Message, ct, permanentRejection);
            }
            else
            {
                // Two-phase interval: fast transient retries, then a steady interval.
                var interval = TimeSpan.FromSeconds(
                    RetrySchedule.NextRetryIntervalSeconds(meta.RetryCount, opts));
                meta.NextRetryAt = now.Add(interval);
                meta.Status = "queued";

                _logger.LogWarning(ex,
                    "[QueueProcessor] {MessageId} failed (attempt {Attempt}), retry after {RetryAt:u}",
                    meta.MessageId, meta.RetryCount, meta.NextRetryAt.Value);

                await WriteMetaAtomicAsync(metaPath, meta);
            }
        }

        return true;
    }

    /// <summary>
    /// Removes a delivered message from the queue (or archives it to mail/sent/).
    /// Runs in the post-delivery commit phase, so it is deliberately not cancellable
    /// and re-entrant: an earlier interrupted commit may already have moved the .eml.
    /// </summary>
    private async Task ArchiveOrDeleteAsync(
        string emlPath, string metaPath, MailMetadata meta, MailQueueOptions opts)
    {
        if (opts.ArchiveSentEmails)
        {
            meta.Status = "sent";
            var archivedJson = JsonSerializer.Serialize(meta, MailMetadataJsonContext.Default.MailMetadata);
            var destEml = Path.Combine(_sentPath, Path.GetFileName(emlPath));
            var destMeta = Path.Combine(_sentPath, Path.GetFileName(metaPath));

            await File.WriteAllTextAsync(destMeta, archivedJson);
            if (File.Exists(emlPath))
                File.Move(emlPath, destEml, overwrite: true);
            File.Delete(metaPath);
        }
        else
        {
            // File.Delete is a no-op for missing files — safe on a resumed commit.
            File.Delete(emlPath);
            File.Delete(metaPath);
        }
    }

    /// <summary>
    /// Writes the meta file atomically (temp + rename), matching MailQueueWriter's
    /// pattern: a crash mid-write must never leave a corrupt meta behind (a corrupt
    /// meta quarantines the message). Deliberately not cancellable — meta updates
    /// are tiny and must complete once started.
    /// </summary>
    private static async Task WriteMetaAtomicAsync(string metaPath, MailMetadata meta)
    {
        var json = JsonSerializer.Serialize(meta, MailMetadataJsonContext.Default.MailMetadata);
        var tmpPath = metaPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json);
        File.Move(tmpPath, metaPath, overwrite: true);
    }

    /// <summary>
    /// Moves a message to mail/failed/ and raises the failure signals. The SMTP client
    /// already received 250 OK, so a quarantine must never be silent: the operator gets
    /// a delivery-failed notification and, when the sender is known, an NDR — exactly
    /// like a permanently failed delivery. NDRs/notifications themselves are exempt
    /// from the NDR (loop guard); the admin notification remains their operator signal.
    /// </summary>
    private async Task QuarantineAsync(
        string metaPath, string? emlPath, MailMetadata meta, string reason, CancellationToken ct,
        bool permanentRejection = false)
    {
        await MoveToFailedAsync(metaPath, emlPath, meta, ct);
        await _metrics.RecordEmailFailedAsync(meta.MessageId, reason, meta.From, meta.Subject,
            retryCount: meta.RetryCount, permanent: permanentRejection, ct: ct);
        await _notify.NotifyEmailDeliveryFailedAsync(meta.MessageId, reason, ct);
        if (!meta.IsNotification)
            await _notify.SendNdrAsync(meta, reason, ct);
    }

    private async Task MoveToFailedAsync(
        string metaPath, string? emlPath, MailMetadata meta, CancellationToken ct)
    {
        meta.Status = "failed";
        meta.NextRetryAt = null;   // permanently failed — no further attempts are scheduled
        var failedJson = JsonSerializer.Serialize(meta, MailMetadataJsonContext.Default.MailMetadata);
        var destMeta = Path.Combine(_failedPath, Path.GetFileName(metaPath));

        await File.WriteAllTextAsync(destMeta, failedJson, ct);

        if (emlPath is not null && File.Exists(emlPath))
        {
            var destEml = Path.Combine(_failedPath, Path.GetFileName(emlPath));
            File.Move(emlPath, destEml, overwrite: true);
        }

        File.Delete(metaPath);
    }

    /// <summary>
    /// Runs once at startup. Moves any .eml file that has no matching .meta.json
    /// to mail/failed/ so it does not silently accumulate in the queue directory.
    /// This can happen if the service crashed between writing the .eml and the .meta.json.
    /// </summary>
    /// <summary>
    /// Deletes file pairs from mail/sent/ that have exceeded the configured retention period.
    /// Runs on startup and then every hour. No-op when ArchiveSentEmails is false.
    /// </summary>
    internal Task CleanupSentEmailsAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.ArchiveSentEmails) return Task.CompletedTask;

        var cutoff = DateTime.UtcNow.AddDays(-opts.SentEmailRetentionDays);
        var deleted = 0;

        foreach (var metaPath in Directory.GetFiles(_sentPath, "*.meta.json"))
        {
            if (ct.IsCancellationRequested) break;

            if (File.GetLastWriteTimeUtc(metaPath) < cutoff)
            {
                var messageId = Path.GetFileNameWithoutExtension(metaPath).Replace(".meta", string.Empty);
                var emlPath = Path.Combine(_sentPath, $"{messageId}.eml");

                File.Delete(metaPath);
                if (File.Exists(emlPath)) File.Delete(emlPath);

                _logger.LogDebug("[QueueProcessor] Retention: deleted {MessageId} from sent/", messageId);
                deleted++;
            }
        }

        if (deleted > 0)
            _logger.LogInformation(
                "[QueueProcessor] Retention cleanup: deleted {Count} sent email(s) older than {Days} day(s)",
                deleted, opts.SentEmailRetentionDays);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes file pairs from mail/failed/ that have exceeded FailedEmailRetentionDays.
    /// 0 disables the cleanup (keep forever). Runs on startup and then hourly —
    /// without it, permanently failed mail accumulates unbounded.
    /// </summary>
    internal void CleanupFailedEmails(CancellationToken ct = default)
    {
        var retentionDays = _options.CurrentValue.FailedEmailRetentionDays;
        if (retentionDays <= 0) return;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var metaPath in Directory.GetFiles(_failedPath, "*.meta.json"))
        {
            if (ct.IsCancellationRequested) break;

            if (File.GetLastWriteTimeUtc(metaPath) < cutoff)
            {
                var messageId = Path.GetFileNameWithoutExtension(metaPath).Replace(".meta", string.Empty);
                var emlPath = Path.Combine(_failedPath, $"{messageId}.eml");

                File.Delete(metaPath);
                if (File.Exists(emlPath)) File.Delete(emlPath);

                _logger.LogDebug("[QueueProcessor] Retention: deleted {MessageId} from failed/", messageId);
                deleted++;
            }
        }

        if (deleted > 0)
            _logger.LogInformation(
                "[QueueProcessor] Retention cleanup: deleted {Count} failed email(s) older than {Days} day(s)",
                deleted, retentionDays);
    }

    internal async Task CleanupOrphanedEmlsAsync(CancellationToken ct = default)
    {
        // Grace period: the queue writer renames the .eml before the .meta.json, so a
        // scan running exactly between the two renames would see a healthy message as
        // an orphan. Anything younger than this is left for the next pass (the cleanup
        // now runs hourly, no longer only at startup).
        var graceCutoff = DateTime.UtcNow.AddMinutes(-5);
        var emlFiles = Directory.GetFiles(_queuePath, "*.eml");

        foreach (var emlPath in emlFiles)
        {
            if (ct.IsCancellationRequested) break;

            var messageId = Path.GetFileNameWithoutExtension(emlPath);
            var metaPath = Path.Combine(_queuePath, $"{messageId}.meta.json");

            if (!File.Exists(metaPath) && File.GetCreationTimeUtc(emlPath) < graceCutoff)
            {
                _logger.LogWarning(
                    "[QueueProcessor] Orphaned EML found (no matching meta): {MessageId}, moving to failed",
                    messageId);

                var destEml = Path.Combine(_failedPath, Path.GetFileName(emlPath));
                File.Move(emlPath, destEml, overwrite: true);

                // Write a minimal meta so the failed/ folder stays consistent
                var meta = new MailMetadata
                {
                    MessageId = messageId,
                    Status = "failed",
                    ReceivedAt = DateTime.UtcNow,
                    LastAttemptAt = DateTime.UtcNow,
                    LastError = "Orphaned EML without matching meta file (service possibly crashed mid-write)",
                };
                var destMeta = Path.Combine(_failedPath, $"{messageId}.meta.json");
                await File.WriteAllTextAsync(
                    destMeta,
                    JsonSerializer.Serialize(meta, MailMetadataJsonContext.Default.MailMetadata),
                    ct);
            }
        }
    }
}
