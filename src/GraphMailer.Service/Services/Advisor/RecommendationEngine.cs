namespace GraphMailer.Service.Services.Advisor;

/// <summary>
/// The result of evaluating the catalog: every rule that is relevant to this installation, split
/// by state. The ConfigTool shows all three sections so an operator can see what was suggested and
/// already handled; the report email only ever sends <see cref="Open"/>.
/// </summary>
internal sealed record RecommendationSummary(
    IReadOnlyList<Recommendation> Open,
    IReadOnlyList<Recommendation> Done,
    IReadOnlyList<Recommendation> Dismissed)
{
    /// <summary>Every relevant rule, whatever its state — open first, then done, then hidden.</summary>
    internal IReadOnlyList<Recommendation> All => [.. Open, .. Done, .. Dismissed];
}

/// <summary>
/// The single catalog of operational hints, shared by the emailed operations report and the
/// ConfigTool's Recommendations page so both always agree on what is worth suggesting.
///
/// Rules are pure functions of a <see cref="RecommendationInput"/> snapshot and are evaluated
/// against the *persisted* configuration, never against half-edited form state.
///
/// Three conventions keep the list from becoming noise:
/// <list type="bullet">
///   <item>Every hint is informational. It never raises a health status and never blocks a save.</item>
///   <item>A rule that only makes sense on a finished install carries a <see cref="Rule.Relevant"/>
///         precondition (e.g. sender validation before Graph credentials exist). A rule whose
///         precondition fails is left out entirely — reporting it as "done" would be a lie, and
///         reporting it as open is advice about a setup step the operator has not reached.</item>
///   <item>A dismissal wins over "done", so the hidden section is exactly the operator's persisted
///         list and stays the one place to manage it.</item>
/// </list>
///
/// To add a hint: add a stable id to <see cref="RecommendationIds"/>, append a <see cref="Rule"/>
/// to <see cref="Catalog"/>, and add an open/done test pair in <c>RecommendationEngineTests</c>.
/// </summary>
internal static class RecommendationEngine
{
    /// <summary>
    /// One catalog entry. <see cref="Detail"/> is a factory because some rules describe the actual
    /// configured value (the log level names which way it deviates).
    /// </summary>
    private sealed record Rule(
        string Id,
        RecommendationSeverity Severity,
        RecommendationCategory Category,
        RecommendationTarget Target,
        string HelpPage,
        string Title,
        Func<RecommendationInput, bool> IsOpen,
        Func<RecommendationInput, string> Detail,
        string Impact,
        string DoneSummary,
        Func<RecommendationInput, bool>? Relevant = null);

    private static Rule Entry(
        string id,
        RecommendationSeverity severity,
        RecommendationCategory category,
        RecommendationTarget target,
        string helpPage,
        string title,
        Func<RecommendationInput, bool> isOpen,
        string detail,
        string impact,
        string doneSummary,
        Func<RecommendationInput, bool>? relevant = null)
        => new(id, severity, category, target, helpPage, title, isOpen, _ => detail, impact, doneSummary, relevant);

