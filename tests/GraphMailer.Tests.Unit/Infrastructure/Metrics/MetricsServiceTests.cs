using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Infrastructure.Metrics;

public sealed class MetricsServiceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "metricsservice-tests-" + Guid.NewGuid().ToString("N"));

    public MetricsServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* SQLite may still hold file – ignored in test teardown */ }
        }
    }

    private IOptionsMonitor<MetricsOptions> EnabledOptions(int retentionDays = 90)
    {
        var opts = new MetricsOptions { Enabled = true, RetentionDays = retentionDays, BasePath = _tempDir };
        var monitor = Substitute.For<IOptionsMonitor<MetricsOptions>>();
        monitor.CurrentValue.Returns(opts);
        return monitor;
    }

    private MetricsService CreateService(IOptionsMonitor<MetricsOptions>? opts = null)
        => new(opts ?? EnabledOptions(), NullLogger<MetricsService>.Instance);

    private string DbPath => Path.Combine(_tempDir, "data", "metrics.db");

    private static ReceivedEmailEvent Received(string messageId, params string[] to) => new()
    {
        From = "sender@test.com",
        To = to.Length > 0 ? to : ["rcpt@test.com"],
        MessageId = messageId,
    };

    private static SentEmailEvent Sent(string messageId, params string[] to) => new()
    {
        From = "sender@test.com",
        To = to.Length > 0 ? to : ["rcpt@test.com"],
        MessageId = messageId,
    };

    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_CreatesDataDirectory()
    {
        _ = CreateService();
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "data")));
    }

    [Fact]
    public async Task RecordEmailReceivedAsync_Enabled_InsertsRow()
    {
        var svc = CreateService();
        await svc.RecordEmailReceivedAsync(Received("msgid-recv-1", "rcpt@test.com", "rcpt2@test.com"));

        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public async Task RecordEmailSentAsync_Enabled_InsertsRow()
    {
        var svc = CreateService();
        await svc.RecordEmailSentAsync(Sent("msgid-sent-1"));
    }

    [Fact]
    public async Task RecordEmailFailedAsync_Enabled_InsertsRow()
    {
        var svc = CreateService();
        await svc.RecordEmailFailedAsync("msgid-fail-1", "Graph API error");
    }

    [Fact]
    public async Task RecordEmailQueuedAsync_Enabled_InsertsRow()
    {
        var svc = CreateService();
        await svc.RecordEmailQueuedAsync("msgid-queued-1");
    }

    [Fact]
    public async Task RecordPerfMetricAsync_Enabled_InsertsRow()
    {
        var svc = CreateService();
        await svc.RecordPerfMetricAsync("memory_mb", 128.5);
    }

    [Fact]
    public async Task RecordEmailReceivedAsync_Disabled_DoesNotInsert()
    {
        var opts = new MetricsOptions { Enabled = false, BasePath = _tempDir };
        var monitor = Substitute.For<IOptionsMonitor<MetricsOptions>>();
        monitor.CurrentValue.Returns(opts);

        var svc = new MetricsService(monitor, NullLogger<MetricsService>.Instance);

        await svc.RecordEmailReceivedAsync(Received("disabled-id"));
    }

    [Fact]
    public async Task CleanupOldRecordsAsync_RemovesExpiredRows()
    {
        var svc = CreateService();
        await svc.RecordEmailSentAsync(Sent("old-msg"));
        await svc.RecordSmtpSessionAsync(new SmtpSessionRecord
        {
            ClientIp = "10.0.0.1",
            ListenerPort = 25,
            Outcome = SessionOutcome.Clean,
            LastStage = SessionStages.Quit,
        });
        await svc.RecordRejectionAsync(RejectionReasons.IpBlacklist, "10.0.0.9", 25);

        var opts = new MetricsOptions { Enabled = true, RetentionDays = 0, BasePath = _tempDir };
        var monitor = Substitute.For<IOptionsMonitor<MetricsOptions>>();
        monitor.CurrentValue.Returns(opts);

        var svc2 = new MetricsService(monitor, NullLogger<MetricsService>.Instance);
        await svc2.CleanupOldRecordsAsync();

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM email_events"));
        Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM smtp_session_stats"));
        Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM smtp_rejection_stats"));
    }

    [Fact]
    public async Task RecordEmailReceivedAsync_Enabled_StoresRecipientsAndSubject()
    {
        var svc = CreateService();
        await svc.RecordEmailReceivedAsync(Received("msgid-meta-1", "alice@example.com", "bob@example.com")
            with { From = "from@example.com", Subject = "Hello World" });

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_addrs, subject FROM email_events WHERE message_id = 'msgid-meta-1'";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("alice@example.com, bob@example.com", reader.GetString(0));
        Assert.Equal("Hello World", reader.GetString(1));
    }

    [Fact]
    public async Task RecordEmailReceivedAsync_StoresReceptionContext()
    {
        var svc = CreateService();
        await svc.RecordEmailReceivedAsync(new ReceivedEmailEvent
        {
            From = "from@example.com",
            To = ["a@example.com", "b@example.com", "c@example.com"],
            MessageId = "msgid-ctx-1",
            ClientIp = "203.0.113.9",
            ListenerPort = 587,
            Tls = true,
            Authenticated = true,
            AuthUser = "relayuser",
            CcCount = 1,
            BccCount = 1,
            AttachmentCount = 2,
            AttachmentBytes = 4096,
        });

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT client_ip, listener_port, tls, authenticated, auth_user, cc_count, bcc_count, attachment_count, attachment_bytes
            FROM email_events WHERE message_id = 'msgid-ctx-1'
            """;
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("203.0.113.9", reader.GetString(0));
        Assert.Equal(587, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal("relayuser", reader.GetString(4));
        Assert.Equal(1, reader.GetInt32(5));
        Assert.Equal(1, reader.GetInt32(6));
        Assert.Equal(2, reader.GetInt32(7));
        Assert.Equal(4096, reader.GetInt64(8));
    }

    [Fact]
    public async Task RecordEmailSentAsync_StoresDeliveryContext()
    {
        var svc = CreateService();
        await svc.RecordEmailSentAsync(Sent("msgid-del-1") with
        {
            RetryCount = 3,
            DeliveryVariant = "draftUpload",
            QueueLatencyMs = 15_000,
            AttachmentCount = 1,
            AttachmentBytes = 5_000_000,
        });

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT retry_count, delivery_variant, queue_latency_ms, attachment_count, attachment_bytes
            FROM email_events WHERE message_id = 'msgid-del-1'
            """;
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal("draftUpload", reader.GetString(1));
        Assert.Equal(15_000, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(5_000_000, reader.GetInt64(4));
    }

    [Fact]
    public async Task RecordEmailFailedAsync_StoresRetryCountAndPermanentFlag()
    {
        var svc = CreateService();
        await svc.RecordEmailFailedAsync("msgid-perm-1", "HTTP 404 ErrorInvalidUser",
            from: "from@example.com", retryCount: 2, permanent: true);

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT retry_count, permanent FROM email_events WHERE message_id = 'msgid-perm-1'";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    // -------------------------------------------------------------------------
    // Session / rejection aggregates
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RecordSmtpSessionAsync_SameBucketKey_IncrementsCountAndDuration()
    {
        var svc = CreateService();
        var record = new SmtpSessionRecord
        {
            ClientIp = "10.0.0.5",
            ListenerPort = 25,
            Outcome = SessionOutcome.Aborted,
            LastStage = SessionStages.Auth,
            Tls = true,
            Authenticated = true,
            DurationMs = 100,
        };
        await svc.RecordSmtpSessionAsync(record);
        await svc.RecordSmtpSessionAsync(record with { DurationMs = 50 });

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count, total_duration_ms, outcome, last_stage FROM smtp_session_stats
            WHERE client_ip = '10.0.0.5' AND listener_port = 25
            """;
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(150, reader.GetInt64(1));
        Assert.Equal("aborted", reader.GetString(2));
        Assert.Equal("auth", reader.GetString(3));
        Assert.False(reader.Read());   // one aggregate row, not two
    }

    [Fact]
    public async Task RecordSmtpSessionAsync_DifferentOutcome_CreatesSeparateBucketRow()
    {
        var svc = CreateService();
        var record = new SmtpSessionRecord
        {
            ClientIp = "10.0.0.5",
            ListenerPort = 25,
            Outcome = SessionOutcome.Clean,
            LastStage = SessionStages.Quit,
        };
        await svc.RecordSmtpSessionAsync(record);
        await svc.RecordSmtpSessionAsync(record with { Outcome = SessionOutcome.Aborted, LastStage = SessionStages.Ehlo });

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        Assert.Equal(2L, Scalar(conn, "SELECT COUNT(*) FROM smtp_session_stats"));
    }

    [Fact]
    public async Task RecordRejectionAsync_SameBucketKey_IncrementsCount()
    {
        var svc = CreateService();
        await svc.RecordRejectionAsync(RejectionReasons.AuthFailed, "10.0.0.7", 587);
        await svc.RecordRejectionAsync(RejectionReasons.AuthFailed, "10.0.0.7", 587);
        await svc.RecordRejectionAsync(RejectionReasons.IpBlacklist, "10.0.0.7", 587);

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        Assert.Equal(2L, Scalar(conn, "SELECT count FROM smtp_rejection_stats WHERE reason = 'auth_failed'"));
        Assert.Equal(1L, Scalar(conn, "SELECT count FROM smtp_rejection_stats WHERE reason = 'ip_blacklist'"));
    }

    // -------------------------------------------------------------------------
    // Aggregates (telemetry heartbeat)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAggregatesAsync_CountsByType_IgnoringQueued()
    {
        var svc = CreateService();
        await svc.RecordEmailReceivedAsync(Received("cnt-1"));
        await svc.RecordEmailReceivedAsync(Received("cnt-2") with { AttachmentCount = 3 });
        await svc.RecordEmailQueuedAsync("cnt-1");
        await svc.RecordEmailSentAsync(Sent("cnt-1"));
        await svc.RecordEmailSentAsync(Sent("cnt-3") with { RetryCount = 2, DeliveryVariant = "draftUpload", QueueLatencyMs = 400 });
        await svc.RecordEmailFailedAsync("cnt-2", "Graph API error");

        var counts = await svc.GetAggregatesAsync(DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(2, counts.Received);
        Assert.Equal(2, counts.Sent);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.MailsWithAttachments);
        Assert.Equal(1, counts.DeliveredFirstTry);
        Assert.Equal(1, counts.DeliveredAfterRetry);
        Assert.Equal(1, counts.DeliveredViaUpload);
        Assert.Equal(400, counts.AvgQueueLatencyMs);
    }

    [Fact]
    public async Task GetAggregatesAsync_CountsSessionsAndRejections()
    {
        var svc = CreateService();
        await svc.RecordSmtpSessionAsync(new SmtpSessionRecord
        {
            ClientIp = "10.0.0.5", ListenerPort = 25,
            Outcome = SessionOutcome.Clean, LastStage = SessionStages.Quit, Tls = true, Authenticated = true,
        });
        await svc.RecordSmtpSessionAsync(new SmtpSessionRecord
        {
            ClientIp = "10.0.0.5", ListenerPort = 25,
            Outcome = SessionOutcome.Aborted, LastStage = SessionStages.Auth, Tls = true, Authenticated = true,
        });
        await svc.RecordRejectionAsync(RejectionReasons.IpBlacklist, "10.0.0.9", 25);
        await svc.RecordRejectionAsync(RejectionReasons.AuthFailed, "10.0.0.9", 25);
        await svc.RecordRejectionAsync(RejectionReasons.BlockedSender, "10.0.0.9", 25);
        await svc.RecordRejectionAsync(RejectionReasons.BlockedRecipient, "10.0.0.9", 25);
        await svc.RecordRejectionAsync(RejectionReasons.SizeExceeded, "10.0.0.9", 25);

        var counts = await svc.GetAggregatesAsync(DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(2, counts.SessionsTotal);
        Assert.Equal(1, counts.SessionsAborted);
        Assert.Equal(0, counts.SessionsFaulted);
        Assert.Equal(2, counts.SessionsTls);
        Assert.Equal(2, counts.SessionsAuthenticated);
        Assert.Equal(5, counts.RejectionsTotal);
        Assert.Equal(1, counts.RejectedIp);
        Assert.Equal(1, counts.RejectedAuth);
        Assert.Equal(1, counts.RejectedSender);
        Assert.Equal(1, counts.RejectedRecipient);
        Assert.Equal(1, counts.RejectedSize);
    }

    [Fact]
    public async Task GetAggregatesAsync_ExcludesEventsBeforeSince()
    {
        var svc = CreateService();
        await svc.RecordEmailSentAsync(Sent("old-cnt"));

        var counts = await svc.GetAggregatesAsync(DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(0, counts.Sent);
        Assert.Equal(0, counts.SessionsTotal);
    }

    // -------------------------------------------------------------------------
    // Schema versioning
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_FreshDb_StampsCurrentSchemaVersion()
    {
        _ = CreateService();

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = "PRAGMA user_version";
        Assert.Equal(MetricsService.SchemaVersion, Convert.ToInt32(c.ExecuteScalar()));
    }

    [Fact]
    public void Constructor_OldDbWithoutClientIp_MigratesColumnAndStampsVersion()
    {
        // Build a pre-versioning database: email_events without client_ip, user_version 0.
        Directory.CreateDirectory(Path.Combine(_tempDir, "data"));
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText =
                "CREATE TABLE email_events (id TEXT PRIMARY KEY, event_type TEXT, occurred_at TEXT); " +
                "PRAGMA user_version = 0;";
            c.ExecuteNonQuery();
        }

        _ = CreateService(); // InitialiseSchema runs the migration

        using var verify = new SqliteConnection($"Data Source={DbPath}");
        verify.Open();

        var columns = TableColumns(verify, "email_events");
        Assert.Contains("client_ip", columns);
        Assert.Contains("attachment_count", columns);

        using var uv = verify.CreateCommand();
        uv.CommandText = "PRAGMA user_version";
        Assert.Equal(MetricsService.SchemaVersion, Convert.ToInt32(uv.ExecuteScalar()));
    }

    [Fact]
    public void Constructor_V1Db_MigratesToV2KeepingExistingRows()
    {
        // Build a v1 database: email_events in the exact v1 shape plus one row.
        Directory.CreateDirectory(Path.Combine(_tempDir, "data"));
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = """
                CREATE TABLE email_events (
                    id TEXT NOT NULL PRIMARY KEY, event_type TEXT NOT NULL,
                    from_addr TEXT NOT NULL DEFAULT '', to_count INT NOT NULL DEFAULT 0,
                    to_addrs TEXT, message_id TEXT NOT NULL DEFAULT '', subject TEXT,
                    occurred_at TEXT NOT NULL, size_bytes INT NOT NULL DEFAULT 0,
                    duration_ms INT NOT NULL DEFAULT 0, error_detail TEXT, client_ip TEXT);
                INSERT INTO email_events (id, event_type, message_id, occurred_at)
                VALUES ('r1', 'sent', 'legacy-msg', '2026-01-01T00:00:00.0000000Z');
                PRAGMA user_version = 1;
                """;
            c.ExecuteNonQuery();
        }

        _ = CreateService();

        using var verify = new SqliteConnection($"Data Source={DbPath}");
        verify.Open();

        var columns = TableColumns(verify, "email_events");
        foreach (var col in new[]
        {
            "cc_count", "bcc_count", "attachment_count", "attachment_bytes",
            "listener_port", "tls", "authenticated", "auth_user",
            "retry_count", "delivery_variant", "queue_latency_ms", "permanent",
        })
            Assert.Contains(col, columns);

        // Existing data survives, new columns read back with defaults, new tables exist.
        Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM email_events WHERE message_id = 'legacy-msg'"));
        Assert.Equal(0L, Scalar(verify, "SELECT retry_count FROM email_events WHERE message_id = 'legacy-msg'"));
        Assert.Equal(0L, Scalar(verify, "SELECT COUNT(*) FROM smtp_session_stats"));
        Assert.Equal(0L, Scalar(verify, "SELECT COUNT(*) FROM smtp_rejection_stats"));

        using var uv = verify.CreateCommand();
        uv.CommandText = "PRAGMA user_version";
        Assert.Equal(MetricsService.SchemaVersion, Convert.ToInt32(uv.ExecuteScalar()));
    }

    // -------------------------------------------------------------------------

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string> TableColumns(SqliteConnection conn, string table)
    {
        var columns = new List<string>();
        using var ti = conn.CreateCommand();
        ti.CommandText = $"PRAGMA table_info({table})";
        using var r = ti.ExecuteReader();
        while (r.Read()) columns.Add(r.GetString(1));
        return columns;
    }
}
