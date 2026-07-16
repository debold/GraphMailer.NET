namespace GraphMailer.Service.Infrastructure.Backup;

/// <summary>
/// Creates and restores portable, password-encrypted configuration backups.
/// Shared by the service (scheduled backups) and the ConfigTool (manual backup / restore).
/// </summary>
internal interface IConfigBackupService
{
    /// <summary>Builds an encrypted backup of the current configuration (in memory).</summary>
    byte[] BuildBackup(string password);

    /// <summary>Builds a backup and writes it to <paramref name="directory"/>; returns the file path.</summary>
    string WriteBackup(string password, string directory);

    /// <summary>Decrypts a backup and returns its manifest (also validates the password).</summary>
    /// <exception cref="BackupDecryptionException">Wrong password or corrupt file.</exception>
    /// <exception cref="BackupFormatException">Not a GraphMailer backup.</exception>
    BackupManifest ReadManifest(byte[] container, string password);

    /// <summary>
    /// Restores a backup over the current configuration: the bundled secrets are re-encrypted
    /// with the local Data Protection key. Overwrites <c>graphmailer.json</c>.
    /// </summary>
    void Restore(byte[] container, string password);

    /// <summary>Deletes the oldest backups in <paramref name="directory"/> beyond
    /// <paramref name="maxBackups"/>; returns how many were deleted.</summary>
    int Rotate(string directory, int maxBackups);

    /// <summary>Standard backup file name for a given local timestamp.</summary>
    string BackupFileName(DateTimeOffset whenLocal);
}
