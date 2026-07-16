namespace GraphMailer.Service.Configuration;

public enum BackupFrequency
{
    Daily,
    Weekly,
}

public sealed class BackupEmailOptions
{
    /// <summary>Email each newly created scheduled backup to <see cref="Recipients"/>.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Recipients for emailed backups (independent of admin-notification recipients).</summary>
    public List<string> Recipients { get; init; } = [];
}

/// <summary>
/// Scheduled, password-encrypted configuration backups. The <see cref="Password"/> is stored
/// as <c>ENC[…]</c> in graphmailer.json and used by the service for unattended backups; the
/// operator must remember it to restore (the backup is portable and key-ring-independent).
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Enable scheduled automatic backups.</summary>
    public bool Enabled { get; init; } = false;

    public BackupFrequency Frequency { get; init; } = BackupFrequency.Weekly;

    /// <summary>Local time of day to run, "HH:mm" (24h).</summary>
    public string TimeOfDay { get; init; } = "03:00";

    /// <summary>Day to run on when <see cref="Frequency"/> is <see cref="BackupFrequency.Weekly"/>.</summary>
    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Sunday;

    /// <summary>Maximum number of backups to retain; older ones are deleted (rotation).</summary>
    public int MaxBackups { get; init; } = 14;

    /// <summary>Target directory; null/empty → <c>%ProgramData%\GraphMailer\backups</c>.</summary>
    public string? Directory { get; init; }

    /// <summary>Sensitive – stored as <c>ENC[…]</c>. Required when <see cref="Enabled"/> is true.</summary>
    public string? Password { get; init; }

    public BackupEmailOptions Email { get; init; } = new();
}
