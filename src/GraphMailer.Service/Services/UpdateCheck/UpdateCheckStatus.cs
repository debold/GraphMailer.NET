using System.Text.Json;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Service.Services.UpdateCheck;

/// <summary>
/// Result of the last GitHub release check, persisted as a small JSON file so the
/// ConfigTool (a separate process) can display it. The companion request file lets
/// the ConfigTool ask the service for an immediate check ("Check now").
/// Both files live in %ProgramData%\GraphMailer\data.
/// </summary>
public sealed class UpdateCheckStatus
{
    public string? CurrentVersion { get; set; }
    public string? LatestVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? ReleaseName { get; set; }
    public DateTime? PublishedUtc { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public DateTime? NextCheckUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>Latest version an admin e-mail was sent for — one mail per new release.</summary>
    public string? LastNotifiedVersion { get; set; }

    public static string StatusFilePath => Path.Combine(AppPaths.DataDir, "update-status.json");

    /// <summary>Presence of this file asks the update check service for an immediate check.</summary>
    public static string CheckRequestFilePath => Path.Combine(AppPaths.DataDir, "update-check.request");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Returns null when the file is missing, mid-write or corrupt.</summary>
    public static UpdateCheckStatus? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpdateCheckStatus>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
