using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Service.Infrastructure.Metrics;

/// <summary>
/// SQLite-backed metrics store.
///
/// Schema (created on first use):
///
///   email_events
///     id           TEXT  PRIMARY KEY   – GUID
///     event_type   TEXT  NOT NULL      – 'received' | 'queued' | 'sent' | 'failed'
///     from_addr    TEXT  NOT NULL
///     to_count     INT   NOT NULL
///     to_addrs     TEXT               – comma-separated recipient addresses
///     message_id   TEXT  NOT NULL
///     subject      TEXT               – RFC-2822 Subject header value
///     occurred_at  TEXT  NOT NULL      – ISO 8601 UTC
///     size_bytes   INT   NOT NULL
///     duration_ms  INT   NOT NULL
///     error_detail TEXT               – non-null for 'failed'
///     client_ip    TEXT               – SMTP client IP for 'received' events (top-hosts report)
///
///   perf_metrics
///     id           INT   PRIMARY KEY AUTOINCREMENT
///     metric_type  TEXT  NOT NULL      – 'memory_mb' | 'cpu_percent' | 'disk_free_percent'
///     value        REAL  NOT NULL
///     recorded_at  TEXT  NOT NULL      – ISO 8601 UTC
/// </summary>
internal sealed class MetricsService : IMetricsService
{
    private readonly string _dbPath;
    private readonly IOptionsMonitor<MetricsOptions> _options;
    private readonly ILogger<MetricsService> _logger;

