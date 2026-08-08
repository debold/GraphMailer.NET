using System.IO;
using System.Text.Json;
using GraphMailer.Service.Services;

namespace GraphMailer.ConfigTool.Services;

/// <summary>Display row for one message in the Messages page DataGrid.</summary>
public sealed class MessageRow
{
    public string MessageId { get; init; } = string.Empty;
    public DateTime ReceivedAt { get; init; }
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    /// <summary>Status pill in the "All" view — capitalised label plus its colours.</summary>
    public string StatusLabel => string.IsNullOrEmpty(Status)
        ? "—"
        : char.ToUpperInvariant(Status[0]) + Status[1..];

    public string StatusBg => Status switch
    {
        "sent" => "#FFDCEEFB",      // delivered — blue, matching the metrics charts
        "failed" => "#FFFDE7E9",    // danger
        "queued" => "#FFFFF4CE",    // waiting
        _ => "#FFF0F0F0",
    };

    public string StatusFg => Status switch
    {
        "sent" => "#FF0F5A9C",
        "failed" => "#FFC42B1C",
        "queued" => "#FF7A5700",
        _ => "#FF616161",
    };
    /// <summary>
    /// Number of delivery attempts so far. For queued/failed messages this is the failed-attempt
    /// count; for sent messages the successful attempt is included (first-try = "1"). Retries are
    /// now time-bounded (no fixed maximum), so this is a plain count.
    /// </summary>
    public string Attempts { get; init; } = string.Empty;
    public DateTime? LastAttemptAt { get; init; }
    public string LastError { get; init; } = string.Empty;
    public DateTime? NextRetryAt { get; init; }
    public DateTime? SentAt { get; init; }
    public string ClientIp { get; init; } = string.Empty;
    public string SmtpMessageId { get; init; } = string.Empty;

    /// <summary>
    /// Free-text search across every displayed field, including the ones only the details panel
    /// shows. Blank search matches everything.
    /// </summary>
    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var term = search.Trim();
        return Contains(From) || Contains(To) || Contains(Subject) || Contains(StatusLabel)
            || Contains(LastError) || Contains(ClientIp) || Contains(SmtpMessageId)
            || Contains(MessageId) || Contains(ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        bool Contains(string value) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}

/// <param name="Rows">Matching rows, newest first.</param>
/// <param name="Total">Messages in the folder(s) — what <paramref name="Rows"/> was drawn from.</param>
/// <param name="HasMore">More messages exist than the limit allowed.</param>
internal sealed record MailFolderResult(List<MessageRow> Rows, int Total, bool HasMore);

/// <summary>
/// Reads the *.meta.json files the service writes next to each queued/failed/archived
/// message (see MailQueueWriter / QueueProcessor) and maps them to display rows.
/// Corrupt or mid-write files are skipped — the next refresh picks them up.
/// </summary>
internal static class MailFolderReader
{
    /// <summary>
    /// Rows added per page. The limit exists for the DataGrid, not for the disk: every
    /// <c>*.meta.json</c> in the folder is read and parsed before sorting, so a higher limit costs
    /// almost nothing beyond holding the rows.
    /// </summary>
    internal const int PageSize = 500;

    /// <summary>Kept for the page's "newest N" wording; the page size is the same number.</summary>
    internal const int MaxEntries = PageSize;

    /// <summary>
    /// Merges several folders into one newest-first list for the "All" view. The limit is
    /// applied to the merged result, so the newest messages win no matter which folder
    /// they sit in — capping per folder first would drop newer entries of a busy folder
    /// in favour of older ones from a quiet one.
    /// </summary>
    internal static MailFolderResult ReadFolders(int limit, string? search, params string[] directories)
    {
        var rows = new List<MessageRow>();
        foreach (var directory in directories)
            rows.AddRange(ReadAll(directory));

        return Page(rows, limit, search);
    }

    internal static MailFolderResult ReadFolder(string directory, int limit, string? search)
        => Page(ReadAll(directory), limit, search);

    /// <summary>
    /// Sorts, filters and cuts to the page. The search runs over the whole folder rather than the
    /// page, since all rows are parsed anyway — a search that only saw the newest 500 would miss
    /// exactly the older message the user is looking for.
    /// </summary>
    private static MailFolderResult Page(List<MessageRow> rows, int limit, string? search)
    {
        var ordered = rows.OrderByDescending(r => r.ReceivedAt);

        var matching = string.IsNullOrWhiteSpace(search)
            ? ordered.ToList()
            : ordered.Where(r => r.Matches(search)).ToList();

        return new MailFolderResult(
            [.. matching.Take(limit)],
            rows.Count,
            matching.Count > limit);
    }

    private static List<MessageRow> ReadAll(string directory)
    {
        var rows = new List<MessageRow>();
        if (!Directory.Exists(directory))
            return rows;

        foreach (var metaPath in Directory.EnumerateFiles(directory, "*.meta.json"))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<MailMetadata>(File.ReadAllText(metaPath));
                if (meta is null) continue;

                // RetryCount counts FAILED attempts; for a delivered message the
                // successful attempt is part of the story (first-try delivery = "1").
                var attemptsUsed = meta.Status == "sent" ? meta.RetryCount + 1 : meta.RetryCount;

                rows.Add(new MessageRow
                {
                    MessageId = meta.MessageId,
                    ReceivedAt = meta.ReceivedAt.ToLocalTime(),
                    From = meta.From,
                    To = string.Join(", ", meta.To),
                    Subject = meta.Subject,
                    Status = meta.Status,
                    Attempts = attemptsUsed.ToString(),
                    LastAttemptAt = meta.LastAttemptAt?.ToLocalTime(),
                    LastError = meta.LastError ?? string.Empty,
                    NextRetryAt = meta.NextRetryAt?.ToLocalTime(),
                    SentAt = meta.SentAt?.ToLocalTime(),
                    ClientIp = meta.ClientIp,
                    SmtpMessageId = meta.SmtpMessageId,
                });
            }
            catch
            {
                // corrupt or currently being written — skip silently
            }
        }

        return rows;
    }
}
