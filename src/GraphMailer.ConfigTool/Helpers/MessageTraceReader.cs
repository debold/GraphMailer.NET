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
    {
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
        catch (Exception)
        {
            return ([], 0, false);
        }
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
