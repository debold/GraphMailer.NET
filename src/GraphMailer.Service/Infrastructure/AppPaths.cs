namespace GraphMailer.Service.Infrastructure;

/// <summary>
/// Centralises all fixed filesystem paths used by the service and the config tool.
///
/// Fixed paths (always under BaseDir, not user-configurable):
///   BaseDir\config\graphmailer.json  – user-writable configuration file
///   BaseDir\config\keys\             – DataProtection fallback key storage
///   BaseDir\logs\                    – rolling log files
///   BaseDir\data\                    – SQLite metrics database
///
/// Configurable paths (user can override in graphmailer.json):
///   mail\queue\, mail\sent\, mail\failed\  – mail queue subdirectories
///   (see <see cref="MailDir"/>; individual dirs use this as their parent)
///
/// Production base: <c>%ProgramData%\GraphMailer</c>
/// Override: set the environment variable <c>GRAPHMAILER_DATA_DIR</c> to a custom path.
///           This is intended for development and integration tests only.
/// </summary>
internal static class AppPaths
{
    private static readonly string _baseDir = ResolveBaseDir();

    /// <summary>Root data directory: %ProgramData%\GraphMailer (or GRAPHMAILER_DATA_DIR).</summary>
    internal static string BaseDir => _baseDir;

    /// <summary>Directory that contains <c>graphmailer.json</c> and the DataProtection key fallback.</summary>
    internal static string ConfigDir => Path.Combine(_baseDir, "config");

    /// <summary>Full path to the user-writable configuration file.</summary>
    internal static string ConfigFilePath => Path.Combine(ConfigDir, "graphmailer.json");

    /// <summary>Directory for rolling Serilog log files.</summary>
    internal static string LogsDir => Path.Combine(_baseDir, "logs");

    /// <summary>Directory for the SQLite metrics database.</summary>
    internal static string DataDir => Path.Combine(_baseDir, "data");

    /// <summary>DataProtection fallback key directory (used when the Registry is unavailable).</summary>
    internal static string KeysDir => Path.Combine(ConfigDir, "keys");

    /// <summary>Default parent directory for mail queue subdirectories.</summary>
    internal static string MailDir => Path.Combine(_baseDir, "mail");

    /// <summary>Default directory for encrypted configuration backups (*.gmbak).</summary>
    internal static string BackupsDir => Path.Combine(_baseDir, "backups");

    private static string ResolveBaseDir()
    {
        var envOverride = Environment.GetEnvironmentVariable("GRAPHMAILER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GraphMailer");
    }
}
