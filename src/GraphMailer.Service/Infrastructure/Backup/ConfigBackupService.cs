using GraphMailer.Service.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMailer.Service.Infrastructure.Backup;

/// <summary>
/// Default <see cref="IConfigBackupService"/>: reads the live config (secrets decrypted),
/// packs it into a manifest + ZIP, and wraps it in a password-encrypted container. Restore
/// reverses this and re-encrypts the secrets with the local key via <see cref="ConfigService"/>.
/// </summary>
internal sealed class ConfigBackupService : IConfigBackupService
{
    internal const string BackupExtension = ".gmbak";

    private readonly IDataProtector _protector;
    private readonly string _configFilePath;

    /// <param name="protector">The config-purpose protector (same key ring as <see cref="ConfigService"/>).</param>
    /// <param name="configFilePath">Override for tests; defaults to <see cref="AppPaths.ConfigFilePath"/>.</param>
    internal ConfigBackupService(IDataProtector protector, string? configFilePath = null)
    {
        _protector = protector;
        _configFilePath = configFilePath ?? AppPaths.ConfigFilePath;
    }

    public byte[] BuildBackup(string password)
    {
        var config = new ConfigService(_configFilePath, _protector);
        var decryptedJson = config.ReadDecryptedJson();

        var manifest = new BackupManifest
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            SourceMachine = Environment.MachineName,
            AppVersion = typeof(ConfigBackupService).Assembly.GetName().Version?.ToString(),
        };

        var payload = BackupArchive.Build(manifest, decryptedJson);
        return BackupCrypto.Encrypt(payload, password);
    }

    public string WriteBackup(string password, string directory)
    {
        Directory.CreateDirectory(directory);
        var bytes = BuildBackup(password);
        var path = Path.Combine(directory, BackupFileName(DateTimeOffset.Now));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public BackupManifest ReadManifest(byte[] container, string password)
    {
        var payload = BackupCrypto.Decrypt(container, password);
        return BackupArchive.Read(payload).Manifest;
    }

    public void Restore(byte[] container, string password)
    {
        var payload = BackupCrypto.Decrypt(container, password);
        var (_, configJson) = BackupArchive.Read(payload);

        var config = new ConfigService(_configFilePath, _protector);
        var doc = config.LoadFromJson(configJson);   // bundled secrets are plaintext, accepted
        Directory.CreateDirectory(Path.GetDirectoryName(_configFilePath)!);
        config.Save(doc);                            // re-encrypts secrets with the local key
    }

    public int Rotate(string directory, int maxBackups)
    {
        if (maxBackups <= 0 || !Directory.Exists(directory))
            return 0;

        var stale = new DirectoryInfo(directory)
            .GetFiles("*" + BackupExtension)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(maxBackups)
            .ToList();

        var deleted = 0;
        foreach (var file in stale)
        {
            try { file.Delete(); deleted++; }
            catch (IOException) { /* locked/already gone — next run retries */ }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    public string BackupFileName(DateTimeOffset whenLocal)
        => $"graphmailer-backup-{whenLocal:yyyyMMdd-HHmmss}{BackupExtension}";
}
