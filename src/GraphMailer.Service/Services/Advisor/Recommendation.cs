namespace GraphMailer.Service.Services.Advisor;

/// <summary>
/// Grouping of a <see cref="Recommendation"/>, also its display order: an operator should
/// see the security hints before the "nice to have" ones. Never a severity — every
/// recommendation is informational and must not influence any health status.
/// </summary>
internal enum RecommendationCategory
{
    Security,
    Reliability,
    Operations,
    Product,
}

/// <summary>
/// The ConfigTool screen that fixes a recommendation. The ConfigTool maps this to its
/// navigation; the report renderer prints the page name so an email reader knows where to go.
/// </summary>
internal enum RecommendationTarget
{
    ServersAndTls,
    AccessControl,
    GraphApi,
    MailQueue,
    Monitoring,
    Notifications,
    BackupAndRestore,
}

/// <summary>
/// Stable identifiers of the built-in recommendations. These strings are persisted in
/// <c>graphmailer.json</c> under <c>Recommendations.Dismissed</c>, so renaming one silently
/// resurrects a hint the operator already dismissed — treat them as part of the config schema
/// and add a migration if one ever has to change.
/// </summary>
internal static class RecommendationIds
{
    internal const string GraphClientCertificate = "graph-client-certificate";
    internal const string TlsListener = "tls-listener";
    internal const string SenderValidation = "sender-validation";
    internal const string ConfigBackup = "config-backup";
    internal const string Ndr = "ndr";
    internal const string LogLevel = "log-level";
    internal const string AdminNotifications = "admin-notifications";
    internal const string UpdateCheck = "update-check";
    internal const string Telemetry = "telemetry";
}

/// <summary>
/// How much the suggestion matters, and the primary sort key so the most consequential advice is
/// read first. Not a health severity — even <see cref="High"/> describes a risk the operator may
/// knowingly accept, never a fault in the running service.
/// </summary>
internal enum RecommendationSeverity
{
    /// <summary>Credentials or secrets are exposed, or a foreseeable outage is being set up.</summary>
    High,
    /// <summary>A failure would go unnoticed, or recovery would be markedly harder.</summary>
    Medium,
    /// <summary>Hygiene and optional extras — worth doing, nothing breaks without it.</summary>
    Low,
}

/// <summary>
/// Where a <see cref="Recommendation"/> stands for the current configuration.
/// Every rule in the catalog reports one of these, so the ConfigTool can show what was
/// suggested and already handled instead of silently dropping it.
/// </summary>
internal enum RecommendationState
{
    /// <summary>The suggestion applies and has not been acted on.</summary>
    Open,
    /// <summary>The configuration already satisfies the suggestion.</summary>
    Done,
    /// <summary>The operator hid the suggestion; it is not shown as open or reported by email.</summary>
    Dismissed,
}

/// <summary>
/// One hint about an optional feature that is switched off (or a setting that is likely not
/// what the operator wants). Informational only — never a health finding, so it must not
/// affect the report's severity.
/// </summary>
/// <param name="Id">Stable key; persisted when the operator dismisses the hint.</param>
/// <param name="Severity">How much it matters; the primary sort key.</param>
/// <param name="Category">Which area of the product it belongs to; a label, and the secondary sort key.</param>
/// <param name="Title">One short imperative line ("Turn on the update check").</param>
/// <param name="Detail">What the setting does and what the current configuration means.</param>
/// <param name="Impact">
/// Why acting on it is worth it: the concrete consequence of leaving it as it is. Kept separate
/// from <paramref name="Detail"/> so an operator weighing the change sees the argument on its own
/// rather than buried in a description — and so <paramref name="Severity"/> is justified in words.
/// </param>
/// <param name="Target">ConfigTool screen that holds the matching setting.</param>
/// <param name="HelpPage">Help page (relative to the bundled <c>help\</c> folder) documenting it.</param>
/// <param name="State">Whether the suggestion is open, already satisfied, or hidden.</param>
/// <param name="DoneSummary">
/// One short sentence describing the satisfied state ("Anonymous usage telemetry is on"), shown
/// instead of <paramref name="Detail"/> once the suggestion is handled — the "why you should"
/// wording reads wrong for something that is already done.
/// </param>
internal sealed record Recommendation(
    string Id,
    RecommendationSeverity Severity,
    RecommendationCategory Category,
    string Title,
    string Detail,
    string Impact,
    RecommendationTarget Target,
    string HelpPage,
    RecommendationState State,
    string DoneSummary)
{
    /// <summary>Label for <see cref="Severity"/> as shown in the UI and the report email.</summary>
    internal string SeverityLabel => Severity switch
    {
        RecommendationSeverity.High => "High",
        RecommendationSeverity.Medium => "Medium",
        RecommendationSeverity.Low => "Low",
        _ => Severity.ToString(),
    };

    /// <summary>Human-readable name of <see cref="Target"/>, as shown in the ConfigTool sidebar.</summary>
    internal string TargetPageName => Target switch
    {
        RecommendationTarget.ServersAndTls => "Servers & TLS",
        RecommendationTarget.AccessControl => "Access Control",
        RecommendationTarget.GraphApi => "Graph API",
        RecommendationTarget.MailQueue => "Mail Queue",
        RecommendationTarget.Monitoring => "Monitoring",
        RecommendationTarget.Notifications => "Notifications",
        RecommendationTarget.BackupAndRestore => "Backup & Restore",
        _ => Target.ToString(),
    };
}
