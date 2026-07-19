using System.Text.Json;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Service.Services.Telemetry;

/// <summary>
/// Runtime state of the opt-in telemetry heartbeat, persisted as a small JSON file so
/// service restarts do not re-send and the ConfigTool (a separate process) can display
/// the install id and last transmission. This file holds only service-written state —
/// the opt-in itself lives in the normal configuration (<c>Telemetry.Enabled</c>).
/// </summary>
public sealed class TelemetryStatus
{
    /// <summary>
    /// Random GUID identifying this installation across heartbeats. Generated once on
    /// the first send; never derived from hardware, hostname or user data.
    /// </summary>
    public string? InstallId { get; set; }

    public DateTime? LastHeartbeatUtc { get; set; }
    public DateTime? NextHeartbeatUtc { get; set; }

    /// <summary>Watermark: mail counters are aggregated from this instant onwards.</summary>
    public DateTime? CountersSinceUtc { get; set; }

    public string? LastError { get; set; }

    public static string StatusFilePath => Path.Combine(AppPaths.DataDir, "telemetry-status.json");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Returns null when the file is missing, mid-write or corrupt.</summary>
    public static TelemetryStatus? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TelemetryStatus>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
