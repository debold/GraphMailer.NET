using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Infrastructure.Metrics;

/// <summary>
/// The metrics.db connection string must survive any legal Windows path. A semicolon is legal in
/// a folder name, and the data directory can be moved (GRAPHMAILER_DATA_DIR, Metrics:BasePath);
/// an interpolated "Data Source=…" connection string broke at the first semicolon.
/// </summary>
public sealed class MetricsDatabaseTests : IDisposable
{
    // The semicolon in the folder name is the point of this fixture.
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "metricsdb-tests;" + Guid.NewGuid().ToString("N"));

    public MetricsDatabaseTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* SQLite may still hold the file — ignored in test teardown */ }
    }

    [Fact]
    public void ConnectionString_PathWithSemicolon_KeepsTheWholePathAsDataSource()
    {
        var path = Path.Combine(_tempDir, "metrics.db");

        var parsed = new SqliteConnectionStringBuilder(MetricsDatabase.ConnectionString(path));

        parsed.DataSource.Should().Be(path);
    }

    [Fact]
    public void ConnectionString_Default_OpensReadWriteCreate()
    {
        var parsed = new SqliteConnectionStringBuilder(MetricsDatabase.ConnectionString(@"C:\data\metrics.db"));

        parsed.Mode.Should().Be(SqliteOpenMode.ReadWriteCreate);
    }

    [Fact]
    public void ConnectionString_ReadOnly_OpensReadOnly()
    {
        var parsed = new SqliteConnectionStringBuilder(
            MetricsDatabase.ConnectionString(@"C:\data\metrics.db", readOnly: true));

        parsed.Mode.Should().Be(SqliteOpenMode.ReadOnly);
    }

    [Fact]
    public async Task MetricsService_BasePathWithSemicolon_WritesEventsThatReadOnlyConnectionsCanRead()
    {
        var options = Substitute.For<IOptionsMonitor<MetricsOptions>>();
        options.CurrentValue.Returns(new MetricsOptions { Enabled = true, BasePath = _tempDir });
        var service = new MetricsService(options, NullLogger<MetricsService>.Instance);

        await service.RecordEmailReceivedAsync(new ReceivedEmailEvent
        {
            From = "sender@test.com",
            To = ["rcpt@test.com"],
            MessageId = "semicolon-path",
        });

        var dbPath = Path.Combine(_tempDir, "data", "metrics.db");
        using var conn = new SqliteConnection(MetricsDatabase.ConnectionString(dbPath, readOnly: true));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM email_events WHERE message_id = 'semicolon-path'";
        Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(1);
    }
}