    /// <summary>
    /// The catalog. Display order is by severity first, then category — see <see cref="Evaluate"/>;
    /// entries written here in the order they should appear within one severity/category pair.
    /// </summary>
    private static readonly Rule[] Catalog =
    [
        // ── High ──────────────────────────────────────────────────────────────
        new Rule(RecommendationIds.TlsListener,
            RecommendationSeverity.High,
            RecommendationCategory.Security,
            RecommendationTarget.ServersAndTls,
            "configuration/servers-tls.html",
            "Enable TLS on the listeners that accept authentication",
            i => i.PlaintextAuthListeners.Count > 0,
            i => $"These listeners accept SMTP authentication but run in plain mode: {string.Join(", ", i.PlaintextAuthListeners)}. "
               + "Switching a listener to STARTTLS keeps its port and still serves clients that do not "
               + "upgrade, so this is usually a change of one setting rather than a new port.",
            "SMTP passwords travel the network in the clear and can be read by anyone able to observe "
            + "the traffic — with AUTH PLAIN or LOGIN they are barely encoded, not encrypted. A captured "
            + "relay password lets an attacker send mail as your organisation.",
            "Every listener that accepts authentication protects it with TLS.",
            // A listener that never sees a password has nothing to protect; without listeners at all
            // the install has a different problem.
            Relevant: i => i.EnabledListenerCount > 0),

        Entry(RecommendationIds.GraphClientCertificate,
            RecommendationSeverity.High,
            RecommendationCategory.Security,
            RecommendationTarget.GraphApi,
            "configuration/graph-api.html",
            "Authenticate to Graph with a certificate instead of a client secret",
            i => i.GraphUsesClientSecret && !i.GraphUsesCertificate,
            "The Entra app currently authenticates with a client secret. A client certificate is bound "
            + "to this machine's certificate store, can be rotated with both certs registered in Entra, "
            + "and never travels in the configuration file.",
            "A client secret expires on a date Entra picked, and when it lapses every message stops "
            + "going out until someone notices and issues a new one. It is also a reusable password "
            + "sitting in the config — anyone who obtains it can send mail as your tenant from anywhere.",
            "Graph authentication uses a client certificate.",
            // Before Graph is set up there is no auth method at all to have an opinion about.
            relevant: i => i.GraphConfigured),

        new Rule(RecommendationIds.CriticalNotifications,
            RecommendationSeverity.High,
            RecommendationCategory.Operations,
            RecommendationTarget.Notifications,
            "configuration/notifications.html",
            "Switch on the alerts that warn you before mail stops",
            i => i.DisabledCriticalNotifications.Count > 0,
            i => $"These early-warning alerts are switched off: {string.Join(", ", i.DisabledCriticalNotifications)}. "
               + "They are the ones that reach you while there is still time to act, as opposed to the "
               + "informational events (IP blocked, service start/stop, backup result) that report something "
               + "already over.",
            "Every one of these fails quietly. An expiring Graph client certificate is the worst case: when it "
            + "lapses, delivery stops completely and GraphMailer can no longer send email — not even to tell "
            + "you why. A full disk, a dead listener port or a Graph outage are equally invisible until "
            + "somebody reports that mail is missing.",
            "The early-warning alerts are all switched on.",
            // Only meaningful once notifications can actually be delivered — otherwise the
            // admin-notifications rule already covers it, and this one would just repeat it.
            Relevant: i => i.AdminNotificationsEnabled && i.HasAdminNotificationRecipients),

        // ── Medium ────────────────────────────────────────────────────────────
        Entry(RecommendationIds.SenderValidation,
            RecommendationSeverity.Medium,
            RecommendationCategory.Reliability,
            RecommendationTarget.AccessControl,
            "configuration/access-control.html",
            "Turn on sender validation",
            i => !i.SenderValidationEnabled,
            "Sender validation checks MAIL FROM against the tenant's users and aliases while the SMTP "
            + "session is still open, so a bad address is refused with an SMTP error the sending "
            + "application can see and log.",
            "Without it a message from an address that does not exist in the tenant is accepted, queued, "
            + "and only fails at the Graph API — long after the sending application was told the mail was "
            + "taken. A typo in a scheduled job's sender address then loses mail silently for days.",
            "Sender validation is on — unknown senders are rejected during the SMTP session.",
            // Resolving the tenant's senders needs Graph (User.Read.All).
            relevant: i => i.GraphConfigured),

        Entry(RecommendationIds.ConfigBackup,
            RecommendationSeverity.Medium,
            RecommendationCategory.Reliability,
            RecommendationTarget.BackupAndRestore,
            "configuration/backup-restore.html",
            "Enable automatic configuration backups",
            i => !i.BackupEnabled,
            "Scheduled backups write a password-protected archive of the whole configuration on a fixed "
            + "rhythm, to a directory you choose and optionally by email.",
            "The configuration holds the Entra registration, every listener, all filter rules and the "
            + "SMTP users with their passwords. Without a backup, a failed disk or a bad edit means "
            + "reconstructing all of it by hand — including credentials nobody wrote down.",
            "Scheduled configuration backups are on."),

        Entry(RecommendationIds.Ndr,
            RecommendationSeverity.Medium,
            RecommendationCategory.Reliability,
            RecommendationTarget.Notifications,
            "configuration/notifications.html",
            "Send non-delivery reports",
            i => !i.NdrEnabled,
            "With NDRs enabled the sender — and optionally the admin — receives a bounce naming the "
            + "reason when a message is given up on, the same way a normal mail server reports it.",
            "A message that cannot be delivered is currently moved to mail\\failed\\ without a word. The "
            + "sending application believes the mail went out, so an invoice or an alert that never "
            + "arrived is only discovered when somebody asks about it.",
            "Non-delivery reports are on — senders learn when a message could not be delivered."),

        Entry(RecommendationIds.AdminNotifications,
            RecommendationSeverity.Medium,
            RecommendationCategory.Operations,
            RecommendationTarget.Notifications,
            "configuration/notifications.html",
            "Add a recipient for admin notifications",
            i => !i.HasAdminNotificationRecipients,
            "One mailbox is enough — a distribution list works. GraphMailer then emails the alerts it "
            + "already produces, using the sender address configured on the same page.",
            "Alerts about an expiring TLS certificate, a listener that stopped accepting connections, "
            + "low disk space or a Graph API outage currently reach the log file only. Nobody reads a "
            + "log file at three in the morning, so the first sign of trouble is a user complaint.",
            "Admin notifications have at least one recipient."),

        Entry(RecommendationIds.UpdateCheck,
            RecommendationSeverity.Medium,
            RecommendationCategory.Operations,
            RecommendationTarget.Monitoring,
            "configuration/monitoring.html",
            "Turn on the update check",
            i => !i.UpdateCheckEnabled,
            "Once a week the service asks github.com whether a newer GraphMailer version exists and "
            + "shows the result on the Status page — nothing else is transmitted.",
            "Security fixes and new releases go unnoticed on this machine. A relay that reaches the "
            + "internet and accepts SMTP from your network is worth keeping patched, and nothing else "
            + "will tell you a fix has shipped.",
            "The weekly update check is on."),

        // ── Low ───────────────────────────────────────────────────────────────
        new Rule(RecommendationIds.LogLevel,
            RecommendationSeverity.Low,
            RecommendationCategory.Operations,
            RecommendationTarget.Monitoring,
            "configuration/monitoring.html",
            "Set the log level back to Information",
            i => !string.Equals(i.LogLevel, RecommendationInput.RecommendedLogLevel, StringComparison.OrdinalIgnoreCase),
            i => DescribeLogLevel(i.LogLevel),
            "Information is the level the rest of the documentation and the troubleshooting steps assume. "
            + "The level applies immediately — no service restart needed.",
            "The log level is Information — business events without the per-request noise."),

        Entry(RecommendationIds.Telemetry,
            RecommendationSeverity.Low,
            RecommendationCategory.Product,
            RecommendationTarget.Monitoring,
            "configuration/monitoring.html",
            "Consider sharing anonymous usage telemetry",
            i => !i.TelemetryEnabled,
            "One daily heartbeat (random install id, version, OS/runtime, aggregated mail counters) and "
            + "PII-free error reports. Email addresses, IP addresses, hostnames and message content are "
            + "never transmitted.",
            "Nothing here affects your installation — it helps decide which platforms and failure modes "
            + "get attention in future releases, based on real deployments rather than guesswork.",
            "Anonymous usage telemetry is on — thank you."),
    ];

