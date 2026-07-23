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
    /// <param name="adminNotificationsEnabled">
    /// Admin notifications will actually be sent — the master switch is on <b>and</b> at least one
    /// recipient is configured. A recipient list on its own does not require a sender: switching
    /// the master off has to let the operator clear the address without emptying the list first.
    /// </param>
    internal static string? Validate(
        string? sender, bool adminNotificationsEnabled, bool ndrEnabled, bool reportEnabled, bool backupEmailEnabled)
    {
        var s = sender?.Trim();

        if (!string.IsNullOrEmpty(s))
            return MailAddress.TryCreate(s, out _)
                ? null
                : "The notification sender address is not a valid email address.";

        var dependents = new List<string>();
        if (adminNotificationsEnabled) dependents.Add("admin notifications");
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
        adminNotificationsEnabled: doc.Notification.NotifEnabled
                                && doc.Notification.RecipientAddresses.Count > 0,
        ndrEnabled: doc.Ndr.NdrEnabled,
        reportEnabled: doc.Notification.ReportEnabled,
        backupEmailEnabled: doc.Backup.EmailEnabled);
}
