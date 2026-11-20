using System.Globalization;
using System.IO;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure.Metrics;
using Microsoft.Data.Sqlite;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>Which messages the Messages page lists.</summary>
internal enum MessageListFilter
{
    All,
    /// <summary>Accepted and still waiting for delivery.</summary>
    Queue,
    /// <summary>Needed more than one delivery attempt: delivered late or still retrying.</summary>
    Delayed,
    Failed,
    /// <summary>Refused during the SMTP session, or discarded by a rule.</summary>
    Rejected,
    Sent,
}

/// <summary>One step of a message's history as stored by the service.</summary>
internal sealed record TraceEventRow(
    DateTime OccurredAt, string Stage, string Outcome, string Summary, string? Detail, int? Attempt);

/// <summary>How a message arrived, from the metrics recorded before message history existed.</summary>
internal sealed record ReceiptFacts(int ListenerPort, bool Tls, string AuthUser, long SizeBytes);

/// <summary>
/// Reads the message history (<c>message_traces</c>, <c>message_trace_events</c>) from
/// <c>metrics.db</c>. Read-only, and every read degrades to "nothing" instead of an error: a
/// database from before 1.6.0, a service that has not started yet or metrics switched off are all
/// normal and only mean the page falls back to the mail folders.
/// </summary>
internal static class MessageTraceReader
{
    /// <summary>The newest list entries matching the filter and search, plus the total and whether more exist.</summary>
    internal static (List<MessageRow> Rows, int Total, bool HasMore) ReadList(
        string dbPath, MessageListFilter filter, string? search, int limit)
        => ReadList(dbPath, filter, search, limit, out _);

