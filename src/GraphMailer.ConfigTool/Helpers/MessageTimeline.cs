using System.Globalization;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure.Metrics;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>One line of the timeline as the Messages page shows it.</summary>
/// <param name="Group">Section heading shown above the first line of a section, else null.</param>
public sealed record TimelineItem(
    DateTime? At, string Outcome, string Summary, string? Detail, string? Group = null)
{
    public string TimeText => At?.ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? string.Empty;
    public string DateText => At?.ToString("dd.MM.", CultureInfo.CurrentCulture) ?? string.Empty;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasGroup => !string.IsNullOrEmpty(Group);

    /// <summary>Dot colour, following the page's status pills.</summary>
    public string DotBrush => Outcome switch
    {
        TraceOutcomes.Ok => "#FF0F7B0F",
        TraceOutcomes.Retry or TraceOutcomes.Warning => "#FF9D5D00",
        TraceOutcomes.Failed => "#FFC42B1C",
        _ => "#FF8A8A8A",
    };
}

/// <summary>
/// Turns what is known about a message into the Messages page's timeline and one-sentence
/// summary. Pure functions, so the wording and the collapsing can be tested without a window.
/// </summary>
internal static class MessageTimeline
{
    /// <summary>Retry runs longer than this are shortened to the first and last two attempts.</summary>
    internal const int CollapseThreshold = 5;

    /// <summary>
    /// Combines the history list with the mail folders. A message in both keeps its history row,
    /// enriched with what only the <c>.meta.json</c> knows (next retry, last error, Message-ID).
    /// A message only in the folders was handled before message history existed.
    /// </summary>
    internal static List<MessageRow> Merge(
        IReadOnlyList<MessageRow> traced, int tracedTotal, IReadOnlyList<MessageRow> folders,
        MessageListFilter filter, string? search, int limit,
        out int total, out bool hasMore)
    {
        var byId = folders.ToDictionary(f => f.MessageId, StringComparer.Ordinal);
        var merged = new List<MessageRow>(traced.Count + folders.Count);

        foreach (var t in traced)
        {
            if (byId.Remove(t.MessageId, out var f))
                merged.Add(Enrich(t, f));
            else
                merged.Add(t);
        }

        merged.AddRange(byId.Values.Where(f => Matches(f, filter)));

        var ordered = merged
            .Where(r => string.IsNullOrWhiteSpace(search) || r.Matches(search!))
            .OrderByDescending(r => r.ReceivedAt)
            .ToList();

        total = tracedTotal + byId.Count;
        hasMore = ordered.Count > limit;
        return [.. ordered.Take(limit)];
    }

    /// <summary>Filter test for folder-only rows; history rows are filtered in SQL with the same meaning.</summary>
    internal static bool Matches(MessageRow row, MessageListFilter filter) => filter switch
    {
        MessageListFilter.Queue => row.Status == TraceStatuses.Queued,
        MessageListFilter.Delayed => (row.Status == TraceStatuses.Sent || row.Status == TraceStatuses.Queued) && row.FailedAttempts > 0,
        MessageListFilter.Failed => row.Status == TraceStatuses.Failed,
        MessageListFilter.Rejected => row.Status is TraceStatuses.Rejected or TraceStatuses.Discarded,
        MessageListFilter.Sent => row.Status == TraceStatuses.Sent,
        _ => true,
    };

    private static MessageRow Enrich(MessageRow t, MessageRow f) => new()
    {
        MessageId = t.MessageId,
        ReceivedAt = t.ReceivedAt,
        From = Pick(t.From, f.From),
        To = Pick(t.To, f.To),
        Subject = Pick(t.Subject, f.Subject),
        // The folder is the live truth while a message is queued; the history may lag a poll.
        Status = string.IsNullOrEmpty(f.Status) ? t.Status : f.Status,
        Attempts = f.Attempts,
        FailedAttempts = f.FailedAttempts,
        LastAttemptAt = f.LastAttemptAt,
        LastError = f.LastError,
        NextRetryAt = f.NextRetryAt,
        SentAt = f.SentAt ?? t.SentAt,
        ClientIp = Pick(t.ClientIp, f.ClientIp),
        SmtpMessageId = f.SmtpMessageId,
        EmlPath = f.EmlPath,
        SizeBytes = t.SizeBytes,
        // A row rebuilt from the metrics (before 1.6.0) has no history of its own.
        HasTrace = t.HasTrace,
    };

