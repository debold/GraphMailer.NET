using System.IO;
using System.Text.RegularExpressions;

namespace GraphMailer.ConfigTool.Services;

/// <summary>One parsed log line plus any continuation lines (stack traces) that followed it.</summary>
public record LogEntry(string TimeLocal, string Level, string Component, string Message, string RawLine)
{
    public string LevelShort => LogFileReader.ShortLevel(Level);

    public string LevelBg => Level switch
    {
        "Fatal" => "#FFC42B1C",
        "Error" => "#FFFDE7E9",
        "Warning" => "#FFFFF4CE",
        "Information" => "#FFDFF6DD",
        "Debug" => "#FFF0F0F0",
        _ => "#FFF0F0F0",
    };

    public string LevelFg => Level switch
    {
        "Fatal" => "#FFFFFFFF",
        "Error" => "#FFC42B1C",
        "Warning" => "#FF7A5700",
        "Information" => "#FF0F7B0F",
        "Debug" => "#FF616161",
        _ => "#FF616161",
    };

    /// <summary>
    /// Free-text search predicate. <see cref="RawLine"/> is included so a term inside a stack
    /// trace is found too — those are appended to the entry rather than kept as rows of their own.
    /// </summary>
    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var term = search.Trim();
        return Message.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Component.Contains(term, StringComparison.OrdinalIgnoreCase)
            || RawLine.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}

/// <param name="Entries">Matching entries, newest first.</param>
/// <param name="Components">
/// Distinct components seen while scanning — over *all* entries looked at, not only the matching
/// ones, so filtering by component never empties the dropdown that selects it.
/// </param>
/// <param name="Scanned">Entries examined; equals the whole log once a scan completes.</param>
/// <param name="HasMore">The limit was reached, so further entries may follow.</param>
/// <param name="ScanCapped">The scan budget ran out before the log was exhausted.</param>
internal sealed record LogReadResult(
    List<LogEntry> Entries,
    IReadOnlyList<string> Components,
    int Scanned,
    bool HasMore,
    bool ScanCapped);

/// <summary>
/// Reads the service's rolling log files newest-first.
/// </summary>
/// <remarks>
/// The service retains 7 daily files (<c>Program.cs</c>), and the page pages through all of them
/// rather than the newest two it used to read. Files are taken one at a time and stopped on as soon
/// as enough entries are collected, so the worst case in memory stays a single file — the same as
/// before, since <c>fileSizeLimitBytes</c> allows 100 MB per file. Reading a file backwards in
/// blocks would bound that properly; it is deliberately not done here, because it only pays off on
/// an installation that actually produces such a file.
/// </remarks>
internal static class LogFileReader
{
    /// <summary>Entries added per page.</summary>
    internal const int PageSize = 2000;

    /// <summary>
    /// Upper bound on the entries a *filtered* read walks through. An unfiltered read stops at the
    /// page limit on its own; a filter that matches nothing would otherwise parse all seven files
    /// on every refresh. Hitting the cap is surfaced in the counter, never silently.
    /// </summary>
    internal const int MaxScan = 25_000;

    // Serilog default file output format (no custom template set in Program.cs):
    // 2025-12-25 10:30:00.123 +01:00 [INF] [Component] Message text
    private static readonly Regex LogLineRegex = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+\s+[+-]\d{2}:\d{2})\s+\[(?<lvl>[A-Z]{3})\]\s+(?<msg>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ComponentRegex = new(@"^\[([^\]]+)\]\s*(.*)$", RegexOptions.Compiled);

    internal static LogReadResult Read(
        string logsDir, int limit, Func<LogEntry, bool>? predicate, int maxScan = MaxScan)
    {
        var entries = new List<LogEntry>();
        var components = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var hasMore = false;
        var scanCapped = false;

        if (!Directory.Exists(logsDir))
            return new LogReadResult(entries, [], 0, false, false);

        // Names carry the date, so descending name order is descending time order — including the
        // _001 suffix a size roll adds, which sorts after the base file of the same day.
        var files = Directory.GetFiles(logsDir, "graphmailer-*.log")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            // Parsed forward: a continuation line belongs to the entry *above* it
            var fileEntries = ParseFile(file);

            // ...then walked backwards, so the overall result stays newest-first
            for (var i = fileEntries.Count - 1; i >= 0; i--)
            {
                var entry = fileEntries[i];
                scanned++;

                if (entry.Component.Length > 0) components.Add(entry.Component);
                if (predicate is null || predicate(entry)) entries.Add(entry);

                if (entries.Count >= limit)
                {
                    hasMore = true;
                    return Done();
                }

                if (predicate is not null && scanned >= maxScan)
                {
                    scanCapped = true;
                    return Done();
                }
            }
        }

        return Done();

        LogReadResult Done() => new(entries, [.. components], scanned, hasMore, scanCapped);
    }

    private static List<LogEntry> ParseFile(string path)
    {
        var entries = new List<LogEntry>();

        string text;
        try
        {
            // Shared read: the service holds its current log file open
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
        }
        catch
        {
            return entries;   // locked or vanished mid-roll — skip the file, never fail the page
        }

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var entry = ParseLine(line);
            if (entry is not null)
            {
                entries.Add(entry);
            }
            else if (entries.Count > 0 && !string.IsNullOrWhiteSpace(line))
            {
                // Continuation line (stack trace etc.) — append to the previous entry
                var prev = entries[^1];
                entries[^1] = prev with { RawLine = prev.RawLine + "\n" + line };
            }
        }

        return entries;
    }

    private static LogEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var m = LogLineRegex.Match(line);
        if (!m.Success) return null;   // continuation / stack trace

        var tsStr = m.Groups["ts"].Value.Trim();
        var level = ExpandLevel(m.Groups["lvl"].Value);
        var msg = m.Groups["msg"].Value;

        var component = "";
        var compMatch = ComponentRegex.Match(msg);
        if (compMatch.Success)
        {
            component = compMatch.Groups[1].Value;
            msg = compMatch.Groups[2].Value;
        }

        var timeLocal = DateTimeOffset.TryParse(tsStr, out var dto)
            ? dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : tsStr;

        return new LogEntry(timeLocal, level, component, msg, line);
    }

    internal static string ExpandLevel(string abbr) => abbr switch
    {
        "VRB" => "Verbose",
        "DBG" => "Debug",
        "INF" => "Information",
        "WRN" => "Warning",
        "ERR" => "Error",
        "FTL" => "Fatal",
        _ => abbr,
    };

    internal static string ShortLevel(string full) => full switch
    {
        "Verbose" => "VRB",
        "Debug" => "DBG",
        "Information" => "INF",
        "Warning" => "WRN",
        "Error" => "ERR",
        "Fatal" => "FTL",
        _ => full,
    };

    internal static int LevelRank(string level) => level switch
    {
        "Verbose" => 0,
        "Debug" => 1,
        "Information" => 2,
        "Warning" => 3,
        "Error" => 4,
        "Fatal" => 5,
        _ => 0,
    };
}
