using System.Reflection;

namespace GraphMailer.Service.Infrastructure;

/// <summary>
/// Build/version information read from the assembly attributes at runtime, so it always
/// reflects the running binary and never needs manual upkeep. The values are produced by
/// <c>src/Directory.Build.props</c>: <see cref="FileVersion"/> equals the release folder name
/// (<c>Version.BuildNumber</c>, e.g. <c>1.1.0.163</c>); <see cref="InformationalVersion"/>
/// carries the SemVer + build date (e.g. <c>1.1.0+20260613</c>).
/// </summary>
internal static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    /// <summary>Four-part file version, e.g. <c>1.1.0.163</c> (matches the release folder).</summary>
    internal static string FileVersion =>
        Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "unknown";

    /// <summary>Informational version, e.g. <c>1.1.0+20260613</c>.</summary>
    internal static string InformationalVersion =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? FileVersion;
}