    private static string Pick(string first, string second) => string.IsNullOrEmpty(first) ? second : first;

    /// <summary>
    /// Timeline items for a message: the recorded steps grouped by stage and with long retry runs
    /// shortened, or — for a message from before 1.6.0 — what the <c>.meta.json</c> and the
    /// receipt metrics still tell.
    /// </summary>
    internal static List<TimelineItem> Build(MessageRow row, IReadOnlyList<TraceEventRow> events, ReceiptFacts? receipt)
    {
        var items = events.Count > 0 ? FromEvents(events) : FromMeta(row, receipt);

        // Every item carries its section; only the first of each run keeps it as a heading.
        string? lastGroup = null;
        for (var i = 0; i < items.Count; i++)
        {
            var group = items[i].Group ?? "Delivery";
            items[i] = items[i] with { Group = group == lastGroup ? null : group };
            lastGroup = group;
        }
        return items;
    }

    private static List<TimelineItem> FromEvents(IReadOnlyList<TraceEventRow> events)
    {
        var items = new List<TimelineItem>();
        var i = 0;
        while (i < events.Count)
        {
            // A run of failed attempts: show the first two and the last two, and how many are hidden.
            if (events[i].Outcome == TraceOutcomes.Retry)
            {
                var start = i;
                while (i < events.Count && events[i].Outcome == TraceOutcomes.Retry) i++;
                var run = events.Skip(start).Take(i - start).ToList();

                if (run.Count > CollapseThreshold)
                {
                    items.AddRange(run.Take(2).Select(ToItem));
                    var hidden = run.Count - 4;
                    items.Add(new TimelineItem(null, TraceOutcomes.Retry,
                        $"{hidden} more failed attempts",
                        $"{run[2].OccurredAt:HH:mm} – {run[^3].OccurredAt:HH:mm}", "Delivery"));
                    items.AddRange(run.Skip(run.Count - 2).Select(ToItem));
                }
                else
                {
                    items.AddRange(run.Select(ToItem));
                }
                continue;
            }

            items.Add(ToItem(events[i]));
            i++;
        }
        return items;
    }

    private static TimelineItem ToItem(TraceEventRow e) => new(
        e.OccurredAt, e.Outcome, e.Summary, e.Detail,
        e.Stage switch
        {
            TraceStages.Smtp => "SMTP",
            TraceStages.Scan or TraceStages.Rules => "Processing",
            _ => "Delivery",
        });

    /// <summary>Reduced timeline for a message without recorded history.</summary>
    private static List<TimelineItem> FromMeta(MessageRow row, ReceiptFacts? receipt)
    {
        var items = new List<TimelineItem>
        {
            new(row.ReceivedAt, TraceOutcomes.Ok,
                string.IsNullOrEmpty(row.ClientIp) ? "Received and queued" : $"Received from {row.ClientIp} and queued",
                receipt is null ? null : string.Join(" · ",
                    $"port {receipt.ListenerPort}",
                    receipt.Tls ? "TLS" : "no TLS",
                    string.IsNullOrEmpty(receipt.AuthUser) ? "not signed in" : $"signed in as {receipt.AuthUser}",
                    FormatSize(receipt.SizeBytes)),
                "SMTP"),
        };

        if (row.FailedAttempts > 0)
        {
            items.Add(new TimelineItem(row.LastAttemptAt, TraceOutcomes.Retry,
                row.FailedAttempts == 1
                    ? "1 delivery attempt failed"
                    : row.LastAttemptAt is { } last
                        ? $"{row.FailedAttempts} delivery attempts failed, the last one at {last:HH:mm}"
                        : $"{row.FailedAttempts} delivery attempts failed",
                string.IsNullOrEmpty(row.LastError) ? null : row.LastError,
                "Delivery"));
        }

        switch (row.Status)
        {
            case TraceStatuses.Sent:
                items.Add(new TimelineItem(row.SentAt, TraceOutcomes.Ok, "Delivered to Exchange Online", null, "Delivery"));
                break;
            case TraceStatuses.Failed:
                items.Add(new TimelineItem(row.LastAttemptAt, TraceOutcomes.Failed, "Given up, moved to the failed folder",
                    string.IsNullOrEmpty(row.LastError) ? null : row.LastError, "Delivery"));
                break;
            case TraceStatuses.Queued when row.NextRetryAt is not null:
                items.Add(new TimelineItem(row.NextRetryAt, TraceOutcomes.Info, "Next attempt scheduled", null, "Delivery"));
                break;
        }
        return items;
    }

