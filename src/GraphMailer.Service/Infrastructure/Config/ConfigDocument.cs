using System.Text.Json.Nodes;

namespace GraphMailer.Service.Infrastructure.Config;

/// <summary>
/// Mutable in-memory model of <c>config\graphmailer.json</c>.
/// All sections default to safe values when the file is absent or a section is missing.
/// Passed between the ConfigTool pages and <see cref="ConfigService"/> for load/save.
/// </summary>
internal sealed class ConfigDocument
{
    /// <summary>On-disk config schema version (see <see cref="ConfigSchema"/>); 0 for pre-versioning files.</summary>
    public int SchemaVersion { get; set; } = ConfigSchema.Current;

    public GraphApiSection GraphApi { get; set; } = new();
    public SmtpSection Smtp { get; set; } = new();
    public CertSection Certificate { get; set; } = new();
    public MailQueueSection MailQueue { get; set; } = new();
    public AccessSection Access { get; set; } = new();
    public List<ServerEntry> Servers { get; set; } = [];
    public IpBlockingSection IpBlocking { get; set; } = new();
    public MonitoringSection Monitoring { get; set; } = new();
    public MetricsSection Metrics { get; set; } = new();
    public NotificationSection Notification { get; set; } = new();
    public NdrSection Ndr { get; set; } = new();
    public SenderValidationSection SenderValidation { get; set; } = new();
    public LoggingSection Logging { get; set; } = new();
    public BackupSection Backup { get; set; } = new();
    public RecommendationsSection Recommendations { get; set; } = new();

    /// <summary>
    /// The original parsed document. Preserved on load so <see cref="ConfigService.Save"/>
    /// can carry forward any unknown top-level keys written by future versions.
    /// Null when the config file did not exist at load time (fresh defaults).
    /// </summary>
    internal JsonObject? RawSource { get; init; }

    /// <summary>
    /// JSON paths of <c>ENC[…]</c> values that could not be decrypted on load
    /// (e.g. <c>GraphApi.ClientSecret</c>, <c>Users[0].Password</c>). The affected fields
    /// are left blank so the rest of the configuration still loads; the caller surfaces
    /// these to the operator. Empty when everything decrypted.
    /// </summary>
    internal IReadOnlyList<string> DecryptionFailures { get; init; } = [];

    // ── Section models ─────────────────────────────────────────────────────

    internal sealed class GraphApiSection
    {
        public string? TenantId { get; set; }
        public string? ClientId { get; set; }
        /// <summary>Sensitive – stored as <c>ENC[…]</c> in the file.</summary>
        public string? ClientSecret { get; set; }
        public string? ClientCertificateThumbprint { get; set; }
        public string? ClientCertificateSubjectName { get; set; }
        public string? ClientCertificateIssuer { get; set; }
    }

    internal sealed class SmtpSection
    {
        public long MaxSizeBytes { get; set; } = 26_214_400;
        public string Banner { get; set; } = "GraphMailer";
    }

    internal sealed class CertSection
    {
        public string StoreLocation { get; set; } = "LocalMachine";
        public string StoreName { get; set; } = "My";
        public string? SubjectName { get; set; }
        public string? Issuer { get; set; }
        /// <summary>TLS listeners without a certificate are not started (no plain fallback).</summary>
        public bool FailClosed { get; set; }
    }

    internal sealed class MailQueueSection
    {
        public string MailDir { get; set; } = string.Empty;
        public int PollingIntervalSeconds { get; set; } = 5;
        public int TransientRetryCount { get; set; } = 6;
        public int TransientRetryIntervalSeconds { get; set; } = 300;
        public int RetryIntervalSeconds { get; set; } = 900;
        public int MessageExpirationHours { get; set; } = 24;
        public int BatchSize { get; set; } = 10;
        public bool ArchiveSentEmails { get; set; }
        public int SentEmailRetentionDays { get; set; } = 7;
        /// <summary>Days failed mail is kept in mail/failed/; 0 = keep forever.</summary>
        public int FailedEmailRetentionDays { get; set; } = 60;
    }

    internal sealed class AccessSection
    {
        public List<string> IpWhitelist { get; set; } = [];
        public Dictionary<string, string> IpWhitelistComments { get; set; } = new();
        public List<string> IpBlacklist { get; set; } = [];
        public Dictionary<string, string> IpBlacklistComments { get; set; } = new();
        public List<string> AllowedSenders { get; set; } = [];
        public List<string> BlockedSenders { get; set; } = [];
        public List<string> AllowedRecipients { get; set; } = [];
        public List<string> BlockedRecipients { get; set; } = [];
        public List<UserEntry> Users { get; set; } = [];
    }

    internal sealed class UserEntry
    {
        public bool Enabled { get; set; } = true;
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>Sensitive – stored as <c>ENC[…]</c> in the file.</summary>
        public string? Password { get; set; }
        /// <summary>
        /// When true, the next successful SMTP AUTH for this user will capture and
        /// persist the supplied password. Cleared automatically after first capture.
        /// </summary>
        public bool CaptureNextPassword { get; set; }
        public List<string> FromRestrictions { get; set; } = [];
    }

