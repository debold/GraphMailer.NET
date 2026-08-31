using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Reading the rule counters back out of metrics.db.
///
/// The degradation cases carry the weight: a missing database, or one written before the table
/// existed, is the ordinary state of a fresh install — reporting an error there would put a
/// problem in front of an operator who does not have one.
/// </summary>
public sealed class MessageRuleStatsReaderTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "rulestats-tests-" + Guid.NewGuid().ToString("N"));

    public MessageRuleStatsReaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* SQLite may still hold the file — ignored in teardown */ }
    }

    private string DbPath => Path.Combine(_tempDir, "data", "metrics.db");

    private MetricsService CreateService()
    {
        var monitor = Substitute.For<IOptionsMonitor<MetricsOptions>>();
        monitor.CurrentValue.Returns(new MetricsOptions { Enabled = true, RetentionDays = 90, BasePath = _tempDir });
        return new MetricsService(monitor, NullLogger<MetricsService>.Instance);
    }

    [Fact]
    public void Read_MissingDatabase_ReturnsNothing()
    {
        MessageRuleStatsReader.Read(Path.Combine(_tempDir, "nope.db"), DateTime.UtcNow.AddDays(-7))
            .Should().BeEmpty();
    }

    [Fact]
    public void Read_DatabaseWithoutTheTable_ReturnsNothing()
    {
        var path = Path.Combine(_tempDir, "old.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE something_else (x INT)";
            cmd.ExecuteNonQuery();
        }

        MessageRuleStatsReader.Read(path, DateTime.UtcNow.AddDays(-7)).Should().BeEmpty();
    }

    [Fact]
    public async Task Read_GroupsByRuleAndMode()
    {
        var service = CreateService();
        await service.RecordRuleHitAsync("disclaimer", "Enforce", "modified", 25);
        await service.RecordRuleHitAsync("disclaimer", "Enforce", "modified", 25);
        await service.RecordRuleHitAsync("disclaimer", "Audit", "modified", 25);
        await service.RecordRuleHitAsync("block macros", "Enforce", "rejected", 25);

        var rows = MessageRuleStatsReader.Read(DbPath, DateTime.UtcNow.AddDays(-1));

        rows.Should().HaveCount(3);
        rows.Should().ContainSingle(r => r.RuleName == "disclaimer" && r.Mode == "Enforce" && r.Modified == 2);
        rows.Should().ContainSingle(r => r.RuleName == "disclaimer" && r.Mode == "Audit" && r.Modified == 1);
        rows.Should().ContainSingle(r => r.RuleName == "block macros" && r.Rejected == 1);
    }

    [Fact]
    public async Task Read_CountsEveryOutcomeSeparately()
    {
        var service = CreateService();
        await service.RecordRuleHitAsync("r", "Enforce", "modified", 25);
        await service.RecordRuleHitAsync("r", "Enforce", "rejected", 25);
        await service.RecordRuleHitAsync("r", "Enforce", "discarded", 25);
        await service.RecordRuleHitAsync("r", "Enforce", "skipped", 25);

        var row = MessageRuleStatsReader.Read(DbPath, DateTime.UtcNow.AddDays(-1)).Should().ContainSingle().Subject;

        row.Modified.Should().Be(1);
        row.Rejected.Should().Be(1);
        row.Discarded.Should().Be(1);
        row.Skipped.Should().Be(1);
        row.Total.Should().Be(4);
    }

    [Fact]
    public async Task Read_IgnoresHitsOutsideTheWindow()
    {
        var service = CreateService();
        await service.RecordRuleHitAsync("r", "Enforce", "modified", 25);

        MessageRuleStatsReader.Read(DbPath, DateTime.UtcNow.AddDays(1)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadHitTotals_SumsBothModesPerRule()
    {
        var service = CreateService();
        await service.RecordRuleHitAsync("disclaimer", "Enforce", "modified", 25);
        await service.RecordRuleHitAsync("disclaimer", "Audit", "modified", 25);
        await service.RecordRuleHitAsync("other", "Enforce", "rejected", 25);

        var totals = MessageRuleStatsReader.ReadHitTotals(DbPath);

        totals["disclaimer"].Should().Be(2);
        totals["other"].Should().Be(1);
    }

    [Fact]
    public void ReadHitTotals_MissingDatabase_ReturnsAnEmptyMap()
    {
        MessageRuleStatsReader.ReadHitTotals(Path.Combine(_tempDir, "nope.db")).Should().BeEmpty();
    }
}
