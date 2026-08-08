using System.Text.Json;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Service.Services;

/// <summary>One currently blocked address. Times are UTC; the reader converts for display.</summary>
public sealed class BlockedIpEntry
{
    public string Ip { get; set; } = string.Empty;

    /// <summary>Failures counted when the block was set — the number that tripped the threshold.</summary>
    public int Failures { get; set; }

    public DateTime BlockedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// The IP blocks currently held by the running service, persisted as a small JSON file so the
/// ConfigTool (a separate process) can show them — the same file-based bridge the sender directory
/// uses, since there is no socket or pipe between the two.
///
/// Written when a block is set and when the periodic sweep drops expired ones. Readers must still
/// filter by <see cref="BlockedIpEntry.ExpiresAtUtc"/>: between two writes the file can list blocks
/// that have since run out, and a file left behind by a stopped service would otherwise look live.
/// </summary>
public sealed class BlockedIpSnapshot
{
    public DateTime WrittenAtUtc { get; set; }
    public List<BlockedIpEntry> Entries { get; set; } = [];

    public static string FilePath => Path.Combine(AppPaths.DataDir, "blocked-ips.json");

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        // Temp + move: the ConfigTool polls this file and must never read a half-written one
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Returns null when the file is missing, mid-write or corrupt.</summary>
    public static BlockedIpSnapshot? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BlockedIpSnapshot>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Entries still in force at <paramref name="nowUtc"/>, newest block first.</summary>
    public IReadOnlyList<BlockedIpEntry> ActiveAt(DateTime nowUtc)
        => [.. Entries.Where(e => e.ExpiresAtUtc > nowUtc).OrderByDescending(e => e.BlockedAtUtc)];
}
