using Microsoft.Win32;

namespace GraphMailer.Service.Infrastructure.Security.Amsi;

/// <summary>An AMSI provider registered on this machine.</summary>
/// <param name="Clsid">COM class id, as listed under the AMSI providers key.</param>
/// <param name="Name">Display name from the CLSID default value; empty when unnamed.</param>
/// <param name="DllPath">In-process server DLL; empty when the COM registration is incomplete.</param>
internal sealed record AmsiProvider(string Clsid, string Name, string DllPath)
{
    /// <summary>Short "Name (dll)" form for logs and the ConfigTool list.</summary>
    internal string Describe()
    {
        var file = string.IsNullOrEmpty(DllPath) ? "unknown DLL" : Path.GetFileName(DllPath);
        return string.IsNullOrEmpty(Name) ? $"{Clsid} ({file})" : $"{Name} ({file})";
    }
}

/// <summary>
/// Enumerates the AMSI providers registered on this machine.
///
/// AMSI is vendor agnostic: any antimalware product may register an in-process COM server
/// implementing <c>IAntimalwareProvider</c>, and a scan is answered by whichever providers are
/// present — Microsoft Defender (<c>MpOav.dll</c>) is just one of them. Registration takes two
/// keys: the AMSI enrolment under <see cref="ProvidersKeyPath"/> and the regular COM registration
/// under <c>HKLM\SOFTWARE\Classes\CLSID\{clsid}</c>.
///
/// This is the only way to establish "is anyone going to scan?" without actually scanning:
/// <c>AmsiInitialize</c> succeeds even with no provider installed, and scans then return
/// <see cref="AmsiResult.NotDetected"/> — indistinguishable from genuinely clean content.
/// Triggering a real detection with a test sample would answer it definitively, but produces a
/// malware alert attributed to this process, so it stays a deliberate, operator-initiated action.
///
/// Read-only HKLM access, so it works from the non-elevated ConfigTool as well.
/// </summary>
internal static class AmsiProviderRegistry
{
    internal const string ProvidersKeyPath = @"SOFTWARE\Microsoft\AMSI\Providers";
    private const string ClsidKeyPath = @"SOFTWARE\Classes\CLSID";

    /// <summary>
    /// Lists the registered providers, newest registry order preserved. Returns an empty list
    /// when the key is absent, unreadable, or holds no entries — every failure mode collapses
    /// to "no provider", which callers must treat as "scanning is not available".
    /// </summary>
    internal static IReadOnlyList<AmsiProvider> Enumerate()
    {
        // GraphMailer ships win-x64 only; the guard also tells the platform-compatibility
        // analyzer (CA1416) that the registry calls below run on Windows. Returning empty
        // rather than throwing keeps the "no provider" degradation path uniform.
        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            using var providers = Registry.LocalMachine.OpenSubKey(ProvidersKeyPath);
            if (providers is null) return [];

            var result = new List<AmsiProvider>();
            foreach (var clsid in providers.GetSubKeyNames())
            {
                var (name, dll) = ReadComRegistration(clsid);
                result.Add(new AmsiProvider(clsid, name, dll));
            }
            return result;
        }
        catch (Exception)
        {
            // A missing or ACL-protected key is not an error worth propagating: the caller
            // degrades to "no scanning available" either way, and that path already logs.
            return [];
        }
    }

    /// <summary>
    /// Resolves display name and in-process server DLL for a CLSID. An enrolled provider whose
    /// COM registration is missing yields empty strings rather than being dropped — it is still
    /// enrolled, and hiding it would misreport the machine's state.
    /// </summary>
    private static (string Name, string DllPath) ReadComRegistration(string clsid)
    {
        if (!OperatingSystem.IsWindows()) return (string.Empty, string.Empty);

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ClsidKeyPath}\{clsid}");
            if (key is null) return (string.Empty, string.Empty);

            var name = key.GetValue(null) as string ?? string.Empty;

            using var inproc = key.OpenSubKey("InprocServer32");
            // REG_EXPAND_SZ is common here (%ProgramFiles%\…), so expand unless asked not to.
            var dll = inproc?.GetValue(null) as string ?? string.Empty;
            if (!string.IsNullOrEmpty(dll))
                dll = Environment.ExpandEnvironmentVariables(dll).Trim('"');

            return (name, dll);
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
    }
}
