namespace GraphMailer.Service.Infrastructure.Backup;

/// <summary>
/// Metadata stored alongside the configuration inside a backup. Carries no secret material —
/// secrets live (decrypted) only in the bundled <c>graphmailer.json</c>, which is itself
/// inside the AES-GCM-encrypted container.
/// </summary>
internal sealed record BackupManifest
{
    /// <summary>Backup payload schema version (independent of the container format version).</summary>
    public int FormatVersion { get; init; } = 1;

    /// <summary>When the backup was created (UTC, ISO-8601).</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Machine the backup was created on (diagnostic only).</summary>
    public string? SourceMachine { get; init; }

    /// <summary>GraphMailer version that produced the backup (diagnostic only).</summary>
    public string? AppVersion { get; init; }
}