    /// <summary>
    /// As <see cref="ReadList(string, MessageListFilter, string?, int)"/>, and says why nothing could be
    /// read: <paramref name="error"/> is set when the database exists but reading it failed, so the
    /// page can show the reason instead of an empty list that looks like "no mail".
    /// </summary>
    internal static (List<MessageRow> Rows, int Total, bool HasMore) ReadList(
        string dbPath, MessageListFilter filter, string? search, int limit, out string? error)
    {
        error = null;
        if (!File.Exists(dbPath)) return ([], 0, false);

        try
        {
            using var conn = Open(dbPath);
            if (!TableExists(conn, "message_traces")) return ([], 0, false);

            var where = new List<string>();
            using var cmd = conn.CreateCommand();

            var statusClause = StatusClause(filter);
            if (statusClause is not null) where.Add(statusClause);

            if (!string.IsNullOrWhiteSpace(search))
            {
                where.Add("""
                    (from_addr LIKE $s ESCAPE '\' OR to_addrs LIKE $s ESCAPE '\' OR subject LIKE $s ESCAPE '\'
                     OR client_ip LIKE $s ESCAPE '\' OR message_id LIKE $s ESCAPE '\' OR status LIKE $s ESCAPE '\'
                     OR message_id IN (SELECT message_id FROM message_trace_events
                                       WHERE summary LIKE $s ESCAPE '\' OR detail LIKE $s ESCAPE '\'))
                    """);
                cmd.Parameters.AddWithValue("$s", "%" + EscapeLike(search.Trim()) + "%");
            }

            var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;

            cmd.CommandText = $"""
                SELECT message_id, received_at, from_addr, to_addrs, subject, client_ip, size_bytes,
                       status, attempts, finished_at
                FROM message_traces
                {whereSql}
                ORDER BY received_at DESC
                LIMIT $limit
                """;   // whereSql is assembled from constant fragments; the search term is a parameter
            cmd.Parameters.AddWithValue("$limit", limit + 1);

            var rows = new List<MessageRow>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    rows.Add(ToRow(r));
            }

            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM message_traces";
            var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

            var hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            return (rows, total, hasMore);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return ([], 0, false);
        }
    }

    /// <summary>Status of a message the metrics saw arrive, but whose outcome they never recorded.</summary>
    internal const string UnknownStatus = "unknown";

    /// <summary>
    /// Messages that have no recorded history (handled before 1.6.0), rebuilt from the per-message
    /// <c>email_events</c> rows the metrics have always kept: receipt, delivery and final failure.
    /// This is what makes older messages visible when the mail folders hold nothing, because
    /// sent mail is not archived (<c>MailQueue:ArchiveSentEmails</c> off, the default).
    /// Queue and Rejected return nothing: queued messages come from the queue folder, and
    /// rejections were never recorded per message before 1.6.0.
    /// </summary>
    internal static (List<MessageRow> Rows, int Total, bool HasMore) ReadMetricsList(
        string dbPath, MessageListFilter filter, string? search, int limit, out string? error)
    {
        error = null;
        if (filter is MessageListFilter.Queue or MessageListFilter.Rejected) return ([], 0, false);
        if (!File.Exists(dbPath)) return ([], 0, false);

        try
        {
            using var conn = Open(dbPath);
            if (!TableExists(conn, "email_events")) return ([], 0, false);

            // Messages with a history are listed by ReadList; never list one twice.
            var notTraced = TableExists(conn, "message_traces")
                ? "AND NOT EXISTS (SELECT 1 FROM message_traces t WHERE t.message_id = r.message_id)"
                : string.Empty;

            var where = new List<string>();
            using var cmd = conn.CreateCommand();

            var outcome = filter switch
            {
                MessageListFilter.Sent => "sent_at IS NOT NULL",
                MessageListFilter.Delayed => "sent_at IS NOT NULL AND sent_retries > 0",
                MessageListFilter.Failed => "sent_at IS NULL AND failed_at IS NOT NULL",
                _ => null,
            };
            if (outcome is not null) where.Add(outcome);

            if (!string.IsNullOrWhiteSpace(search))
            {
                where.Add("""
                    (from_addr LIKE $s ESCAPE '\' OR to_addrs LIKE $s ESCAPE '\' OR subject LIKE $s ESCAPE '\'
                     OR client_ip LIKE $s ESCAPE '\' OR message_id LIKE $s ESCAPE '\' OR failed_error LIKE $s ESCAPE '\')
                    """);
                cmd.Parameters.AddWithValue("$s", "%" + EscapeLike(search.Trim()) + "%");
            }

            var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;

            // The outcome rows are aggregated once each, not looked up per received row: the
            // table holds every message of the retention period.
            var baseSql = $"""
                WITH s AS (SELECT message_id, MAX(occurred_at) AS sent_at, MAX(retry_count) AS sent_retries
                           FROM email_events WHERE event_type = 'sent' GROUP BY message_id),
                     f AS (SELECT message_id, MAX(occurred_at) AS failed_at, MAX(retry_count) AS failed_retries,
                                  MAX(error_detail) AS failed_error
                           FROM email_events WHERE event_type = 'failed' GROUP BY message_id),
                     m AS (SELECT r.message_id, r.occurred_at AS received_at, r.from_addr, r.to_addrs, r.subject,
                                  r.client_ip, r.size_bytes, s.sent_at, s.sent_retries,
                                  f.failed_at, f.failed_retries, f.failed_error
                           FROM email_events r
                           LEFT JOIN s ON s.message_id = r.message_id
                           LEFT JOIN f ON f.message_id = r.message_id
                           WHERE r.event_type = 'received' AND r.message_id <> '' {notTraced})
                """;   // notTraced and whereSql are constant fragments; the search term is a parameter

            cmd.CommandText = baseSql + $"""
                SELECT message_id, received_at, from_addr, to_addrs, subject, client_ip, size_bytes,
                       sent_at, sent_retries, failed_at, failed_retries, failed_error
                FROM m
                {whereSql}
                ORDER BY received_at DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit + 1);

            var rows = new List<MessageRow>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    rows.Add(ToMetricsRow(r));
            }

            using var count = conn.CreateCommand();
            count.CommandText = baseSql + "SELECT COUNT(*) FROM m";
            var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

            var hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            return (rows, total, hasMore);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return ([], 0, false);
        }
    }

    private static MessageRow ToMetricsRow(SqliteDataReader r)
    {
        var sentAt = r.IsDBNull(7) ? (DateTime?)null : ParseUtc(r.GetString(7));
        var failedAt = r.IsDBNull(9) ? (DateTime?)null : ParseUtc(r.GetString(9));
        // retry_count is the number of failed attempts before the one that ended it.
        var retries = sentAt is not null
            ? (r.IsDBNull(8) ? 0 : r.GetInt32(8))
            : (r.IsDBNull(10) ? 0 : r.GetInt32(10));
        var status = sentAt is not null ? TraceStatuses.Sent
            : failedAt is not null ? TraceStatuses.Failed
            : UnknownStatus;
        return new MessageRow
        {
            MessageId = r.GetString(0),
            ReceivedAt = ParseUtc(r.GetString(1)),
            From = r.IsDBNull(2) ? string.Empty : r.GetString(2),
            To = r.IsDBNull(3) ? string.Empty : r.GetString(3),
            Subject = r.IsDBNull(4) ? string.Empty : r.GetString(4),
            ClientIp = r.IsDBNull(5) ? string.Empty : r.GetString(5),
            SizeBytes = r.IsDBNull(6) ? 0 : r.GetInt64(6),
            Status = status,
            Attempts = status == UnknownStatus
                ? string.Empty
                : (retries + (status == TraceStatuses.Sent ? 1 : 0)).ToString(CultureInfo.InvariantCulture),
            FailedAttempts = retries,
            SentAt = sentAt,
            LastAttemptAt = failedAt,
            LastError = r.IsDBNull(11) ? string.Empty : r.GetString(11),
            HasTrace = false,
        };
    }

    /// <summary>The recorded steps of one message, oldest first.</summary>
    internal static List<TraceEventRow> ReadEvents(string dbPath, string messageId)
    {
        if (!File.Exists(dbPath)) return [];

        try
        {
            using var conn = Open(dbPath);
            if (!TableExists(conn, "message_trace_events")) return [];

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT occurred_at, stage, outcome, summary, detail, attempt
                FROM message_trace_events
                WHERE message_id = $id
                ORDER BY occurred_at, id
                """;
            cmd.Parameters.AddWithValue("$id", messageId);

            var events = new List<TraceEventRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                events.Add(new TraceEventRow(
                    ParseUtc(r.GetString(0)),
                    r.GetString(1),
                    r.GetString(2),
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetInt32(5)));
            }
            return events;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Reception facts from the <c>received</c> metrics row, for messages without history.
    /// Null when the metrics never saw the message (metrics off, or older than their retention).
    /// </summary>
    internal static ReceiptFacts? ReadReceipt(string dbPath, string messageId)
    {
        if (!File.Exists(dbPath)) return null;

        try
        {
            using var conn = Open(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT listener_port, tls, auth_user, size_bytes
                FROM email_events
                WHERE message_id = $id AND event_type = 'received'
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$id", messageId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ReceiptFacts(
                r.GetInt32(0), r.GetInt32(1) != 0, r.IsDBNull(2) ? string.Empty : r.GetString(2), r.GetInt64(3));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>SQL condition for a filter; null for <see cref="MessageListFilter.All"/>. Constant text only.</summary>
    internal static string? StatusClause(MessageListFilter filter) => filter switch
    {
        MessageListFilter.Queue => "status = 'queued'",
        MessageListFilter.Delayed => "((status = 'sent' AND attempts > 1) OR (status = 'queued' AND attempts > 0))",
        MessageListFilter.Failed => "status = 'failed'",
        MessageListFilter.Rejected => "status IN ('rejected', 'discarded')",
        MessageListFilter.Sent => "status = 'sent'",
        _ => null,
    };

    private static MessageRow ToRow(SqliteDataReader r)
    {
        var status = r.GetString(7);
        var attempts = r.GetInt32(8);
        var finishedAt = r.IsDBNull(9) ? (DateTime?)null : ParseUtc(r.GetString(9));
        return new MessageRow
        {
            MessageId = r.GetString(0),
            ReceivedAt = ParseUtc(r.GetString(1)),
            From = r.GetString(2),
            To = r.IsDBNull(3) ? string.Empty : r.GetString(3),
            Subject = r.IsDBNull(4) ? string.Empty : r.GetString(4),
            ClientIp = r.IsDBNull(5) ? string.Empty : r.GetString(5),
            SizeBytes = r.GetInt64(6),
            Status = status,
            Attempts = attempts.ToString(CultureInfo.InvariantCulture),
            // A delivered message's count includes the attempt that worked.
            FailedAttempts = status == TraceStatuses.Sent ? Math.Max(0, attempts - 1) : attempts,
            SentAt = status == TraceStatuses.Sent ? finishedAt : null,
            HasTrace = true,
        };
    }

    /// <summary>Stored as UTC ISO 8601; shown in local time like the rest of the page.</summary>
    internal static DateTime ParseUtc(string iso)
        => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            .ToLocalTime();

    private static string EscapeLike(string term)
        => term.Replace(@"\", @"\\", StringComparison.Ordinal)
               .Replace("%", @"\%", StringComparison.Ordinal)
               .Replace("_", @"\_", StringComparison.Ordinal);

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(MetricsDatabase.ConnectionString(dbPath, readOnly: true));
        conn.Open();
        return conn;
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t";
        cmd.Parameters.AddWithValue("$t", table);
        return cmd.ExecuteScalar() is not null;
    }
}
