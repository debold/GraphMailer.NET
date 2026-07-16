using System.Text.Json;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Service.Services;

/// <summary>
/// Sync status of the tenant sender directory, persisted as a small JSON file so
/// the ConfigTool (a separate process) can display it. The companion request file
/// lets the ConfigTool ask the service for an immediate sync ("Sync now").
/// Both files live in %ProgramData%\GraphMailer\data.
/// </summary>
public sealed class SenderDirectoryStatus
{
    public DateTime? LastSyncUtc { get; set; }
    public bool LastSyncSuccess { get; set; }
    public int UserCount { get; set; }
    public int AddressCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? NextSyncUtc { get; set; }

    public static string StatusFilePath => Path.Combine(AppPaths.DataDir, "sender-directory-status.json");

    /// <summary>Presence of this file asks the sync service for an immediate refresh.</summary>
    public static string SyncRequestFilePath => Path.Combine(AppPaths.DataDir, "sender-directory-sync.request");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Returns null when the file is missing, mid-write or corrupt.</summary>
    public static SenderDirectoryStatus? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SenderDirectoryStatus>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
