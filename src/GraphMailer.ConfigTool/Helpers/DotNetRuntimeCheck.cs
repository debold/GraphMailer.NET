using System.IO;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Detects whether the .NET runtime required by the (framework-dependent)
/// GraphMailer service is installed, by enumerating the shared-framework
/// folders under the dotnet root.
///
/// Note: this can only ever run when a runtime exists for the ConfigTool
/// itself — the value of the check is catching mixed deployments (e.g. a
/// self-contained ConfigTool next to a framework-dependent service) and
/// giving a precise hint when a service start fails. The "no .NET at all"
/// case is handled by the native app host, which shows its own download
/// dialog/message before any managed code runs.
/// </summary>
internal static class DotNetRuntimeCheck
{
    /// <summary>Major .NET version the service requires.</summary>
    public const int RequiredMajor = 8;

    public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";

    /// <summary>Hint text appended to error messages when the runtime is missing.</summary>
    public static string MissingRuntimeHint =>
        $".NET Runtime {RequiredMajor} (x64) was not found on this machine — " +
        $"install the .NET Desktop Runtime {RequiredMajor} from {DownloadUrl}.";

    /// <summary>True when a Microsoft.NETCore.App {RequiredMajor}.x runtime is installed.</summary>
    public static bool IsServiceRuntimeInstalled()
        => CandidateDotnetRoots().Any(root =>
            GetInstalledMajors(root, "Microsoft.NETCore.App").Contains(RequiredMajor));

    private static IEnumerable<string> CandidateDotnetRoots()
    {
        var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
            yield return envRoot;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "dotnet");
    }

    /// <summary>
    /// Major versions installed for a shared framework (folder names like
    /// "8.0.16" or "9.0.0-preview.5" under {root}\shared\{framework}).
    /// </summary>
    internal static IReadOnlyCollection<int> GetInstalledMajors(string dotnetRoot, string sharedFramework)
    {
        try
        {
            var dir = Path.Combine(dotnetRoot, "shared", sharedFramework);
            if (!Directory.Exists(dir))
                return [];

            var majors = new HashSet<int>();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                var dash = name.IndexOf('-');          // strip "-preview…" suffixes
                if (dash > 0) name = name[..dash];
                if (Version.TryParse(name, out var version))
                    majors.Add(version.Major);
            }
            return majors;
        }
        catch
        {
            return [];   // inaccessible path — treat as not installed
        }
    }
}
