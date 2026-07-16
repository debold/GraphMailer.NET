using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Shared sc.exe wrapper for the GraphMailer Windows Service.
/// Single source for service name, sc.exe invocation and queryex parsing —
/// used by both the MainWindow status poller and the StatusPage controls.
/// </summary>
internal static class ServiceControl
{
    public const string ServiceName = "GraphMailer";

    private static readonly Regex StateRegex = new(@"STATE\s*:\s*\d+\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex PidRegex = new(@"PID\s*:\s*(\d+)", RegexOptions.Compiled);

    /// <summary>Runs sc.exe with the given arguments and returns combined stdout+stderr and the exit code.</summary>
    public static (string Output, int ExitCode) RunSc(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (output, proc.ExitCode);
    }

    /// <summary>
    /// Queries the service via <c>sc queryex</c>.
    /// State is the SCM token (RUNNING, STOPPED, START_PENDING, STOP_PENDING, …)
    /// or "Not Installed" / "Pending Deletion" / "Unknown".
    /// Pid is non-zero only while the service is RUNNING.
    /// </summary>
    public static (string State, bool Exists, uint Pid) Query()
    {
        var (output, exit) = RunSc($"queryex \"{ServiceName}\"");

        if (exit == 1060 || output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return ("Not Installed", false, 0);
        // 1072 = ERROR_SERVICE_MARKED_FOR_DELETE — treat as gone for Install button purposes
        if (exit == 1072 || output.Contains("marked for deletion", StringComparison.OrdinalIgnoreCase))
            return ("Pending Deletion", false, 0);

        var stateMatch = StateRegex.Match(output);
        var state = stateMatch.Success ? stateMatch.Groups[1].Value : "Unknown";

        uint pid = 0;
        if (state == "RUNNING")
        {
            var pidMatch = PidRegex.Match(output);
            if (pidMatch.Success) uint.TryParse(pidMatch.Groups[1].Value, out pid);
        }

        // "Unknown" still counts as existing: the query reached a service entry,
        // only the output was unparseable — don't offer Install in that case.
        return (state, true, pid);
    }
}
