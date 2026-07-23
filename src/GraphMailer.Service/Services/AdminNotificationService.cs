using System.Collections.Concurrent;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services.Reporting;
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
            _pendingFailures.Add($"{messageId}: {error}");

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

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Warning,
            Title = "Certificate is expiring soon",
            Intro = "A certificate used by GraphMailer expires shortly. Renew or replace it before the expiry date to avoid a service degradation.",
            Fields = [new("Subject", certSubject), new("Expires", $"{notAfter:R}")],
        };
        return SendAsync(opts, $"Certificate expiring: {certSubject}", mail, "cert-expiring", ct);
    }

    public Task NotifyCertificateExpiredAsync(string certSubject, DateTime notAfter, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.CertificateExpired, "cert-expired")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = "Certificate has expired",
            Intro = "A certificate used by GraphMailer has expired and must be renewed or replaced now.",
            Fields = [new("Subject", certSubject), new("Expired", $"{notAfter:R}")],
        };
        return SendAsync(opts, $"Certificate EXPIRED: {certSubject}", mail, "cert-expired", ct);
    }

    public Task NotifyGraphCertificateExpiringAsync(string certSubject, DateTime notAfter, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.GraphCertificateExpiringWarning, "graph-cert-expiring")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = "Graph client certificate is expiring soon",
            Intro = "The certificate GraphMailer uses to authenticate against Microsoft Entra expires shortly. "
                  + "When it lapses, mail delivery stops completely — and this warning is the last one you will "
                  + "get: without a valid certificate GraphMailer cannot reach Graph and therefore cannot send "
                  + "any further notification. Renew the certificate and register the new one in the Entra app "
                  + "registration before the expiry date.",
            Fields = [new("Subject", certSubject), new("Expires", $"{notAfter:R}")],
        };
        return SendAsync(opts, $"Graph client certificate expiring: {certSubject}", mail, "graph-cert-expiring", ct);
    }

    public Task NotifyLowDiskSpaceAsync(string drivePath, double freePercent, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.LowDiskSpaceWarning, "disk-low")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Warning,
            Title = "Low disk space detected",
            Intro = "Free space on the drive holding the GraphMailer data directory is running low. The mail queue and logs stop working when the disk is full.",
            Fields = [new("Drive", drivePath), new("Free", $"{freePercent:F1}%")],
        };
        return SendAsync(opts, $"Low disk space: {freePercent:F1}% free on {drivePath}", mail, "disk-low", ct);
    }

    public Task NotifyIpBlockedAsync(string ip, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || !opts.NotificationTypes.IpBlockedAlert.Enabled) return Task.CompletedTask;

        var thr = opts.NotificationTypes.IpBlockedAlert;
        if (!IsAboveThreshold("ip-blocked", thr.FailureThreshold, thr.TimeWindowSeconds)) return Task.CompletedTask;
        if (!CanSend("ip-blocked")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = "IP address blocked",
            Intro = "An SMTP client exceeded the failure limit and has been blocked temporarily. Repeated blocks may indicate an attack or a misconfigured application.",
            Fields = [new("Blocked IP", ip)],
        };
        return SendAsync(opts, $"IP blocked: {ip}", mail, "ip-blocked", ct);
    }

    public Task NotifyAuthFailureAsync(string ip, string username, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || !opts.NotificationTypes.AuthenticationFailureAlert.Enabled) return Task.CompletedTask;

        var thr = opts.NotificationTypes.AuthenticationFailureAlert;
        if (!IsAboveThreshold("auth-failure", thr.FailureThreshold, thr.TimeWindowSeconds)) return Task.CompletedTask;
        if (!CanSend("auth-failure")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = "Authentication failure threshold reached",
            Intro = "Repeated SMTP authentication failures were detected. Check whether this is a misconfigured application or a password-guessing attempt.",
            Fields = [new("IP", ip), new("Username", username)],
        };
        return SendAsync(opts, $"Auth failures from {ip}", mail, "auth-failure", ct);
    }

    public Task NotifyGraphApiErrorAsync(string error, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.GraphApiConnectionError, "graph-error")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = "Graph API connectivity problem detected",
            Intro = "GraphMailer cannot reach the Microsoft Graph API. Queued mail is retried automatically and delivered once connectivity returns.",
            Fields = [new("Error", error)],
        };
        return SendAsync(opts, "Graph API connection error", mail, "graph-error", ct);
    }

    public Task NotifyGraphApiRestoredAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.GraphApiConnectivityRestored, "graph-restored")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Success,
            Title = "Graph API connection restored",
            Intro = "Graph API connectivity has been restored. Queued mail is being delivered again.",
        };
        return SendAsync(opts, "Graph API connection restored", mail, "graph-restored", ct);
    }

    public Task NotifyConfigDecryptionFailedAsync(IReadOnlyList<string> fieldPaths, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.ConfigDecryptionError, "config-decrypt-error")) return Task.CompletedTask;

        // Note: this notification is sent via Graph API. If the Graph client secret itself is
        // among the undecryptable values, the send will fail and only the LogError remains.
        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = "Config secrets cannot be decrypted",
            Intro = "One or more encrypted values in graphmailer.json cannot be decrypted with the current Data Protection key ring.",
            ItemsTitle = "Affected configuration values",
            Items = [.. fieldPaths],
            Note = "Likely cause: the key ring (HKLM\\SOFTWARE\\GraphMailer\\DataProtection) differs from the one that encrypted the config — e.g. after restoring graphmailer.json to a different machine. Re-enter the affected values in the ConfigTool to re-encrypt them with the current key.",
        };
        return SendAsync(opts, "Config secrets cannot be decrypted", mail, "config-decrypt-error", ct);
    }

    public Task NotifyBackupResultAsync(bool succeeded, string detail, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.BackupResult, "backup-result")) return Task.CompletedTask;

        var subject = succeeded ? "Configuration backup succeeded" : "Configuration backup FAILED";
        var mail = new NotificationEmail
        {
            Severity = succeeded ? NotificationSeverity.Success : NotificationSeverity.Critical,
            Title = succeeded ? "Configuration backup completed successfully" : "Configuration backup failed",
            Intro = succeeded
                ? "A scheduled configuration backup completed successfully."
                : "A scheduled configuration backup failed. Check the service log for details and verify the backup settings.",
            Items = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        return SendAsync(opts, subject, mail, "backup-result", ct);
    }

    public Task NotifyUpdateAvailableAsync(string currentVersion, string latestVersion, string? releaseUrl, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.UpdateAvailable, "update-available")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Info,
            Title = "A newer GraphMailer version is available",
            Fields = [new("Installed", currentVersion), new("Latest", latestVersion)],
            LinkUrl = releaseUrl,
            LinkLabel = "Release notes & download",
        };
        return SendAsync(opts, $"Update available: {latestVersion}", mail, "update-available", ct);
    }

    public Task NotifyPortOutageAsync(int port, string reason, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.PortMonitoringAlert, $"port-outage-{port}")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = $"Port {port} is not reachable",
            Intro = "A monitored SMTP listener port failed its connectivity check.",
            Fields = [new("Port", port.ToString()), new("Reason", reason)],
        };
        return SendAsync(opts, $"Port {port} outage", mail, $"port-outage-{port}", ct);
    }

    public Task NotifyPortRestoredAsync(int port, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.PortMonitoringRecovery, $"port-restored-{port}")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Success,
            Title = $"Port {port} is reachable again",
            Intro = "The monitored SMTP listener port passed its connectivity check again.",
        };
        return SendAsync(opts, $"Port {port} restored", mail, $"port-restored-{port}", ct);
    }

    public Task NotifyServiceStartedAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.ServiceStartStopAlert, "service-started")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Info,
            Title = "GraphMailer service started",
            Fields = [new("Started at", $"{DateTimeOffset.UtcNow:R}")],
        };
        return SendAsync(opts, "Service started", mail, "service-started", ct);
    }

    public Task NotifyServiceStoppedAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!IsEnabled(opts, opts.NotificationTypes.ServiceStartStopAlert, "service-stopped")) return Task.CompletedTask;

        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Info,
            Title = "GraphMailer service is stopping",
            Fields = [new("Stopping at", $"{DateTimeOffset.UtcNow:R}")],
        };
        return SendAsync(opts, "Service stopping", mail, "service-stopped", ct);
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
        var bodyHtml = NotificationHtmlRenderer.Render(BuildNdrEmail(meta, deliveryError));

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
            await EnqueueNotificationMailAsync(adminOpts.SenderAddress, [meta.From], subject, body, bodyHtml,
                $"NDR for {meta.MessageId} to sender <{meta.From}>", ct);
        }

        if (ndrOpts.NotifyAdmin && adminOpts.RecipientAddresses.Count > 0)
        {
            var adminSubject = $"{adminOpts.SubjectPrefix} NDR: {(string.IsNullOrEmpty(meta.Subject) ? meta.MessageId : meta.Subject)}";
            _logger.LogInformation("[AdminNotify] Queueing NDR admin copy for {MessageId}", meta.MessageId);
            await EnqueueNotificationMailAsync(adminOpts.SenderAddress, adminOpts.RecipientAddresses, adminSubject, body, bodyHtml,
                $"admin NDR copy for {meta.MessageId}", ct);
        }
    }

    /// <summary>
    /// Builds a multipart/alternative mail (plain text + HTML) as EML and writes it to
    /// the mail queue with the IsNotification flag, so the queue processor delivers it
    /// with the normal retry schedule. Never throws — a failed enqueue is logged like a
    /// failed send.
    /// </summary>
    private async Task EnqueueNotificationMailAsync(
        string from,
        IReadOnlyList<string> to,
        string subject,
        string bodyText,
        string bodyHtml,
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

            var builder = new MimeKit.BodyBuilder { TextBody = bodyText, HtmlBody = bodyHtml };
            mime.Body = builder.ToMessageBody();

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

    private static NotificationEmail BuildNdrEmail(MailMetadata meta, string deliveryError) => new()
    {
        Severity = NotificationSeverity.Critical,
        Title = "Your message could not be delivered",
        Intro = "The message below could not be delivered to one or more recipients.",
        Fields =
        [
            new("From", meta.From),
            new("To", meta.To.Count > 0 ? string.Join(", ", meta.To) : "(unknown)"),
            new("Subject", meta.Subject ?? "(no subject)"),
            new("Sent", $"{meta.ReceivedAt:R}"),
            new("Message-ID", string.IsNullOrEmpty(meta.SmtpMessageId) ? "N/A" : meta.SmtpMessageId),
            new("Reason", deliveryError),
        ],
        Kicker = "Non-Delivery Report",
        FooterNote = "This is an automatically generated Non-Delivery Report from GraphMailer. This is an unmonitored mailbox — do not reply.",
    };

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

    private async Task SendAsync(AdminNotificationsOptions opts, string subject, NotificationEmail mail, string cooldownKey, CancellationToken ct)
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

        var html = NotificationHtmlRenderer.Render(mail);
        await _graph.SendHtmlNotificationAsync(opts.SenderAddress, opts.RecipientAddresses, fullSubject, html, ct: ct);
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
        var mail = new NotificationEmail
        {
            Severity = NotificationSeverity.Critical,
            Title = $"Email delivery failed for {count} message{(count == 1 ? "" : "s")}",
            Intro = "The following message(s) failed to deliver. Failed messages are retried automatically; permanently failed mail ends up in the failed queue.",
            ItemsTitle = "Failed messages",
            Items = lines,
        };

        _logger.LogInformation("[AdminNotify] Flushing {Count} delivery failure(s) as batched notification", count);
        await SendAsync(opts, subject, mail, "delivery-failed-batch", CancellationToken.None);
    }
}
