namespace GraphMailer.Service.Services;

/// <summary>
/// Sends administrative alert emails when notable events occur.
/// All methods are safe to call unconditionally: if admin notifications are disabled
/// or Graph API is not configured, the call returns immediately.
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
    Task NotifyLowDiskSpaceAsync(string drivePath, double freePercent, CancellationToken ct = default);
    Task NotifyIpBlockedAsync(string ip, CancellationToken ct = default);
    Task NotifyAuthFailureAsync(string ip, string username, CancellationToken ct = default);
    Task NotifyGraphApiErrorAsync(string error, CancellationToken ct = default);

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
    Task NotifyPortOutageAsync(int port, string reason, CancellationToken ct = default);
    Task NotifyPortRestoredAsync(int port, CancellationToken ct = default);
    Task NotifyServiceStartedAsync(CancellationToken ct = default);
    Task NotifyServiceStoppedAsync(CancellationToken ct = default);
}
