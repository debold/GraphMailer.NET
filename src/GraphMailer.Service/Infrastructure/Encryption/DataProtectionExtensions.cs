using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Service.Infrastructure.Encryption;

internal static class DataProtectionExtensions
{
    internal const string ApplicationName = "GraphMailer";
    internal const string ConfigPurpose = "GraphMailer.Configuration.v1";

    private const string RegistryKeyPath = @"SOFTWARE\GraphMailer\DataProtection";

    /// <summary>
    /// Configures Data Protection key persistence:
    ///   - HKLM\SOFTWARE\GraphMailer\DataProtection when running with sufficient permissions (service/admin)
    ///   - config/keys/ directory as fallback (development / insufficient permissions)
    /// </summary>
    internal static IDataProtectionBuilder PersistToRegistryOrFallback(
        this IDataProtectionBuilder builder)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var regKey = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true)
                          ?? Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true);

                if (regKey is not null)
                    // Protect the keys with MACHINE-wide DPAPI (not the default per-user scope) so the
                    // SYSTEM service and the elevated (admin-user) ConfigTool can both read the same ring.
                    // Without this, a key written by one identity is undecryptable by the other and the
                    // service crashes at startup with a CryptographicException.
                    return builder.PersistKeysToRegistry(regKey)
                                  .ProtectKeysWithDpapi(protectToLocalMachine: true);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException
                   or System.Security.SecurityException
                   or IOException)
            {
                // Insufficient permission (non-admin) or the key is locked – fall through
                // to file keys. Loud on purpose: silently switching rings makes every
                // existing ENC[...] secret undecryptable, and the operator must know WHY
                // decryption fails (SecretIntegrityCheckService only reports THAT it does).
                Serilog.Log.Error(ex,
                    "[DataProtection] Cannot access the registry key ring (HKLM\\SOFTWARE\\GraphMailer\\DataProtection) — " +
                    "falling back to file-based keys under config\\keys. Secrets encrypted with the registry ring " +
                    "will NOT decrypt in this mode; run elevated (or as the service) to use the registry ring.");
            }
        }

        var keysDir = new DirectoryInfo(AppPaths.KeysDir);
        keysDir.Create();

        return builder.PersistKeysToFileSystem(keysDir);
    }

    // The ServiceProvider backing a standalone protector must OUTLIVE the protector:
    // Protect()/Unprotect() resolve key-ring services lazily from it. Disposing it (a
    // previous `using`) made later Protect() calls throw
    // "An error occurred while trying to encrypt the provided data"
    // (inner: ObjectDisposedException) — the ConfigTool could load config but not save.
    // These providers are process-lifetime by design (service startup + ConfigTool),
    // so they are rooted here and released only at process exit.
    private static readonly List<ServiceProvider> RootedProviders = [];

    /// <summary>
    /// Builds a standalone IDataProtector for use during configuration loading,
    /// before the main DI container is available. Uses the same key ring as the
    /// protector registered in DI. The backing ServiceProvider is kept alive for the
    /// process lifetime — the returned protector depends on it for every operation.
    /// </summary>
    internal static IDataProtector BuildConfigProtector()
    {
        var services = new ServiceCollection();

        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistToRegistryOrFallback();

        var sp = services.BuildServiceProvider();
        lock (RootedProviders) RootedProviders.Add(sp);

        return sp.GetRequiredService<IDataProtectionProvider>()
                 .CreateProtector(ConfigPurpose);
    }
}
