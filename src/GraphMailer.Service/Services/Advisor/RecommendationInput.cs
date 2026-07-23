using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.Service.Services.Advisor;

/// <summary>
/// Flat snapshot of everything <see cref="RecommendationEngine"/> needs to decide which hints
/// apply. Deliberately free of <c>IOptions</c>, <c>IConfiguration</c> and WPF types so the
/// rules stay a pure function of plain values and are trivially testable.
///
/// Two producers feed it: the service builds it from its options monitors
/// (<see cref="Reporting.ReportDataCollector"/>), the ConfigTool from the document it has in
/// memory (<see cref="FromConfigDocument"/>). Both must describe the *persisted* configuration
/// so the sidebar badge and the emailed report never disagree.
/// </summary>
internal sealed record RecommendationInput
{
    // ── Graph / Entra ──
    /// <summary>Tenant id and client id are both present — the install got past initial setup.</summary>
    public bool GraphConfigured { get; init; }
    public bool GraphUsesClientSecret { get; init; }
    public bool GraphUsesCertificate { get; init; }

    // ── Listeners ──
    /// <summary>Number of enabled SMTP listeners; 0 means nothing is configured yet.</summary>
    public int EnabledListenerCount { get; init; }
    /// <summary>At least one enabled listener uses StartTls or Ssl.</summary>
    public bool HasTlsListener { get; init; }
    /// <summary>
    /// Names of enabled listeners that run in plain mode <b>and</b> accept SMTP authentication
    /// (AuthMode Optional or Required) — those carry credentials over an unencrypted connection.
    /// A plain listener with AuthMode "None" is not listed: it never sees a password.
    /// </summary>
    public IReadOnlyList<string> PlaintextAuthListeners { get; init; } = [];

    // ── Feature toggles ──
    public bool SenderValidationEnabled { get; init; }
    public bool BackupEnabled { get; init; }
    public bool NdrEnabled { get; init; }
    public bool UpdateCheckEnabled { get; init; }
    public bool TelemetryEnabled { get; init; }

    // ── Operations ──
    /// <summary>Serilog minimum level, e.g. "Information". Empty when not configured anywhere.</summary>
    public string LogLevel { get; init; } = "Information";
    public bool HasAdminNotificationRecipients { get; init; }
    /// <summary>The admin-notification master switch.</summary>
    public bool AdminNotificationsEnabled { get; init; }
    /// <summary>
    /// Display names of the early-warning alerts that are switched off. These are the ones that
    /// tell an operator <i>before</i> something breaks; the remaining event types report things
    /// that have already happened and are a matter of taste.
    /// </summary>
    public IReadOnlyList<string> DisabledCriticalNotifications { get; init; } = [];

    /// <summary>The recommended Serilog level — anything else raises the log-level hint.</summary>
    internal const string RecommendedLogLevel = "Information";

    /// <summary>
    /// Collects the display names of the early-warning alerts that are switched off. Shared by both
    /// producers so the ConfigTool and the report agree on what counts as critical.
    ///
    /// The Graph client certificate warning only counts when Graph actually authenticates with a
    /// certificate — and it is listed first, because it is the only alert whose absence is
    /// unrecoverable: once that certificate lapses, GraphMailer cannot send the bad news either.
    /// </summary>
    internal static IReadOnlyList<string> CollectDisabledCriticalNotifications(
        bool graphUsesCertificate,
        bool graphCertExpiringEnabled,
        bool deliveryFailedEnabled,
        bool graphDownEnabled,
        bool tlsCertExpiringEnabled,
        bool diskSpaceEnabled,
        bool portDownEnabled)
    {
        var off = new List<string>();
        if (graphUsesCertificate && !graphCertExpiringEnabled) off.Add("Graph client certificate expiring");
        if (!deliveryFailedEnabled) off.Add("Email delivery failed");
        if (!graphDownEnabled) off.Add("Graph API unreachable");
        if (!tlsCertExpiringEnabled) off.Add("TLS listener certificate expiring");
        if (!diskSpaceEnabled) off.Add("Low disk space");
        if (!portDownEnabled) off.Add("SMTP port connectivity failure");
        return off;
    }

    /// <summary>
    /// Builds the snapshot from the ConfigTool's in-memory document. Mirrors the service-side
    /// construction in <see cref="Reporting.ReportDataCollector"/> — when a rule gains an input,
    /// both producers must be extended.
    /// </summary>
    internal static RecommendationInput FromConfigDocument(ConfigDocument doc)
    {
        var enabledServers = doc.Servers.Where(s => s.Enabled).ToList();
        return new RecommendationInput
        {
            GraphConfigured = !string.IsNullOrWhiteSpace(doc.GraphApi.TenantId)
                           && !string.IsNullOrWhiteSpace(doc.GraphApi.ClientId),
            GraphUsesClientSecret = !string.IsNullOrWhiteSpace(doc.GraphApi.ClientSecret),
            GraphUsesCertificate = !string.IsNullOrWhiteSpace(doc.GraphApi.ClientCertificateThumbprint)
                                || !string.IsNullOrWhiteSpace(doc.GraphApi.ClientCertificateSubjectName),
            EnabledListenerCount = enabledServers.Count,
            HasTlsListener = enabledServers.Any(s => !s.Mode.Equals("Plain", StringComparison.OrdinalIgnoreCase)),
            PlaintextAuthListeners =
            [
                .. enabledServers
                    .Where(s => s.Mode.Equals("Plain", StringComparison.OrdinalIgnoreCase)
                             && !s.AuthMode.Equals("None", StringComparison.OrdinalIgnoreCase))
                    .Select(s => $"{s.Name} (port {s.Port})")
            ],
            SenderValidationEnabled = doc.SenderValidation.SvEnabled,
            BackupEnabled = doc.Backup.BackupEnabled,
            NdrEnabled = doc.Ndr.NdrEnabled,
            UpdateCheckEnabled = doc.Monitoring.UpdateCheckEnabled,
            TelemetryEnabled = doc.Monitoring.TelemetryEnabled,
            LogLevel = doc.Logging.DefaultLevel,
            HasAdminNotificationRecipients = doc.Notification.RecipientAddresses.Count > 0,
            AdminNotificationsEnabled = doc.Notification.NotifEnabled,
            DisabledCriticalNotifications = CollectDisabledCriticalNotifications(
                graphUsesCertificate: !string.IsNullOrWhiteSpace(doc.GraphApi.ClientCertificateThumbprint)
                                   || !string.IsNullOrWhiteSpace(doc.GraphApi.ClientCertificateSubjectName),
                graphCertExpiringEnabled: doc.Notification.NotifGraphCertExpiring,
                deliveryFailedEnabled: doc.Notification.NotifDeliveryFailed,
                graphDownEnabled: doc.Notification.NotifGraphDown,
                tlsCertExpiringEnabled: doc.Notification.NotifCertExpiring,
                diskSpaceEnabled: doc.Notification.NotifDiskSpace,
                portDownEnabled: doc.Notification.NotifPortDown),
        };
    }
}
