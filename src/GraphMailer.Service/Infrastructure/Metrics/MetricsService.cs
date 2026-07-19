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
///     id            TEXT  PRIMARY KEY   – GUID
///     event_type    TEXT  NOT NULL      – 'received' | 'queued' | 'sent' | 'failed'
///     from_addr     TEXT  NOT NULL
///     to_count      INT   NOT NULL      – envelope recipient count
///     to_addrs      TEXT               – comma-separated recipient addresses
///     message_id    TEXT  NOT NULL
///     subject       TEXT               – RFC-2822 Subject header value
///     occurred_at   TEXT  NOT NULL      – ISO 8601 UTC
///     size_bytes    INT   NOT NULL
///     duration_ms   INT   NOT NULL
///     error_detail  TEXT               – non-null for 'failed'
///     client_ip     TEXT               – SMTP client IP for 'received' events
///     ── v2, reception context ('received') ──
///     cc_count / bcc_count / attachment_count / attachment_bytes  INT
///     listener_port INT · tls INT · authenticated INT · auth_user TEXT
///     ── v2, delivery context ('sent'/'failed') ──
///     retry_count INT · delivery_variant TEXT ('sendMail'|'draftUpload')
///     queue_latency_ms INT · permanent INT
///
///   smtp_session_stats                 – hourly aggregate, one row per bucket key
///     bucket_hour TEXT ('yyyy-MM-ddTHH' UTC) · listener_port INT · client_ip TEXT
///     outcome TEXT ('clean'|'aborted'|'faulted'|'cancelled') · last_stage TEXT
///     tls INT · authenticated INT · count INT · total_duration_ms INT
///
///   smtp_rejection_stats               – hourly aggregate, one row per bucket key
///     bucket_hour TEXT · listener_port INT · client_ip TEXT · reason TEXT · count INT
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
    // Public API — email events
    // -------------------------------------------------------------------------

    public Task RecordEmailReceivedAsync(ReceivedEmailEvent e, CancellationToken ct = default)
        => InsertEmailEventAsync(new EmailEventRow
        {
            EventType = "received",
            From = e.From,
            To = e.To,
            MessageId = e.MessageId,
            Subject = e.Subject,
            SizeBytes = e.SizeBytes,
            DurationMs = e.DurationMs,
            ClientIp = e.ClientIp,
            CcCount = e.CcCount,
            BccCount = e.BccCount,
            AttachmentCount = e.AttachmentCount,
            AttachmentBytes = e.AttachmentBytes,
            ListenerPort = e.ListenerPort,
            Tls = e.Tls,
            Authenticated = e.Authenticated,
            AuthUser = e.AuthUser,
        }, ct);

    public Task RecordEmailQueuedAsync(string messageId, CancellationToken ct = default)
        => InsertEmailEventAsync(new EmailEventRow { EventType = "queued", MessageId = messageId }, ct);

    public Task RecordEmailSentAsync(SentEmailEvent e, CancellationToken ct = default)
        => InsertEmailEventAsync(new EmailEventRow
        {
            EventType = "sent",
            From = e.From,
            To = e.To,
            MessageId = e.MessageId,
            Subject = e.Subject,
            SizeBytes = e.SizeBytes,
            DurationMs = e.DurationMs,
            RetryCount = e.RetryCount,
            DeliveryVariant = e.DeliveryVariant,
            QueueLatencyMs = e.QueueLatencyMs,
            AttachmentCount = e.AttachmentCount,
            AttachmentBytes = e.AttachmentBytes,
        }, ct);

    public Task RecordEmailFailedAsync(string messageId, string error, string from = "", string subject = "", int retryCount = 0, bool permanent = false, CancellationToken ct = default)
        => InsertEmailEventAsync(new EmailEventRow
        {
            EventType = "failed",
            From = from,
            MessageId = messageId,
            Subject = subject,
            Error = error,
            RetryCount = retryCount,
            Permanent = permanent,
        }, ct);

    // -------------------------------------------------------------------------
    // Public API — session / rejection aggregates
    // -------------------------------------------------------------------------

    public async Task RecordSmtpSessionAsync(SmtpSessionRecord r, CancellationToken ct = default)
    {
        if (!_options.CurrentValue.Enabled) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO smtp_session_stats
                    (bucket_hour, listener_port, client_ip, outcome, last_stage, tls, authenticated, count, total_duration_ms)
                VALUES ($bucket, $port, $ip, $outcome, $stage, $tls, $auth, 1, $dur)
                ON CONFLICT(bucket_hour, listener_port, client_ip, outcome, last_stage, tls, authenticated)
                DO UPDATE SET count = count + 1, total_duration_ms = total_duration_ms + $dur
                """;
            cmd.Parameters.AddWithValue("$bucket", BucketHour(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("$port", r.ListenerPort);
            cmd.Parameters.AddWithValue("$ip", r.ClientIp);
            cmd.Parameters.AddWithValue("$outcome", OutcomeToDb(r.Outcome));
            cmd.Parameters.AddWithValue("$stage", r.LastStage);
            cmd.Parameters.AddWithValue("$tls", r.Tls ? 1 : 0);
            cmd.Parameters.AddWithValue("$auth", r.Authenticated ? 1 : 0);
            cmd.Parameters.AddWithValue("$dur", r.DurationMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to record SMTP session for {Ip}", r.ClientIp);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordRejectionAsync(string reason, string clientIp, int listenerPort, CancellationToken ct = default)
    {
        if (!_options.CurrentValue.Enabled) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO smtp_rejection_stats (bucket_hour, listener_port, client_ip, reason, count)
                VALUES ($bucket, $port, $ip, $reason, 1)
                ON CONFLICT(bucket_hour, listener_port, client_ip, reason)
                DO UPDATE SET count = count + 1
                """;
            cmd.Parameters.AddWithValue("$bucket", BucketHour(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("$port", listenerPort);
            cmd.Parameters.AddWithValue("$ip", clientIp);
            cmd.Parameters.AddWithValue("$reason", reason);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to record rejection {Reason}", reason);
        }
        finally
        {
            _writeLock.Release();
        }
    }

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

    // -------------------------------------------------------------------------
    // Public API — aggregate read (telemetry heartbeat)
    // -------------------------------------------------------------------------

    public async Task<MetricsAggregates> GetAggregatesAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        // Read-only: WAL mode allows this concurrently with writes — no write lock needed.
        try
        {
            await using var conn = OpenConnection();
            var sinceIso = sinceUtc.ToString("O");
            var sinceBucket = BucketHour(sinceUtc);

            var result = new MetricsAggregates();

            // Email event counters (one grouped pass).
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT event_type,
                           COUNT(*),
                           SUM(CASE WHEN attachment_count > 0 THEN 1 ELSE 0 END),
                           SUM(CASE WHEN retry_count = 0 THEN 1 ELSE 0 END),
                           SUM(CASE WHEN retry_count > 0 THEN 1 ELSE 0 END),
                           SUM(CASE WHEN delivery_variant = 'draftUpload' THEN 1 ELSE 0 END),
                           AVG(CASE WHEN queue_latency_ms > 0 THEN queue_latency_ms END)
                    FROM email_events
                    WHERE occurred_at >= $since
                    GROUP BY event_type
                    """;
                cmd.Parameters.AddWithValue("$since", sinceIso);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var count = reader.GetInt32(1);
                    switch (reader.GetString(0))
                    {
                        case "received":
                            result = result with
                            {
                                Received = count,
                                MailsWithAttachments = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                            };
                            break;
                        case "sent":
                            result = result with
                            {
                                Sent = count,
                                DeliveredFirstTry = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                DeliveredAfterRetry = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                                DeliveredViaUpload = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                                AvgQueueLatencyMs = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                            };
                            break;
                        case "failed":
                            result = result with { Failed = count };
                            break;
                    }
                }
            }

            // Session counters.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(SUM(count), 0),
                           COALESCE(SUM(CASE WHEN outcome = 'aborted' THEN count ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN outcome = 'faulted' THEN count ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN tls = 1 THEN count ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN authenticated = 1 THEN count ELSE 0 END), 0)
                    FROM smtp_session_stats
                    WHERE bucket_hour >= $since
                    """;
                cmd.Parameters.AddWithValue("$since", sinceBucket);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    result = result with
                    {
                        SessionsTotal = reader.GetInt32(0),
                        SessionsAborted = reader.GetInt32(1),
                        SessionsFaulted = reader.GetInt32(2),
                        SessionsTls = reader.GetInt32(3),
                        SessionsAuthenticated = reader.GetInt32(4),
                    };
                }
            }

            // Rejection counters, grouped for telemetry (no per-reason detail needed there).
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(SUM(count), 0),
                           COALESCE(SUM(CASE WHEN reason IN ('ip_blacklist','ip_not_whitelisted','ip_blocked') THEN count ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN reason IN ('auth_required','auth_failed','prior_auth_failed') THEN count ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN reason IN ('from_restriction','blocked_sender','unknown_sender','sender_validation_unavailable') THEN count ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN reason = 'blocked_recipient' THEN count ELSE 0 END), 0),
                           COALESCE(SUM(CASE WHEN reason = 'size_exceeded' THEN count ELSE 0 END), 0)
                    FROM smtp_rejection_stats
                    WHERE bucket_hour >= $since
                    """;
                cmd.Parameters.AddWithValue("$since", sinceBucket);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    result = result with
                    {
                        RejectionsTotal = reader.GetInt32(0),
                        RejectedIp = reader.GetInt32(1),
                        RejectedAuth = reader.GetInt32(2),
                        RejectedSender = reader.GetInt32(3),
                        RejectedRecipient = reader.GetInt32(4),
                        RejectedSize = reader.GetInt32(5),
                    };
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to aggregate metrics since {Since}", sinceUtc);
            return new MetricsAggregates();
        }
    }

    // -------------------------------------------------------------------------
    // Cleanup (called by MetricsCollectorService)
    // -------------------------------------------------------------------------

    internal async Task CleanupOldRecordsAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        var cutoffUtc = DateTime.UtcNow.AddDays(-opts.RetentionDays);
        var cutoff = cutoffUtc.ToString("O");
        // Bucket tables use '<=': the bucket containing the cutoff hour is removed as
        // well (retention off by at most 1 h — irrelevant at day-scale retention).
        var cutoffBucket = BucketHour(cutoffUtc);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = OpenConnection();

            var removed = 0;
            foreach (var (table, column, op, value) in new[]
            {
                ("email_events", "occurred_at", "<", cutoff),
                ("perf_metrics", "recorded_at", "<", cutoff),
                ("smtp_session_stats", "bucket_hour", "<=", cutoffBucket),
                ("smtp_rejection_stats", "bucket_hour", "<=", cutoffBucket),
            })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE {column} {op} $cutoff";   // names/operators are compile-time constants
                cmd.Parameters.AddWithValue("$cutoff", value);
                removed += await cmd.ExecuteNonQueryAsync(ct);
            }

            if (removed > 0)
            {
                _logger.LogInformation(
                    "[Metrics] Retention cleanup: removed {Count} expired row(s) across all metric tables",
                    removed);

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

    /// <summary>Flat row model for email_events inserts (internal to keep one INSERT path).</summary>
    private sealed record EmailEventRow
    {
        public required string EventType { get; init; }
        public string From { get; init; } = string.Empty;
        public IReadOnlyList<string> To { get; init; } = [];
        public required string MessageId { get; init; }
        public string Subject { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public int DurationMs { get; init; }
        public string? Error { get; init; }
        public string ClientIp { get; init; } = string.Empty;
        public int CcCount { get; init; }
        public int BccCount { get; init; }
        public int AttachmentCount { get; init; }
        public long AttachmentBytes { get; init; }
        public int ListenerPort { get; init; }
        public bool Tls { get; init; }
        public bool Authenticated { get; init; }
        public string AuthUser { get; init; } = string.Empty;
        public int RetryCount { get; init; }
        public string DeliveryVariant { get; init; } = string.Empty;
        public long QueueLatencyMs { get; init; }
        public bool Permanent { get; init; }
    }

    private async Task InsertEmailEventAsync(EmailEventRow row, CancellationToken ct)
    {
        if (!_options.CurrentValue.Enabled) return;

        var toAddrs = row.To.Count > 0 ? string.Join(", ", row.To) : string.Empty;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO email_events (
                    id, event_type, from_addr, to_count, to_addrs, message_id, subject, occurred_at,
                    size_bytes, duration_ms, error_detail, client_ip,
                    cc_count, bcc_count, attachment_count, attachment_bytes,
                    listener_port, tls, authenticated, auth_user,
                    retry_count, delivery_variant, queue_latency_ms, permanent)
                VALUES (
                    $id, $type, $from, $toCount, $toAddrs, $msgId, $subject, $at,
                    $size, $dur, $error, $clientIp,
                    $ccCount, $bccCount, $attCount, $attBytes,
                    $port, $tls, $auth, $authUser,
                    $retries, $variant, $latency, $permanent)
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$type", row.EventType);
            cmd.Parameters.AddWithValue("$from", row.From);
            cmd.Parameters.AddWithValue("$toCount", row.To.Count);
            cmd.Parameters.AddWithValue("$toAddrs", toAddrs.Length > 0 ? toAddrs : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$msgId", row.MessageId);
            cmd.Parameters.AddWithValue("$subject", row.Subject.Length > 0 ? row.Subject : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$size", row.SizeBytes);
            cmd.Parameters.AddWithValue("$dur", row.DurationMs);
            cmd.Parameters.AddWithValue("$error", (object?)row.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clientIp", row.ClientIp.Length > 0 ? row.ClientIp : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$ccCount", row.CcCount);
            cmd.Parameters.AddWithValue("$bccCount", row.BccCount);
            cmd.Parameters.AddWithValue("$attCount", row.AttachmentCount);
            cmd.Parameters.AddWithValue("$attBytes", row.AttachmentBytes);
            cmd.Parameters.AddWithValue("$port", row.ListenerPort);
            cmd.Parameters.AddWithValue("$tls", row.Tls ? 1 : 0);
            cmd.Parameters.AddWithValue("$auth", row.Authenticated ? 1 : 0);
            cmd.Parameters.AddWithValue("$authUser", row.AuthUser.Length > 0 ? row.AuthUser : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$retries", row.RetryCount);
            cmd.Parameters.AddWithValue("$variant", row.DeliveryVariant.Length > 0 ? row.DeliveryVariant : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$latency", row.QueueLatencyMs);
            cmd.Parameters.AddWithValue("$permanent", row.Permanent ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to record {EventType} for {MessageId}", row.EventType, row.MessageId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Hourly aggregation bucket key: 'yyyy-MM-ddTHH' in UTC.</summary>
    internal static string BucketHour(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH");

    private static string OutcomeToDb(SessionOutcome outcome) => outcome switch
    {
        SessionOutcome.Clean => "clean",
        SessionOutcome.Aborted => "aborted",
        SessionOutcome.Faulted => "faulted",
        SessionOutcome.Cancelled => "cancelled",
        _ => "unknown",
    };

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
                    id               TEXT NOT NULL PRIMARY KEY,
                    event_type       TEXT NOT NULL,
                    from_addr        TEXT NOT NULL DEFAULT '',
                    to_count         INT  NOT NULL DEFAULT 0,
                    to_addrs         TEXT,
                    message_id       TEXT NOT NULL DEFAULT '',
                    subject          TEXT,
                    occurred_at      TEXT NOT NULL,
                    size_bytes       INT  NOT NULL DEFAULT 0,
                    duration_ms      INT  NOT NULL DEFAULT 0,
                    error_detail     TEXT,
                    client_ip        TEXT,
                    cc_count         INT  NOT NULL DEFAULT 0,
                    bcc_count        INT  NOT NULL DEFAULT 0,
                    attachment_count INT  NOT NULL DEFAULT 0,
                    attachment_bytes INT  NOT NULL DEFAULT 0,
                    listener_port    INT  NOT NULL DEFAULT 0,
                    tls              INT  NOT NULL DEFAULT 0,
                    authenticated    INT  NOT NULL DEFAULT 0,
                    auth_user        TEXT,
                    retry_count      INT  NOT NULL DEFAULT 0,
                    delivery_variant TEXT,
                    queue_latency_ms INT  NOT NULL DEFAULT 0,
                    permanent        INT  NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS idx_email_type_time
                    ON email_events(event_type, occurred_at);

                CREATE TABLE IF NOT EXISTS smtp_session_stats (
                    bucket_hour       TEXT NOT NULL,
                    listener_port     INT  NOT NULL,
                    client_ip         TEXT NOT NULL,
                    outcome           TEXT NOT NULL,
                    last_stage        TEXT NOT NULL,
                    tls               INT  NOT NULL,
                    authenticated     INT  NOT NULL,
                    count             INT  NOT NULL DEFAULT 0,
                    total_duration_ms INT  NOT NULL DEFAULT 0,
                    UNIQUE(bucket_hour, listener_port, client_ip, outcome, last_stage, tls, authenticated)
                );

                CREATE INDEX IF NOT EXISTS idx_session_bucket
                    ON smtp_session_stats(bucket_hour);

                CREATE TABLE IF NOT EXISTS smtp_rejection_stats (
                    bucket_hour   TEXT NOT NULL,
                    listener_port INT  NOT NULL,
                    client_ip     TEXT NOT NULL,
                    reason        TEXT NOT NULL,
                    count         INT  NOT NULL DEFAULT 0,
                    UNIQUE(bucket_hour, listener_port, client_ip, reason)
                );

                CREATE INDEX IF NOT EXISTS idx_rejection_bucket
                    ON smtp_rejection_stats(bucket_hour);

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
    internal const int SchemaVersion = 2;

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
        // v1 → v2: reception/delivery context columns; the smtp_session_stats and
        // smtp_rejection_stats tables are created by InitialiseSchema (IF NOT EXISTS).
        if (ver < 2)
        {
            migrated |= EnsureColumn(conn, "email_events", "cc_count", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "bcc_count", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "attachment_count", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "attachment_bytes", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "listener_port", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "tls", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "authenticated", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "auth_user", "TEXT");
            migrated |= EnsureColumn(conn, "email_events", "retry_count", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "delivery_variant", "TEXT");
            migrated |= EnsureColumn(conn, "email_events", "queue_latency_ms", "INT NOT NULL DEFAULT 0");
            migrated |= EnsureColumn(conn, "email_events", "permanent", "INT NOT NULL DEFAULT 0");
        }

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
