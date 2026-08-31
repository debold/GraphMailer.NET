using System.IO;
using Microsoft.Data.Sqlite;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Reads the message-rule counters from <c>metrics.db</c>. Shared by the Message Rules page (the
/// per-rule hit column) and the Metrics page (the breakdown card), so both show the same numbers
/// from the same query.
///
/// Every read degrades to zeros rather than an error: a database written before the table
/// existed, or a service that has never applied a rule, is the normal case — not something worth
/// putting in front of an operator.
/// </summary>
internal static class MessageRuleStatsReader
{
    /// <param name="RuleName">As configured; the counter is keyed on the name.</param>
    /// <param name="Mode">"Audit" or "Enforce".</param>
    internal readonly record struct RuleCounts(
        string RuleName, string Mode, long Modified, long Rejected, long Discarded, long Skipped)
    {
        internal long Total => Modified + Rejected + Discarded + Skipped;
    }

    /// <summary>
    /// Per-rule, per-mode counters recorded since <paramref name="sinceUtc"/>, most active first.
    /// </summary>
    internal static IReadOnlyList<RuleCounts> Read(string dbPath, DateTime sinceUtc)
    {
        if (!File.Exists(dbPath)) return [];

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            return Read(conn, sinceUtc);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Overload for callers that already hold an open read-only connection.</summary>
    internal static IReadOnlyList<RuleCounts> Read(SqliteConnection conn, DateTime sinceUtc)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT rule_name,
                       mode,
                       COALESCE(SUM(CASE WHEN outcome = 'modified'  THEN count ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN outcome = 'rejected'  THEN count ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN outcome = 'discarded' THEN count ELSE 0 END), 0),
                       COALESCE(SUM(CASE WHEN outcome = 'skipped'   THEN count ELSE 0 END), 0)
                FROM message_rule_hits
                WHERE bucket_hour >= $since
                GROUP BY rule_name, mode
                ORDER BY 3 + 4 + 5 + 6 DESC, rule_name
                """;
            // Must match MetricsService.BucketHour exactly — the comparison is on the string.
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("yyyy-MM-dd'T'HH"));

            var rows = new List<RuleCounts>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new RuleCounts(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5)));
            }
            return rows;
        }
        catch (SqliteException)
        {
            // Database written by an older build, before the table existed.
            return [];
        }
    }

    /// <summary>
    /// Total hits per rule name over the last <paramref name="days"/> days, across both modes.
    /// Used by the rule grid so a rule that never fires is visible where it is edited.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> ReadHitTotals(string dbPath, int days = 30)
    {
        var rows = Read(dbPath, DateTime.UtcNow.AddDays(-days));

        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            totals.TryGetValue(row.RuleName, out var current);
            totals[row.RuleName] = current + (int)Math.Min(int.MaxValue, row.Total);
        }
        return totals;
    }
}
