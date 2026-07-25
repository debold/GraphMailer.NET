using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace GraphMailer.Service.Infrastructure.Encryption;

/// <summary>
/// Thrown when the shared Data Protection key ring in
/// <c>HKLM\SOFTWARE\GraphMailer\DataProtection</c> cannot be accessed — almost always because the
/// process is not elevated. There is no file-based fallback (see
/// <see cref="DataProtectionExtensions.PersistKeysToSharedRegistry"/>): the service fails startup
/// and the ConfigTool surfaces a clear message instead of silently encrypting with a divergent key.
/// </summary>
internal sealed class KeyRingUnavailableException(string message, Exception? inner)
    : Exception(message, inner);

internal static class DataProtectionExtensions
{
    internal const string ApplicationName = "GraphMailer";
    internal const string ConfigPurpose = "GraphMailer.Configuration.v1";

    internal const string RegistryKeyPath = @"SOFTWARE\GraphMailer\DataProtection";

    /// <summary>
    /// Persists the Data Protection key ring in <c>HKLM\SOFTWARE\GraphMailer\DataProtection</c>,
    /// protected with MACHINE-wide DPAPI so the SYSTEM service and the elevated (admin-user)
    /// ConfigTool share one ring — a key written by one identity must decrypt for the other.
    ///
    /// There is deliberately NO file-based fallback. Both real processes always reach HKLM in
    /// production (the service runs as SYSTEM; the ConfigTool's manifest forces elevation), so an
    /// unreachable registry ring is never a normal state — it only ever happens for a non-elevated
    /// run. Falling back to a throwaway file ring in that case would silently encrypt secrets with a
    /// key the other process cannot decrypt, corrupting the shared <c>graphmailer.json</c>. Failing
    /// fast is strictly safer: callers surface the actionable error instead of diverging in silence.
    /// </summary>
    /// <exception cref="KeyRingUnavailableException">
    /// The shared registry key ring cannot be opened or created (e.g. the process is not elevated).
    /// </exception>
    internal static IDataProtectionBuilder PersistKeysToSharedRegistry(
        this IDataProtectionBuilder builder)
    {
        if (!OperatingSystem.IsWindows())
            throw new KeyRingUnavailableException(
                "GraphMailer's Data Protection key ring is Windows-only (registry-backed).", null);

        try
        {
            var regKey = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true)
                      ?? Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true)
                      ?? throw new KeyRingUnavailableException(
                             $"Could not open or create HKLM\\{RegistryKeyPath}.", null);

            return builder.PersistKeysToRegistry(regKey)
                          .ProtectKeysWithDpapi(protectToLocalMachine: true);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
               or System.Security.SecurityException
               or IOException)
        {
            throw new KeyRingUnavailableException(
                $"Cannot access the shared Data Protection key ring (HKLM\\{RegistryKeyPath}). " +
                "Run GraphMailer elevated (the service runs as SYSTEM; start the ConfigTool as administrator).",
                ex);
        }
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
            .PersistKeysToSharedRegistry();

        var sp = services.BuildServiceProvider();
        lock (RootedProviders) RootedProviders.Add(sp);

        return sp.GetRequiredService<IDataProtectionProvider>()
                 .CreateProtector(ConfigPurpose);
    }
}