    /// <summary>
    /// Evaluates the whole catalog against <paramref name="input"/> and splits it into open,
    /// already-satisfied and operator-hidden suggestions. Rules whose precondition does not hold
    /// are omitted from all three lists.
    ///
    /// Unknown ids in <paramref name="dismissedIds"/> are ignored — a config written by a newer
    /// build (or a rule that was removed) must never break the evaluation.
    /// </summary>
    internal static RecommendationSummary Evaluate(RecommendationInput input, IEnumerable<string>? dismissedIds = null)
    {
        var dismissed = new HashSet<string>(dismissedIds ?? [], StringComparer.OrdinalIgnoreCase);
        List<Recommendation> open = [], done = [], hidden = [];

        foreach (var rule in Catalog)
        {
            if (rule.Relevant is { } relevant && !relevant(input))
                continue;

            var isOpen = rule.IsOpen(input);
            var state = dismissed.Contains(rule.Id) ? RecommendationState.Dismissed
                      : isOpen ? RecommendationState.Open
                      : RecommendationState.Done;

            var item = new Recommendation(
                rule.Id, rule.Severity, rule.Category, rule.Title, rule.Detail(input),
                rule.Impact, rule.Target, rule.HelpPage, state, rule.DoneSummary);

            (state switch
            {
                RecommendationState.Open => open,
                RecommendationState.Done => done,
                _ => hidden,
            }).Add(item);
        }

        // Stable sort: severity first so the most consequential advice is read first, then
        // category; catalog order is preserved within each pair.
        return new RecommendationSummary(Order(open), Order(done), Order(hidden));
    }

    private static IReadOnlyList<Recommendation> Order(List<Recommendation> items)
        => [.. items.OrderBy(r => r.Severity).ThenBy(r => r.Category)];

    /// <summary>
    /// The log-level hint fires in both directions, and the reason differs: a level below
    /// Information floods the disk with per-request detail, a level above it hides the business
    /// events and policy rejections an operator needs. Name the actual case.
    /// </summary>
    private static string DescribeLogLevel(string level)
    {
        var isVerbose = level.Equals("Verbose", StringComparison.OrdinalIgnoreCase)
                     || level.Equals("Debug", StringComparison.OrdinalIgnoreCase);

        return isVerbose
            ? $"The log level is currently {level}, which records every connection, filter decision and "
              + "health probe. That is right while tracking down a problem, but it fills the log directory "
              + "quickly and buries the entries that matter."
            : $"The log level is currently {level}, so delivered messages, authentications and policy "
              + "rejections are never written. When something needs investigating afterwards, that history "
              + "is missing.";
    }
}
