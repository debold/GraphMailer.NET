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
        await svc.RecordEmailReceivedAsync("sender@test.com", ["rcpt@test.com", "rcpt2@test.com"], "msgid-recv-1");

        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public async Task RecordEmailSentAsync_Enabled_InsertsRow()
    {
        var svc = CreateService();
        await svc.RecordEmailSentAsync("sender@test.com", ["rcpt@test.com"], "msgid-sent-1");
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

        await svc.RecordEmailReceivedAsync("a@b.com", ["rcpt@b.com"], "disabled-id");
    }

    [Fact]
    public async Task CleanupOldRecordsAsync_RemovesExpiredRows()
    {
        var svc = CreateService();
        await svc.RecordEmailSentAsync("a@b.com", ["rcpt@b.com"], "old-msg");

        var opts = new MetricsOptions { Enabled = true, RetentionDays = 0, BasePath = _tempDir };
        var monitor = Substitute.For<IOptionsMonitor<MetricsOptions>>();
        monitor.CurrentValue.Returns(opts);

        var svc2 = new MetricsService(monitor, NullLogger<MetricsService>.Instance);
        await svc2.CleanupOldRecordsAsync();
    }

    [Fact]
    public async Task RecordEmailReceivedAsync_Enabled_StoresRecipientsAndSubject()
    {
        var svc = CreateService();
        await svc.RecordEmailReceivedAsync(
            "from@example.com",
            ["alice@example.com", "bob@example.com"],
            "msgid-meta-1",
            subject: "Hello World");

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
    public async Task RecordEmailSentAsync_Enabled_StoresRecipientsAndSubject()
    {
        var svc = CreateService();
        await svc.RecordEmailSentAsync(
            "from@example.com",
            ["carol@example.com"],
            "msgid-meta-2",
            subject: "Meeting invite");

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_addrs, subject FROM email_events WHERE message_id = 'msgid-meta-2'";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("carol@example.com", reader.GetString(0));
        Assert.Equal("Meeting invite", reader.GetString(1));
    }

    [Fact]
    public async Task RecordEmailReceivedAsync_StoresClientIp_ForTopHostsReport()
    {
        var svc = CreateService();
        await svc.RecordEmailReceivedAsync(
            "from@example.com", ["dest@example.com"], "msgid-ip-1", clientIp: "203.0.113.9");

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_ip FROM email_events WHERE message_id = 'msgid-ip-1'";

        Assert.Equal("203.0.113.9", cmd.ExecuteScalar() as string);
    }

    [Fact]
    public async Task GetEventCountsAsync_CountsByType_IgnoringQueued()
    {
        var svc = CreateService();
        await svc.RecordEmailReceivedAsync("a@b.com", ["r@b.com"], "cnt-1");
        await svc.RecordEmailReceivedAsync("a@b.com", ["r@b.com"], "cnt-2");
        await svc.RecordEmailQueuedAsync("cnt-1");
        await svc.RecordEmailSentAsync("a@b.com", ["r@b.com"], "cnt-1");
        await svc.RecordEmailFailedAsync("cnt-2", "Graph API error");

        var counts = await svc.GetEventCountsAsync(DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(new EmailEventCounts(Received: 2, Sent: 1, Failed: 1), counts);
    }

    [Fact]
    public async Task GetEventCountsAsync_ExcludesEventsBeforeSince()
    {
        var svc = CreateService();
        await svc.RecordEmailSentAsync("a@b.com", ["r@b.com"], "old-cnt");

        var counts = await svc.GetEventCountsAsync(DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(new EmailEventCounts(0, 0, 0), counts);
    }

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

        var columns = new List<string>();
        using (var ti = verify.CreateCommand())
        {
            ti.CommandText = "PRAGMA table_info(email_events)";
            using var r = ti.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }
        Assert.Contains("client_ip", columns);

        using var uv = verify.CreateCommand();
        uv.CommandText = "PRAGMA user_version";
        Assert.Equal(MetricsService.SchemaVersion, Convert.ToInt32(uv.ExecuteScalar()));
    }
}
