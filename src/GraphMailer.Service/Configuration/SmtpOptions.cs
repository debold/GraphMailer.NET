namespace GraphMailer.Service.Configuration;

public sealed class SmtpServerEntry
{
    /// <summary>When false, the listener is defined but not started. Defaults to true.</summary>
    public bool Enabled { get; init; } = true;

    public string Name { get; init; } = "SMTP";
    public int Port { get; init; } = 2525;

    /// <summary>Plain, StartTls, or Ssl</summary>
    public string Mode { get; init; } = "Plain";
    public bool AuthRequired { get; init; } = false;

    /// <summary>
    /// "None", "Optional" or "Required". <see cref="AuthRequired"/> is the listener's behaviour flag
    /// (derived from this on save); this distinguishes "authentication is offered" from "no
    /// authentication at all", which the recommendation engine needs to judge plaintext listeners.
    /// Older configs without the key fall back to the <see cref="AuthRequired"/> reading.
    /// </summary>
    public string AuthMode { get; init; } = "Optional";

    /// <summary>True when this listener accepts SMTP credentials at all.</summary>
    public bool AcceptsCredentials =>
        !AuthMode.Equals("None", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this listener never upgrades to TLS.</summary>
    public bool IsPlaintext =>
        Mode.Equals("Plain", StringComparison.OrdinalIgnoreCase);
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>
    /// Maximum message size in bytes accepted during the SMTP DATA phase.
    /// Default: 25 MB – a conservative value below Exchange Online's
    /// organisational default receive limit (~35 MB, configurable up to 150 MB).
    ///
    /// Delivery constraints for Phase 3 (Graph API queue processor):
    ///   ≤ ~3 MB raw  → Graph API /me/sendMail  (single POST, 4 MB request limit)
    ///   > ~3 MB raw  → Graph API createUploadSession  (streaming, up to 150 MB)
    ///
    /// Setting this above 150 MB (157 286 400 bytes) makes messages permanently
    /// undeliverable via Microsoft Graph API – SmtpOptionsValidator warns at startup.
    /// </summary>
    public long MaxSizeBytes { get; init; } = 26_214_400;

    /// <summary>
    /// Maximum number of envelope recipients accepted for a single message.
    /// Default: 500 – the Exchange Online default for a mailbox's <c>RecipientLimits</c>,
    /// which an administrator can change per mailbox (Microsoft allows 1–1000).
    ///
    /// The value cannot be discovered at runtime: no Microsoft Graph permission the service
    /// holds (Mail.Send, Mail.ReadWrite, User.Read.All) exposes it, and reading it would
    /// require tenant-wide Exchange administration rights. It is therefore configured, and
    /// <c>SmtpOptionsValidator</c> keeps it inside Microsoft's range.
    ///
    /// Set too low, GraphMailer rejects mail Exchange would have accepted; set too high,
    /// Exchange rejects it with <c>ErrorRecipientLimitExceeded</c> and the sender gets an NDR
    /// – so erring on the high side is the safer mistake.
    /// </summary>
    public int MaxRecipients { get; init; } = 500;

    public string Banner { get; init; } = "GraphMailer";
}