    internal sealed class ServerEntry
    {
        public bool Enabled { get; set; } = true;
        public string Name { get; set; } = "SMTP";
        public int Port { get; set; } = 2525;
        public string Mode { get; set; } = "Plain";
        public string AuthMode { get; set; } = "Optional";
    }

    internal sealed class IpBlockingSection
    {
        public int FailureThreshold { get; set; } = 10;
        public int TimeframeSeconds { get; set; } = 600;
        public int BlockDurationSeconds { get; set; } = 600;
    }

    internal sealed class MonitoringSection
    {
        public int CertWarnDays { get; set; } = 14;
        public int DiskWarnPct { get; set; } = 10;
        public int PortCheckIntervalMinutes { get; set; } = 5;
        public int GraphCheckIntervalMinutes { get; set; } = 15;
        /// <summary>Opt-in weekly GitHub release check (queries api.github.com).</summary>
        public bool UpdateCheckEnabled { get; set; } = false;
        /// <summary>Opt-in anonymous usage telemetry (daily heartbeat + PII-free error reports).</summary>
        public bool TelemetryEnabled { get; set; } = false;
    }

    internal sealed class MetricsSection
    {
        public bool Enabled { get; set; } = true;
        public int RetentionDays { get; set; } = 90;
        public int CleanupIntervalHours { get; set; } = 24;
        public bool PerfMetricsEnabled { get; set; } = true;
        public int PerfMemoryIntervalSeconds { get; set; } = 60;
        public int PerfCpuIntervalSeconds { get; set; } = 60;
        public int PerfDiskIntervalSeconds { get; set; } = 300;
    }

    internal sealed class NdrSection
    {
        public bool NdrEnabled { get; set; } = false;
        public bool NdrNotifySender { get; set; } = true;
        public bool NdrNotifyAdmin { get; set; } = false;
    }

    internal sealed class SenderValidationSection
    {
        public bool SvEnabled { get; set; } = false;
        public int SvRefreshIntervalMinutes { get; set; } = 60;
        public bool SvFailClosed { get; set; } = false;
    }

    internal sealed class LoggingSection
    {
        public string DefaultLevel { get; set; } = "Information";
    }

    internal sealed class RecommendationsSection
    {
        /// <summary>
        /// Stable ids of recommendation hints the operator has permanently hidden
        /// (see <c>RecommendationIds</c>). Empty means every applicable hint is shown.
        /// </summary>
        public List<string> Dismissed { get; set; } = [];
    }

    internal sealed class BackupSection
    {
        public bool BackupEnabled { get; set; } = false;
        /// <summary>"Daily" or "Weekly".</summary>
        public string Frequency { get; set; } = "Weekly";
        public string TimeOfDay { get; set; } = "03:00";
        /// <summary>"Sunday".."Saturday" (weekly only).</summary>
        public string DayOfWeek { get; set; } = "Sunday";
        public int MaxBackups { get; set; } = 14;
        /// <summary>Null/empty → default backups directory.</summary>
        public string? Directory { get; set; }
        /// <summary>Sensitive – stored as ENC[…].</summary>
        public string? Password { get; set; }
        public bool EmailEnabled { get; set; } = false;
        public List<string> EmailRecipients { get; set; } = [];
    }

    internal sealed class NotificationSection
    {
        /// <summary>
        /// Master switch for all admin notifications. When false nothing is sent, regardless of the
        /// per-event flags below — which keep their values so the setup survives a temporary silence.
        /// </summary>
        public bool NotifEnabled { get; set; }

        public List<string> RecipientAddresses { get; set; } = [];
        public string? NotifFrom { get; set; }
        public string SubjectPrefix { get; set; } = "[GraphMailer]";
        public bool NotifIpBlocked { get; set; } = true;
        public bool NotifDeliveryFailed { get; set; } = true;
        public bool NotifCertExpiring { get; set; } = true;
        public bool NotifCertExpired { get; set; } = true;
        public bool NotifDiskSpace { get; set; } = true;
        public bool NotifGraphDown { get; set; } = true;
        public bool NotifPortDown { get; set; } = true;
        public bool NotifServiceStartStop { get; set; } = false;
        public bool NotifBackup { get; set; } = true;
        public bool NotifUpdateAvailable { get; set; } = false;

        // ── Periodic operations report (sent to RecipientAddresses above) ──
        public bool ReportEnabled { get; set; } = false;
        /// <summary>"Weekly" or "Monthly".</summary>
        public string ReportFrequency { get; set; } = "Weekly";
        public string ReportTimeOfDay { get; set; } = "07:00";
        /// <summary>"Sunday".."Saturday" (weekly only).</summary>
        public string ReportDayOfWeek { get; set; } = "Monday";
        /// <summary>1–28 (monthly only).</summary>
        public int ReportDayOfMonth { get; set; } = 1;
    }
}
