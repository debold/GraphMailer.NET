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
}

/// <summary>
/// Reads the *.meta.json files the service writes next to each queued/failed/archived
/// message (see MailQueueWriter / QueueProcessor) and maps them to display rows.
/// Corrupt or mid-write files are skipped — the next refresh picks them up.
/// </summary>
internal static class MailFolderReader
{
    /// <summary>Upper bound so a huge archive folder cannot freeze the UI.</summary>
    internal const int MaxEntries = 500;

    internal static List<MessageRow> ReadFolder(string directory)
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

        return [.. rows.OrderByDescending(r => r.ReceivedAt).Take(MaxEntries)];
    }
}
