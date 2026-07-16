using System.Text.Json;
using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Services;
using GraphMailer.Service.Services.Reporting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services.Reporting;

public sealed class ReportDataCollectorTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "gm-collector-" + Guid.NewGuid().ToString("N"));

    public ReportDataCollectorTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(_temp, recursive: true); } catch { /* SQLite handle */ }
    }

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(value);
        return m;
    }

    private MetricsService SeedMetrics()
    {
        var svc = new MetricsService(
            Monitor(new MetricsOptions { Enabled = true, BasePath = _temp }),
            NullLogger<MetricsService>.Instance);
        return svc;
    }

    private ReportDataCollector CreateCollector()
        => new(
            Monitor(new MailQueueOptions { MailDir = Path.Combine(_temp, "mail") }),
            Monitor(new MetricsOptions { Enabled = true, BasePath = _temp }),
            Monitor(new CertificateOptions()),
            Monitor(new CertificateMonitoringOptions()),
            Monitor(new DiskSpaceMonitoringOptions()),
            Monitor(new List<SmtpServerEntry>()),
            new EphemeralDataProtectionProvider(),
            NullLogger<ReportDataCollector>.Instance);

    private void WriteFailedMeta(MailMetadata meta)
    {
        var dir = Path.Combine(_temp, "mail", "failed");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{meta.MessageId}.meta.json"), JsonSerializer.Serialize(meta));
    }

    [Fact]
    public async Task Collect_AggregatesDeliveredFailedAndTopHosts()
    {
        var metrics = SeedMetrics();
        await metrics.RecordEmailReceivedAsync("a@corp.com", ["x@ext.com"], "m1", clientIp: "10.0.0.5");
        await metrics.RecordEmailReceivedAsync("b@corp.com", ["y@ext.com"], "m2", clientIp: "10.0.0.5");
        await metrics.RecordEmailSentAsync("a@corp.com", ["x@ext.com"], "m1");
        await metrics.RecordEmailFailedAsync("m3", "550 rejected", "c@corp.com");

        var data = CreateCollector().Collect(new ScheduledReportOptions { Frequency = ReportFrequency.Weekly }, DateTimeOffset.Now);

        data.Delivered.Should().Be(1);
        data.Failed.Should().Be(1);
        data.TopHosts.Should().ContainSingle(h => h.Name == "10.0.0.5" && h.Count == 2);
        data.Title.Should().Be("Weekly Operations Report");
    }

    [Fact]
    public void Collect_ReadsFailedQueueFolder()
    {
        WriteFailedMeta(new MailMetadata
        {
            MessageId = "fail-1",
            From = "app@corp.com",
            To = ["dest@ext.com"],
            Subject = "Nightly export",
            LastError = "MailboxNotEnabledForRESTAPI",
            RetryCount = 10,
            Status = "failed",
            LastAttemptAt = DateTime.UtcNow,
        });

        var data = CreateCollector().Collect(new ScheduledReportOptions(), DateTimeOffset.Now);

        data.FailedQueueCount.Should().Be(1);
        data.FailedQueueItems.Should().ContainSingle();
        data.FailedQueueItems[0].Subject.Should().Be("Nightly export");
        data.FailedQueueItems[0].LastError.Should().Be("MailboxNotEnabledForRESTAPI");
    }

    [Fact]
    public async Task Collect_SentWithDuration_ReportsAvgAndPeakDelivery()
    {
        // Regression: MAX(duration_ms) on the INT column comes back as long (SQLite keeps
        // column affinity), while AVG always yields double — the collector once dropped
        // the long and rendered "no data" next to a populated average.
        var metrics = SeedMetrics();
        await metrics.RecordEmailSentAsync("a@corp.com", ["x@ext.com"], "m1", durationMs: 200);
        await metrics.RecordEmailSentAsync("a@corp.com", ["y@ext.com"], "m2", durationMs: 400);

        var data = CreateCollector().Collect(new ScheduledReportOptions(), DateTimeOffset.Now);

        data.AvgDeliveryMs.Should().Be(300);
        data.PeakDeliveryMs.Should().Be(400);
    }

    [Fact]
    public void Collect_NoMetricsDb_ReturnsZeroedStatsWithoutThrowing()
    {
        var data = CreateCollector().Collect(new ScheduledReportOptions { Frequency = ReportFrequency.Monthly }, DateTimeOffset.Now);

        data.Delivered.Should().Be(0);
        data.Failed.Should().Be(0);
        data.Title.Should().Be("Monthly Operations Report");
        data.Health.Should().NotBeEmpty();
    }
}
