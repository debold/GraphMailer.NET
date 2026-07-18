using System.Collections.Concurrent;
using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace GraphMailer.Service.Services;

/// <summary>
/// Routes monitoring alerts to the admin via Graph API.
///
/// Batching (EmailDeliveryFailed): accumulates failures for <see cref="BatchedNotificationTypeOptions.BatchDelaySeconds"/>
/// seconds before sending a single summary message. Subsequent calls reset the timer.
///
/// Threshold (IpBlocked, AuthFailure): only notifies when the event count inside a rolling
/// <see cref="ThresholdNotificationTypeOptions.TimeWindowSeconds"/> window reaches
/// <see cref="ThresholdNotificationTypeOptions.FailureThreshold"/>.
///
/// Cooldown: per notification type, a minimum 60-second gap is enforced to prevent flooding.
/// </summary>
internal sealed class AdminNotificationService : IAdminNotificationService, IDisposable
{
    private const int MinCooldownSeconds = 60;

    private readonly IGraphApiClient _graph;
    private readonly MailQueueWriter _queueWriter;
    private readonly IOptionsMonitor<AdminNotificationsOptions> _options;
    private readonly IOptionsMonitor<NdrOptions> _ndrOptions;
    private readonly ILogger<AdminNotificationService> _logger;

    // --- Batching (EmailDeliveryFailed) ---
    private readonly object _batchLock = new();
    private readonly List<string> _pendingFailures = [];
    private Timer? _batchFlushTimer;

    // --- Threshold sliding windows ---
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _eventWindows = new();

    // --- Cooldown tracking ---
    private readonly object _cooldownLock = new();
    private readonly Dictionary<string, DateTime> _lastSent = new();

    public AdminNotificationService(
        IGraphApiClient graph,
        MailQueueWriter queueWriter,
        IOptionsMonitor<AdminNotificationsOptions> options,
        IOptionsMonitor<NdrOptions> ndrOptions,
        ILogger<AdminNotificationService> logger)
    {
        _graph = graph;
        _queueWriter = queueWriter;
        _options = options;
        _ndrOptions = ndrOptions;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Public notification methods
    // -------------------------------------------------------------------------

    public Task NotifyEmailDeliveryFailedAsync(string messageId, string error, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || !opts.NotificationTypes.EmailDeliveryFailed.Enabled) return Task.CompletedTask;

        var delay = opts.NotificationTypes.EmailDeliveryFailed.BatchDelaySeconds;
        lock (_batchLock)
        {
            _pendingFailures.Add($"  • {messageId}: {error}");

            // Restart/start the flush timer on every new failure
            _batchFlushTimer?.Dispose();
            _batchFlushTimer = new Timer(
                _ => Task.Run(async () =>
                {
                    try { await FlushFailureBatchAsync(); }
                    catch (Exception ex) { _logger.LogError(ex, "[AdminNotify] Batch flush failed"); }
                }),
                null,
                TimeSpan.FromSeconds(delay),
                Timeout.InfiniteTimeSpan);
        }
        return Task.CompletedTask;
    }

