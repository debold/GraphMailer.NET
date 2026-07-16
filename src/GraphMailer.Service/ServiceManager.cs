using System.Diagnostics;

namespace GraphMailer.Service;

internal static class ServiceManager
{
    private const string ServiceName = "GraphMailer";
    private const string ServiceDisplayName = "GraphMailer SMTP Relay";
    private const string ServiceDescription = "SMTP relay service that delivers emails via Microsoft 365 Graph API";

    internal static int Install()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");

        Console.WriteLine($"Installing Windows Service '{ServiceName}'...");

        int result = RunSc($"create \"{ServiceName}\" binPath= \"{exePath}\" start= auto DisplayName= \"{ServiceDisplayName}\"");
        if (result == 0)
        {
            RunSc($"description \"{ServiceName}\" \"{ServiceDescription}\"");
            // Recovery actions: restart the service 60 s after a crash (up to three
            // times; the failure counter resets after 24 h). Without this a crashed
            // service stays stopped until an operator intervenes.
            RunSc($"failure \"{ServiceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000");
            result = RunSc($"start \"{ServiceName}\"");
            if (result == 0)
                Console.WriteLine("Service installed and started successfully.");
        }

        return result;
    }

    internal static int Uninstall()
    {
        Console.WriteLine($"Stopping and removing Windows Service '{ServiceName}'...");

        RunSc($"stop \"{ServiceName}\"");
        int result = RunSc($"delete \"{ServiceName}\"");

        if (result == 0)
            Console.WriteLine("Service removed successfully.");

        return result;
    }

    internal static int Status()
    {
        return RunSc($"query \"{ServiceName}\"");
    }

    private static int RunSc(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sc.exe");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);

        return process.ExitCode;
    }
}
