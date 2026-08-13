namespace GraphMailer.Service.Services;

/// <summary>
/// Sends administrative alert emails when notable events occur.
/// All methods are safe to call unconditionally: if admin notifications are disabled
/// or Graph API is not configured, the call returns immediately.
///
/// The <c>Notify…</c>/<c>Notify…Recovered</c> pairs are <i>state-based</i>: monitors report the
/// current state on every check and this service decides what to send, so no monitor keeps
/// notification state of its own. Calling the raise method repeatedly while the condition persists
/// is expected and produces at most one mail per
/// <see cref="Configuration.AdminNotificationsOptions.RenotifyMinutes"/>; calling the recovery
/// method on every healthy check is equally expected and only mails if the condition had been
/// reported before.
/// </summary>
internal interface IAdminNotificationService
{
    /// <summary>
    /// Sends a Non-Delivery Report (NDR) for a message that was accepted via SMTP but
    /// permanently rejected by Microsoft 365.  Fire-and-forget — failures are logged at
    /// Warning level only.  Structurally loop-safe: NDRs are sent directly via Graph API
    /// and never enter the mail queue.
    /// </summary>
    Task SendNdrAsync(MailMetadata meta, string deliveryError, CancellationToken ct = default);

    Task NotifyEmailDeliveryFailedAsync(string messageId, string error, CancellationToken ct = default);
    Task NotifyCertificateExpiringAsync(string certSubject, DateTime notAfter, CancellationToken ct = default);
    Task NotifyCertificateExpiredAsync(string certSubject, DateTime notAfter, CancellationToken ct = default);

    /// <summary>Clears the TLS listener certificate alert — expiring and expired share one condition.</summary>
    Task NotifyCertificateRenewedAsync(string certSubject, DateTime notAfter, CancellationToken ct = default);

    /// <summary>
    /// Advance warning that the Graph client certificate (Entra app-only auth) is about to expire.
    /// There is no "expired" counterpart on purpose: once it lapses no Graph token can be acquired,
    /// so the message could never be delivered.
    /// </summary>
    Task NotifyGraphCertificateExpiringAsync(string certSubject, DateTime notAfter, CancellationToken ct = default);
    Task NotifyGraphCertificateRenewedAsync(string certSubject, DateTime notAfter, CancellationToken ct = default);
    Task NotifyLowDiskSpaceAsync(string drivePath, double freePercent, CancellationToken ct = default);
    Task NotifyDiskSpaceRecoveredAsync(string drivePath, double freePercent, CancellationToken ct = default);
    Task NotifyIpBlockedAsync(string ip, CancellationToken ct = default);
    Task NotifyAuthFailureAsync(string ip, string username, CancellationToken ct = default);
    Task NotifyGraphApiErrorAsync(string error, CancellationToken ct = default);

    /// <summary>
    /// Reports that the Entra app registration is missing required application permissions.
    /// <paramref name="missingRoles"/> is the bare role list used to detect a changed gap;
    /// <paramref name="detail"/> is the human-readable version including what each is needed for.
    /// </summary>
    Task NotifyGraphPermissionsMissingAsync(IReadOnlyList<string> missingRoles, string detail, CancellationToken ct = default);
    Task NotifyGraphPermissionsRestoredAsync(CancellationToken ct = default);

    /// <summary>
    /// Alerts that one or more <c>ENC[...]</c> values in <c>graphmailer.json</c> cannot be
    /// decrypted with the current Data Protection key ring (e.g. config restored to a
    /// different machine). <paramref name="fieldPaths"/> contains the affected JSON paths
    /// only — never the cipher text or any secret material.
    /// </summary>
    Task NotifyConfigDecryptionFailedAsync(IReadOnlyList<string> fieldPaths, CancellationToken ct = default);

    /// <summary>
    /// Reports the outcome of a scheduled configuration backup.
    /// <paramref name="detail"/> is a short summary (file/size/rotation) on success or the
    /// error reason on failure — never secret material.
    /// </summary>
    Task NotifyBackupResultAsync(bool succeeded, string detail, CancellationToken ct = default);
    Task NotifyGraphApiRestoredAsync(CancellationToken ct = default);

    /// <summary>
    /// Informs the admin that the weekly update check found a newer GraphMailer release.
    /// The caller (<see cref="UpdateCheck.UpdateCheckService"/>) deduplicates to one mail
    /// per new version.
    /// </summary>
    Task NotifyUpdateAvailableAsync(string currentVersion, string latestVersion, string? releaseUrl, CancellationToken ct = default);
    /// <summary>
    /// Reports a message the malware scan flagged. Batched like delivery failures — a malware
    /// wave would otherwise produce one mail per message. <paramref name="blocked"/> is false in
    /// audit mode, where the message was delivered anyway; the wording differs accordingly.
    /// <paramref name="sha256"/> is null for body detections, which cannot be allowlisted.
    /// </summary>
    Task NotifyMalwareDetectedAsync(
        string from, IReadOnlyList<string> recipients, string clientIp, string subject,
        string threatLocation, string? sha256, bool blocked, CancellationToken ct = default);

    /// <summary>
    /// Reports that scans are failing or being skipped. Threshold-based, because the scan fails
    /// open: without this the operator has no signal that messages are flowing past a scanner
    /// that stopped working.
    /// </summary>
    Task NotifyMalwareScanFailureAsync(string reason, CancellationToken ct = default);

    Task NotifyPortOutageAsync(int port, string reason, CancellationToken ct = default);
    Task NotifyPortRestoredAsync(int port, CancellationToken ct = default);
    Task NotifyServiceStartedAsync(CancellationToken ct = default);
    Task NotifyServiceStoppedAsync(CancellationToken ct = default);
}
