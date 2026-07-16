using System.Net.Mail;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Validates the notification sender address against everything that depends on it.
///
/// Graph app-only authentication needs an explicit sender mailbox
/// (<c>/users/{sender}/sendMail</c>) — there is no fallback account. Without a sender
/// the service silently skips admin notifications, NDRs, scheduled reports and emailed
/// backups (log-only warnings), so saving such a configuration must be a visible error
/// instead of a surprise in production.
/// </summary>
internal static class SenderAddressRule
{
    /// <summary>Rule on primitives (unit-testable). Returns the error text, or null when valid.</summary>
    internal static string? Validate(
        string? sender, bool hasRecipients, bool ndrEnabled, bool reportEnabled, bool backupEmailEnabled)
    {
        var s = sender?.Trim();

        if (!string.IsNullOrEmpty(s))
            return MailAddress.TryCreate(s, out _)
                ? null
                : "The notification sender address is not a valid email address.";

        var dependents = new List<string>();
        if (hasRecipients) dependents.Add("admin notifications");
        if (ndrEnabled) dependents.Add("non-delivery reports (NDR)");
        if (reportEnabled) dependents.Add("scheduled reports");
        if (backupEmailEnabled) dependents.Add("emailed backups");

        return dependents.Count == 0
            ? null
            : "A sender email address is required — without one the service cannot send: " +
              string.Join(", ", dependents) + ".";
    }

    /// <summary>
    /// Evaluates the rule against a fully collected <see cref="ConfigDocument"/> —
    /// covers the cross-page dependency (emailed backups live on the Backup page).
    /// </summary>
    internal static string? Validate(ConfigDocument doc) => Validate(
        doc.Notification.NotifFrom,
        hasRecipients: doc.Notification.RecipientAddresses.Count > 0,
        ndrEnabled: doc.Ndr.NdrEnabled,
        reportEnabled: doc.Notification.ReportEnabled,
        backupEmailEnabled: doc.Backup.EmailEnabled);
}