    // SQLite WAL mode supports concurrent reads + one writer; a single lock suffices for writes.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public MetricsService(
        IOptionsMonitor<MetricsOptions> options,
        ILogger<MetricsService> logger)
    {
        _options = options;
        _logger = logger;

        var opts = options.CurrentValue;
        var baseDir = string.IsNullOrEmpty(opts.BasePath)
            ? AppPaths.BaseDir
            : opts.BasePath;
        var dataDir = Path.Combine(baseDir, "data");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "metrics.db");

        InitialiseSchema();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public Task RecordEmailReceivedAsync(string from, IReadOnlyList<string> toAddresses, string messageId, string subject = "", long sizeBytes = 0, int durationMs = 0, string clientIp = "", CancellationToken ct = default)
        => InsertEmailEventAsync("received", from, toAddresses, messageId, subject, sizeBytes, durationMs, error: null, clientIp, ct);

    public Task RecordEmailQueuedAsync(string messageId, CancellationToken ct = default)
        => InsertEmailEventAsync("queued", string.Empty, [], messageId, string.Empty, sizeBytes: 0, durationMs: 0, error: null, clientIp: "", ct);

    public Task RecordEmailSentAsync(string from, IReadOnlyList<string> toAddresses, string messageId, string subject = "", long sizeBytes = 0, int durationMs = 0, CancellationToken ct = default)
        => InsertEmailEventAsync("sent", from, toAddresses, messageId, subject, sizeBytes, durationMs, error: null, clientIp: "", ct);

    public Task RecordEmailFailedAsync(string messageId, string error, string from = "", string subject = "", CancellationToken ct = default)
        => InsertEmailEventAsync("failed", from, [], messageId, subject, sizeBytes: 0, durationMs: 0, error, clientIp: "", ct);

    public async Task RecordPerfMetricAsync(string metricType, double value, CancellationToken ct = default)
    {
        if (!_options.CurrentValue.Enabled) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO perf_metrics (metric_type, value, recorded_at)
                VALUES ($type, $value, $at)
                """;
            cmd.Parameters.AddWithValue("$type", metricType);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to record perf metric {Type}", metricType);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<EmailEventCounts> GetEventCountsAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        // Read-only: WAL mode allows this concurrently with writes — no write lock needed.
        try
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT event_type, COUNT(*)
                FROM email_events
                WHERE occurred_at >= $since
                GROUP BY event_type
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));

            int received = 0, sent = 0, failed = 0;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var count = reader.GetInt32(1);
                switch (reader.GetString(0))
                {
                    case "received": received = count; break;
                    case "sent": sent = count; break;
                    case "failed": failed = count; break;
                }
            }
            return new EmailEventCounts(received, sent, failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to count email events since {Since}", sinceUtc);
            return new EmailEventCounts(0, 0, 0);
        }
    }

    // -------------------------------------------------------------------------
    // Cleanup (called by MetricsCollectorService)
    // -------------------------------------------------------------------------

    internal async Task CleanupOldRecordsAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        var cutoff = DateTime.UtcNow.AddDays(-opts.RetentionDays).ToString("O");

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = OpenConnection();

            await using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "DELETE FROM email_events WHERE occurred_at < $cutoff";
            cmd1.Parameters.AddWithValue("$cutoff", cutoff);
            var emailRows = await cmd1.ExecuteNonQueryAsync(ct);

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "DELETE FROM perf_metrics WHERE recorded_at < $cutoff";
            cmd2.Parameters.AddWithValue("$cutoff", cutoff);
            var perfRows = await cmd2.ExecuteNonQueryAsync(ct);

            if (emailRows + perfRows > 0)
            {
                _logger.LogInformation(
                    "[Metrics] Retention cleanup: removed {Email} email event(s) and {Perf} perf metric(s)",
                    emailRows, perfRows);

                // Reclaim the freed pages — without VACUUM the file only ever grows —
                // and truncate the WAL so it cannot accumulate unbounded either.
                await using var maintenance = conn.CreateCommand();
                maintenance.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
                await maintenance.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Retention cleanup failed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private async Task InsertEmailEventAsync(
        string eventType, string from, IReadOnlyList<string> toAddresses, string messageId,
        string subject, long sizeBytes, int durationMs, string? error, string clientIp, CancellationToken ct)
    {
        if (!_options.CurrentValue.Enabled) return;

        var toAddrs = toAddresses.Count > 0 ? string.Join(", ", toAddresses) : string.Empty;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO email_events (id, event_type, from_addr, to_count, to_addrs, message_id, subject, occurred_at, size_bytes, duration_ms, error_detail, client_ip)
                VALUES ($id, $type, $from, $toCount, $toAddrs, $msgId, $subject, $at, $size, $dur, $error, $clientIp)
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$type", eventType);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$toCount", toAddresses.Count);
            cmd.Parameters.AddWithValue("$toAddrs", toAddrs.Length > 0 ? toAddrs : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$msgId", messageId);
            cmd.Parameters.AddWithValue("$subject", subject.Length > 0 ? subject : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$dur", durationMs);
            cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clientIp", clientIp.Length > 0 ? clientIp : (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to record {EventType} for {MessageId}", eventType, messageId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void InitialiseSchema()
    {
        try
        {
            using var conn = OpenConnection();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS email_events (
                    id           TEXT NOT NULL PRIMARY KEY,
                    event_type   TEXT NOT NULL,
                    from_addr    TEXT NOT NULL DEFAULT '',
                    to_count     INT  NOT NULL DEFAULT 0,
                    to_addrs     TEXT,
                    message_id   TEXT NOT NULL DEFAULT '',
                    subject      TEXT,
                    occurred_at  TEXT NOT NULL,
                    size_bytes   INT  NOT NULL DEFAULT 0,
                    duration_ms  INT  NOT NULL DEFAULT 0,
                    error_detail TEXT,
                    client_ip    TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_email_type_time
                    ON email_events(event_type, occurred_at);

                CREATE TABLE IF NOT EXISTS perf_metrics (
                    id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    metric_type TEXT    NOT NULL,
                    value       REAL    NOT NULL,
                    recorded_at TEXT    NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_perf_type_time
                    ON perf_metrics(metric_type, recorded_at);
                """;
            cmd.ExecuteNonQuery();

            ApplyMigrations(conn);

            _logger.LogInformation("[Metrics] SQLite database ready: {Path}", _dbPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to initialise SQLite database at {Path}", _dbPath);
        }
    }

    // -------------------------------------------------------------------------
    // Schema versioning (PRAGMA user_version)
    // -------------------------------------------------------------------------

    /// <summary>metrics.db schema version understood by this build.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>
    /// Brings an existing database up to <see cref="SchemaVersion"/> via idempotent, forward-only
    /// steps, tracked by <c>PRAGMA user_version</c>. Fresh databases already have the current shape
    /// (from the CREATE statements) and are simply stamped. A database newer than this build is left
    /// as-is with a warning. Table/column names are compile-time constants — no injection risk.
    /// </summary>
    private void ApplyMigrations(SqliteConnection conn)
    {
        var ver = ReadUserVersion(conn);
        if (ver > SchemaVersion)
        {
            _logger.LogWarning(
                "[Metrics] metrics.db schema v{Found} is newer than this build (v{Supported}) — using it as-is",
                ver, SchemaVersion);
            return;
        }
        if (ver == SchemaVersion) return;

        var migrated = false;
        // v0 → v1: client_ip column added for the top-sending-hosts report.
        if (ver < 1) migrated |= EnsureColumn(conn, "email_events", "client_ip", "TEXT");

        SetUserVersion(conn, SchemaVersion);
        if (migrated)
            _logger.LogInformation("[Metrics] Migrated metrics.db schema v{From} → v{To}", ver, SchemaVersion);
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using var c = conn.CreateCommand();
        c.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(c.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        using var c = conn.CreateCommand();
        c.CommandText = $"PRAGMA user_version = {version}";   // PRAGMA values cannot be parameterised; version is an int constant
        c.ExecuteNonQuery();
    }

    /// <summary>Adds <paramref name="column"/> to <paramref name="table"/> when missing. Returns true if it was added.</summary>
    private static bool EnsureColumn(SqliteConnection conn, string table, string column, string type)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var r = check.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return false;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
        return true;
    }
}
