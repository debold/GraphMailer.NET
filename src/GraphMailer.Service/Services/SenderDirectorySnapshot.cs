using System.Text.Json;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Service.Services;

/// <summary>One synchronised recipient, as shown in the ConfigTool's directory viewer.</summary>
/// <param name="Kind">"Mailbox" or "Group" — written as text so the file stays readable.</param>
/// <param name="DisplayName">Directory display name, when Graph reported one.</param>
/// <param name="PrimaryAddress">UPN for a mailbox, the mail address for a group.</param>
/// <param name="Addresses">Every SMTP address the sender may use, primary and aliases.</param>
public sealed record SenderDirectoryEntry(
    string Kind,
    string? DisplayName,
    string? PrimaryAddress,
    IReadOnlyList<string> Addresses);

/// <summary>
/// The full set of recipients the running service has synced, written to disk after every
/// directory sync so the ConfigTool — a separate process — can show what was actually
/// recognised. Read-only by design: it is a view of the service's state, not an input.
///
/// Follows the same file-based IPC pattern as <see cref="SenderDirectoryStatus"/>; both live
/// in %ProgramData%\GraphMailer\data.
/// </summary>
public sealed class SenderDirectorySnapshot
{
    public DateTime GeneratedUtc { get; set; }
    public List<SenderDirectoryEntry> Entries { get; set; } = [];

    /// <summary>
    /// The tenant mail domains derived from those recipients, each with its leading '@'. Shown in
    /// the viewer alongside the recipients: they are what decides whether a mail-enabled public
    /// folder or dynamic distribution group gets through, and there is no other way to see them.
    /// </summary>
    public List<string> Domains { get; set; } = [];

    /// <summary>
    /// Cap on how many entries are written. A very large tenant would otherwise turn an
    /// informational file into a multi-megabyte write on every sync; the viewer says so when
    /// the list was cut short.
    /// </summary>
    public const int MaxEntries = 20_000;

    /// <summary>True when the tenant has more recipients than <see cref="MaxEntries"/>.</summary>
    public bool Truncated { get; set; }

    public static string FilePath => Path.Combine(AppPaths.DataDir, "sender-directory.json");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>Returns null when the file is missing, mid-write or corrupt.</summary>
    public static SenderDirectorySnapshot? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SenderDirectorySnapshot>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>Builds a snapshot from the directory's recipients, sorted for a stable view.</summary>
    internal static SenderDirectorySnapshot From(
        IReadOnlyList<TenantUser> recipients, IReadOnlyList<string> mailDomains, DateTime utcNow)
    {
        var ordered = recipients
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.DisplayName ?? r.PrimaryOrFirstAddress(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SenderDirectorySnapshot
        {
            GeneratedUtc = utcNow,
            Truncated = ordered.Count > MaxEntries,
            Entries =
            [
                .. ordered.Take(MaxEntries).Select(r => new SenderDirectoryEntry(
                    r.Kind.ToString(),
                    r.DisplayName,
                    r.PrimaryOrFirstAddress(),
                    r.SmtpAddresses)),
            ],
            Domains = [.. mailDomains.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)],
        };
    }
}