    public Task NotifyCertificateExpiringAsync(string certSubject, DateTime notAfter, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.CertificateExpiringWarning, "cert-expiring")) return Task.CompletedTask;

        var body = $"Certificate is expiring soon.\n\nSubject: {certSubject}\nExpires:  {notAfter:R}";
        return SendAsync(opts, $"Certificate expiring: {certSubject}", body, "cert-expiring", ct);
    }

    public Task NotifyCertificateExpiredAsync(string certSubject, DateTime notAfter, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.CertificateExpired, "cert-expired")) return Task.CompletedTask;

        var body = $"Certificate has expired!\n\nSubject: {certSubject}\nExpired: {notAfter:R}";
        return SendAsync(opts, $"Certificate EXPIRED: {certSubject}", body, "cert-expired", ct);
    }

    public Task NotifyLowDiskSpaceAsync(string drivePath, double freePercent, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.LowDiskSpaceWarning, "disk-low")) return Task.CompletedTask;

        var body = $"Low disk space detected.\n\nDrive:     {drivePath}\nFree:      {freePercent:F1}%";
        return SendAsync(opts, $"Low disk space: {freePercent:F1}% free on {drivePath}", body, "disk-low", ct);
    }

    public Task NotifyIpBlockedAsync(string ip, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || !opts.NotificationTypes.IpBlockedAlert.Enabled) return Task.CompletedTask;

        var thr = opts.NotificationTypes.IpBlockedAlert;
        if (!IsAboveThreshold("ip-blocked", thr.FailureThreshold, thr.TimeWindowSeconds)) return Task.CompletedTask;
        if (!CanSend("ip-blocked")) return Task.CompletedTask;

        var body = $"An IP address has been blocked.\n\nBlocked IP: {ip}";
        return SendAsync(opts, $"IP blocked: {ip}", body, "ip-blocked", ct);
    }

    public Task NotifyAuthFailureAsync(string ip, string username, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || !opts.NotificationTypes.AuthenticationFailureAlert.Enabled) return Task.CompletedTask;

        var thr = opts.NotificationTypes.AuthenticationFailureAlert;
        if (!IsAboveThreshold("auth-failure", thr.FailureThreshold, thr.TimeWindowSeconds)) return Task.CompletedTask;
        if (!CanSend("auth-failure")) return Task.CompletedTask;

        var body = $"Authentication failure threshold reached.\n\nIP:       {ip}\nUsername: {username}";
        return SendAsync(opts, $"Auth failures from {ip}", body, "auth-failure", ct);
    }

    public Task NotifyGraphApiErrorAsync(string error, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.GraphApiConnectionError, "graph-error")) return Task.CompletedTask;

        var body = $"Graph API connectivity problem detected.\n\nError: {error}";
        return SendAsync(opts, "Graph API connection error", body, "graph-error", ct);
    }

    public Task NotifyGraphApiRestoredAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.GraphApiConnectivityRestored, "graph-restored")) return Task.CompletedTask;

        return SendAsync(opts, "Graph API connection restored", "Graph API connectivity has been restored.", "graph-restored", ct);
    }

    public Task NotifyConfigDecryptionFailedAsync(IReadOnlyList<string> fieldPaths, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.ConfigDecryptionError, "config-decrypt-error")) return Task.CompletedTask;

        // Note: this notification is sent via Graph API. If the Graph client secret itself is
        // among the undecryptable values, the send will fail and only the LogError remains.
        var list = string.Join("\n", fieldPaths.Select(f => $"  • {f}"));
        var body =
            "One or more encrypted values in graphmailer.json cannot be decrypted with the current " +
            "Data Protection key ring.\n\nAffected configuration values:\n" + list +
            "\n\nLikely cause: the key ring (HKLM\\SOFTWARE\\GraphMailer\\DataProtection) differs from the " +
            "one that encrypted the config — e.g. after restoring graphmailer.json to a different machine.\n" +
            "Re-enter the affected values in the ConfigTool to re-encrypt them with the current key.";
        return SendAsync(opts, "Config secrets cannot be decrypted", body, "config-decrypt-error", ct);
    }

    public Task NotifyBackupResultAsync(bool succeeded, string detail, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.BackupResult, "backup-result")) return Task.CompletedTask;

        var subject = succeeded ? "Configuration backup succeeded" : "Configuration backup FAILED";
        var intro = succeeded
            ? "A scheduled configuration backup completed successfully.\n\n"
            : "A scheduled configuration backup failed.\n\n";
        return SendAsync(opts, subject, intro + detail, "backup-result", ct);
    }

    public Task NotifyUpdateAvailableAsync(string currentVersion, string latestVersion, string? releaseUrl, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.UpdateAvailable, "update-available")) return Task.CompletedTask;

        var body =
            $"A newer GraphMailer version is available.\n\n" +
            $"Installed: {currentVersion}\nLatest:    {latestVersion}\n" +
            (string.IsNullOrEmpty(releaseUrl) ? "" : $"\nRelease notes and download:\n{releaseUrl}\n");
        return SendAsync(opts, $"Update available: {latestVersion}", body, "update-available", ct);
    }

    public Task NotifyPortOutageAsync(int port, string reason, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.PortMonitoringAlert, $"port-outage-{port}")) return Task.CompletedTask;

        var body = $"Port connectivity alert.\n\nPort:   {port}\nReason: {reason}";
        return SendAsync(opts, $"Port {port} outage", body, $"port-outage-{port}", ct);
    }

    public Task NotifyPortRestoredAsync(int port, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.PortMonitoringRecovery, $"port-restored-{port}")) return Task.CompletedTask;

        return SendAsync(opts, $"Port {port} restored", $"Port {port} is reachable again.", $"port-restored-{port}", ct);
    }

    public Task NotifyServiceStartedAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.ServiceStartStopAlert, "service-started")) return Task.CompletedTask;

        return SendAsync(opts, "Service started", $"GraphMailer service started at {DateTimeOffset.UtcNow:R}.", "service-started", ct);
    }

    public Task NotifyServiceStoppedAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.ServiceStartStopAlert, "service-stopped")) return Task.CompletedTask;

        return SendAsync(opts, "Service stopping", $"GraphMailer service is stopping at {DateTimeOffset.UtcNow:R}.", "service-stopped", ct);
    }

    public async Task SendNdrAsync(MailMetadata meta, string deliveryError, CancellationToken ct = default)
    {
        var ndrOpts = _ndrOptions.CurrentValue;
        if (!ndrOpts.Enabled) return;

        var adminOpts = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(adminOpts.SenderAddress))
        {
            _logger.LogWarning("[AdminNotify] Cannot send NDR for {MessageId} – SenderAddress not configured", meta.MessageId);
            return;
        }

        var subject = $"Undeliverable: {(string.IsNullOrEmpty(meta.Subject) ? "(no subject)" : meta.Subject)}";
        var body = BuildNdrBody(meta, deliveryError);

        // NDRs are enqueued into the service's own mail queue instead of being sent
        // one-shot via Graph: a permanent delivery failure usually coincides with a
        // Graph outage, and a direct send would fail silently — the sender would never
        // learn the mail was lost. Queued NDRs inherit the full retry schedule.
        // IsNotification marks them so a failed NDR never generates an NDR for itself.

        if (ndrOpts.NotifySender
            && !string.IsNullOrWhiteSpace(meta.From)
            && !meta.From.Equals(adminOpts.SenderAddress, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[AdminNotify] Queueing NDR for {MessageId} to original sender <{From}>",
                meta.MessageId, meta.From);
            await EnqueueNotificationMailAsync(adminOpts.SenderAddress, [meta.From], subject, body,
                $"NDR for {meta.MessageId} to sender <{meta.From}>", ct);
        }

        if (ndrOpts.NotifyAdmin && adminOpts.RecipientAddresses.Count > 0)
        {
            var adminSubject = $"{adminOpts.SubjectPrefix} NDR: {(string.IsNullOrEmpty(meta.Subject) ? meta.MessageId : meta.Subject)}";
            _logger.LogInformation("[AdminNotify] Queueing NDR admin copy for {MessageId}", meta.MessageId);
            await EnqueueNotificationMailAsync(adminOpts.SenderAddress, adminOpts.RecipientAddresses, adminSubject, body,
                $"admin NDR copy for {meta.MessageId}", ct);
        }
    }

    /// <summary>
    /// Builds a plain-text mail as EML and writes it to the mail queue with the
    /// IsNotification flag, so the queue processor delivers it with the normal
    /// retry schedule. Never throws — a failed enqueue is logged like a failed send.
    /// </summary>
    private async Task EnqueueNotificationMailAsync(
        string from,
        IReadOnlyList<string> to,
        string subject,
        string bodyText,
        string logContext,
        CancellationToken ct)
    {
        try
        {
            var mime = new MimeKit.MimeMessage();
            mime.From.Add(MimeKit.MailboxAddress.Parse(from));
            foreach (var addr in to)
                mime.To.Add(MimeKit.MailboxAddress.Parse(addr));
            mime.Subject = subject;
            mime.Body = new MimeKit.TextPart("plain") { Text = bodyText };

            using var ms = new MemoryStream();
            await mime.WriteToAsync(ms, ct);

            await _queueWriter.WriteAsync(from, to, "internal", ms.ToArray(), ct, isNotification: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdminNotify] Failed to queue {Context}", logContext);
        }
    }

    private static string BuildNdrBody(MailMetadata meta, string deliveryError)
    {
        var toList = meta.To.Count > 0 ? string.Join(", ", meta.To) : "(unknown)";
        var msgId = string.IsNullOrEmpty(meta.SmtpMessageId) ? "N/A" : meta.SmtpMessageId;

        var sb = new StringBuilder();
        sb.AppendLine("Your message could not be delivered to one or more recipients.");
        sb.AppendLine();
        sb.AppendLine($"  From:       {meta.From}");
        sb.AppendLine($"  To:         {toList}");
        sb.AppendLine($"  Subject:    {meta.Subject ?? "(no subject)"}");
        sb.AppendLine($"  Sent:       {meta.ReceivedAt:R}");
        sb.AppendLine($"  Message-ID: {msgId}");
        sb.AppendLine();
        sb.AppendLine($"Reason: {deliveryError}");
        sb.AppendLine();
        sb.AppendLine("----");
        sb.Append("This is an automatically generated Non-Delivery Report from GraphMailer.");
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_batchLock)
        {
            _batchFlushTimer?.Dispose();
            _batchFlushTimer = null;
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private bool IsEnabled(AdminNotificationsOptions opts, NotificationTypeOptions typeOpts, string cooldownKey)
        => opts.Enabled && typeOpts.Enabled && CanSend(cooldownKey);

    private bool CanSend(string key)
    {
        var now = DateTime.UtcNow;
        lock (_cooldownLock)
        {
            if (_lastSent.TryGetValue(key, out var prev)
                && now - prev < TimeSpan.FromSeconds(MinCooldownSeconds))
                return false;
            _lastSent[key] = now;
            return true;
        }
    }

    private bool IsInCooldown(string key)
    {
        lock (_cooldownLock)
            return _lastSent.TryGetValue(key, out var last)
                   && DateTime.UtcNow - last < TimeSpan.FromSeconds(MinCooldownSeconds);
    }

    private bool IsAboveThreshold(string key, int threshold, int windowSeconds)
    {
        var window = _eventWindows.GetOrAdd(key, _ => new Queue<DateTime>());
        var cutoff = DateTime.UtcNow.AddSeconds(-windowSeconds);

        lock (window)
        {
            // Don't record events during an active cooldown — they are already
            // represented by the notification that triggered it. Recording them
            // would inflate the window so the threshold is permanently exceeded
            // the moment cooldown expires.
            if (!IsInCooldown(key))
                window.Enqueue(DateTime.UtcNow);
            while (window.Count > 0 && window.Peek() < cutoff)
                window.Dequeue();
            return window.Count >= threshold;
        }
    }

    private async Task SendAsync(AdminNotificationsOptions opts, string subject, string body, string cooldownKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.SenderAddress) || opts.RecipientAddresses.Count == 0)
        {
            var missing = string.IsNullOrWhiteSpace(opts.SenderAddress) && opts.RecipientAddresses.Count == 0
                ? "SenderAddress and RecipientAddresses are"
                : string.IsNullOrWhiteSpace(opts.SenderAddress)
                    ? "SenderAddress is"
                    : "RecipientAddresses are";
            _logger.LogWarning("[AdminNotify] Cannot send notification – {Missing} not configured", missing);
            return;
        }

        var fullSubject = $"{opts.SubjectPrefix} {subject}";
        _logger.LogInformation("[AdminNotify] Sending notification: {Subject}", fullSubject);

        await _graph.SendNotificationAsync(opts.SenderAddress, opts.RecipientAddresses, fullSubject, body, ct);
    }

    private async Task FlushFailureBatchAsync()
    {
        List<string> lines;
        lock (_batchLock)
        {
            if (_pendingFailures.Count == 0) return;
            lines = [.. _pendingFailures];
            _pendingFailures.Clear();
            _batchFlushTimer?.Dispose();
            _batchFlushTimer = null;
        }

        var opts = _options.CurrentValue;
        if (!opts.Enabled) return;
        if (!CanSend("delivery-failed-batch")) return;

        var count = lines.Count;
        var subject = $"Email delivery failed ({count} message{(count == 1 ? "" : "s")})";
        var body = $"The following message(s) failed to deliver:\n\n{string.Join("\n", lines)}";

        _logger.LogInformation("[AdminNotify] Flushing {Count} delivery failure(s) as batched notification", count);
        await SendAsync(opts, subject, body, "delivery-failed-batch", CancellationToken.None);
    }
}
