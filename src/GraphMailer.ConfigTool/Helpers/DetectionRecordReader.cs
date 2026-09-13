using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GraphMailer.Service.Services;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Picks the malware detections out of <c>mail\blocked\</c> for the detection list on the Malware
/// Scan page.
///
/// The folder is shared: the message rules write their discard records there too, and both kinds
/// sit side by side in the same time order. So the display limit has to be applied to
/// <i>detections</i>, never to files — cutting the newest N files first would hide every detection
/// that happens to sit behind a burst of rule hits, and the page would report "no detections"
/// while the findings were on disk all along.
///
/// Separate from the page so the selection can be tested without a visual tree; the page keeps the
/// projection into display rows.
/// </summary>
internal static class DetectionRecordReader
{
    /// <summary>Newest first, and bounded — the folder holds up to a full retention period.</summary>
    internal const int MaxDetectionsShown = 200;

    /// <summary>
    /// How many record files are opened while looking for those detections. Reading past the
    /// discards is the whole point, but not without a limit: this runs on the UI thread and a busy
    /// rule can leave thousands of records behind.
    /// </summary>
    internal const int MaxRecordsExamined = 5_000;

    /// <param name="Truncated">
    /// True when the search stopped at <see cref="MaxRecordsExamined"/> with older files left
    /// unread. It is what keeps the page from claiming the folder holds no detections after
    /// having read only part of it.
    /// </param>
    internal readonly record struct Result(IReadOnlyList<BlockedMessageRecord> Records, bool Truncated);

    /// <summary>
    /// Reads the newest detections from <paramref name="blockedDir"/>, newest first. Best-effort
    /// throughout: this is a convenience view over evidence files, and neither a missing folder nor
    /// a single unreadable record may take the configuration page down with it.
    /// </summary>
    /// <param name="maxRecordsExamined">
    /// Overrides <see cref="MaxRecordsExamined"/>, so the truncation behaviour can be tested
    /// without laying down thousands of files. The page always uses the default.
    /// </param>
    internal static Result Read(string blockedDir, int maxRecordsExamined = MaxRecordsExamined)
    {
        var records = new List<BlockedMessageRecord>();
        var examined = 0;

        try
        {
            if (!Directory.Exists(blockedDir)) return new Result(records, Truncated: false);

            foreach (var file in new DirectoryInfo(blockedDir)
                         .GetFiles("*.meta.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (++examined > maxRecordsExamined) break;
                if (TryRead(file.FullName) is { } record) records.Add(record);
                if (records.Count == MaxDetectionsShown) break;
            }
        }
        catch (Exception)
        {
            // Directory vanished or is ACL-protected — what was read so far is the honest answer.
        }

        return new Result(records, Truncated: examined > maxRecordsExamined);
    }

    /// <summary>
    /// The caption under the list. Split out with the reader because its whole job is not to
    /// overstate what was read: "no detections recorded" is a claim about the entire folder and
    /// must not be made after a truncated search.
    /// </summary>
    internal static string Describe(int shown, bool truncated) => shown switch
    {
        0 when truncated => $"No detections among the newest {MaxRecordsExamined:N0} records.",
        0 => "No detections recorded.",
        _ => $"{shown} detection(s)"
             + (shown == MaxDetectionsShown || truncated ? " (newest shown)" : string.Empty),
    };

    /// <summary>
    /// One record, or <see langword="null"/> when it is not a detection. A discard written by the
    /// message rules is not one; an empty <c>Source</c> is, because every record written before
    /// that field existed came from the scanner.
    /// </summary>
    private static BlockedMessageRecord? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var record = JsonSerializer.Deserialize<BlockedMessageRecord>(stream);
            if (record is null || record.Source == BlockedMessageSources.MessageRule) return null;
            return record;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