    /// <summary>The answer to "was it delivered, and when?" in one or two sentences.</summary>
    internal static string Summarize(MessageRow row, IReadOnlyList<TraceEventRow> events)
    {
        var lastFailure = events.LastOrDefault(e => e.Outcome is TraceOutcomes.Retry or TraceOutcomes.Failed);
        var lastError = lastFailure?.Detail ?? (string.IsNullOrEmpty(row.LastError) ? null : row.LastError);
        lastError = lastError is null ? null : FirstPart(lastError);

        switch (row.Status)
        {
            case TraceStatuses.Sent when row.SentAt is { } sent:
            {
                var text = $"Delivered on {sent:d} at {sent:HH:mm}, {FormatDelay(sent - row.ReceivedAt)} after it arrived.";
                if (row.FailedAttempts > 0)
                    text += row.FailedAttempts == 1
                        ? $" The first attempt failed{(lastError is null ? "" : $" ({lastError})")}."
                        : $" {row.FailedAttempts} attempts failed before that{(lastError is null ? "" : $", the last one with: {lastError}")}.";
                return text;
            }
            case TraceStatuses.Sent:
                return "Delivered.";
            case TraceStatuses.Queued when row.FailedAttempts > 0:
                return $"Not delivered yet. {row.FailedAttempts} attempt(s) failed so far"
                       + (lastError is null ? "." : $", the last one with: {lastError}.")
                       + (row.NextRetryAt is { } next ? $" Next attempt at {next:HH:mm}." : string.Empty);
            case TraceStatuses.Queued:
                return $"Accepted at {row.ReceivedAt:HH:mm} and waiting for delivery.";
            case TraceStatuses.Failed:
                return "Not delivered. GraphMailer gave up"
                       + (row.FailedAttempts > 0 ? $" after {row.FailedAttempts} attempt(s)" : string.Empty)
                       + (lastError is null ? "." : $": {lastError}.");
            case TraceStatuses.Rejected:
                return $"Not accepted. {events.LastOrDefault(e => e.Outcome == TraceOutcomes.Failed)?.Summary ?? "The message was refused during the SMTP session"}; the sending device got an error reply.";
            case TraceStatuses.Discarded:
                return $"Not delivered. {events.LastOrDefault(e => e.Outcome == TraceOutcomes.Failed)?.Summary ?? "A rule discarded the message"}; the sending device was told it was accepted.";
            case MessageTraceReader.UnknownStatus:
                return $"Received on {row.ReceivedAt:d} at {row.ReceivedAt:HH:mm}. Its delivery was not recorded "
                       + "(handled before GraphMailer 1.6.0, or still under way when the service stopped).";
            default:
                return string.Empty;
        }
    }

    /// <summary>Text for "Copy summary": the sentence plus the facts someone asking will want.</summary>
    internal static string BuildCopyText(MessageRow row, string summary)
    {
        var lines = new List<string>
        {
            summary,
            string.Empty,
            $"Subject:  {row.Subject}",
            $"From:     {row.From}",
            $"To:       {row.To}",
            $"Received: {row.ReceivedAt:yyyy-MM-dd HH:mm:ss}",
        };
        if (row.SentAt is { } sent) lines.Add($"Delivered: {sent:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"GraphMailer id: {row.MessageId}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Delay in words, e.g. "36 minutes", "2 hours 5 minutes", "12 seconds".</summary>
    internal static string FormatDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (delay.TotalMinutes < 1) return $"{Math.Max(0, (int)delay.TotalSeconds)} seconds";
        if (delay.TotalHours < 1) return delay.Minutes == 1 ? "1 minute" : $"{delay.Minutes} minutes";
        var hours = (int)delay.TotalHours;
        var h = hours == 1 ? "1 hour" : $"{hours} hours";
        return delay.Minutes == 0 ? h : $"{h} {delay.Minutes} min";
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };

    /// <summary>The error before the " · next attempt in …" the service appends to a retry line.</summary>
    private static string FirstPart(string detail)
    {
        var cut = detail.IndexOf(" · ", StringComparison.Ordinal);
        return cut > 0 ? detail[..cut] : detail;
    }
}
