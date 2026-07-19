using System.Text.Json;
using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Services;
using GraphMailer.Service.Services.Reporting;
using GraphMailer.Service.Services.UpdateCheck;
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

    private ReportDataCollector CreateCollector(bool updateCheckEnabled = false)
        => new(
            Monitor(new MailQueueOptions { MailDir = Path.Combine(_temp, "mail") }),
            Monitor(new MetricsOptions { Enabled = true, BasePath = _temp }),
            Monitor(new CertificateOptions()),
            Monitor(new CertificateMonitoringOptions()),
            Monitor(new DiskSpaceMonitoringOptions()),
            Monitor(new List<SmtpServerEntry>()),
            Monitor(new UpdateCheckOptions { Enabled = updateCheckEnabled }),
            new EphemeralDataProtectionProvider(),
            NullLogger<ReportDataCollector>.Instance)
        {
            // Keep the test hermetic: never read the machine's real %ProgramData% status file.
            UpdateStatusPath = Path.Combine(_temp, "update-status.json"),
        };

    private void WriteUpdateStatus(UpdateCheckStatus status)
        => status.Save(Path.Combine(_temp, "update-status.json"));

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
    public void Collect_UpdateAvailable_ReportsSoftwareUpdateWarning()
    {
        WriteUpdateStatus(new UpdateCheckStatus
        {
            CurrentVersion = "1.2.0.100",
            LatestVersion = "1.3.0.200",
            UpdateAvailable = true,
            LastCheckUtc = DateTime.UtcNow,
        });

        var data = CreateCollector(updateCheckEnabled: true).Collect(new ScheduledReportOptions(), DateTimeOffset.Now);

        var row = data.Health.Should().ContainSingle(h => h.Component == "Software Update").Subject;
        row.Status.Should().Be(HealthStatus.Warning);
        row.Detail.Should().Contain("1.3.0.200").And.Contain("1.2.0.100");
    }

    [Fact]
    public void Collect_UpToDate_ReportsSoftwareUpdateOk()
    {
        WriteUpdateStatus(new UpdateCheckStatus
        {
            CurrentVersion = "1.3.0.200",
            LatestVersion = "1.3.0.200",
            UpdateAvailable = false,
            LastCheckUtc = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc),
        });

        var data = CreateCollector(updateCheckEnabled: true).Collect(new ScheduledReportOptions(), DateTimeOffset.Now);

        var row = data.Health.Should().ContainSingle(h => h.Component == "Software Update").Subject;
        row.Status.Should().Be(HealthStatus.Ok);
        row.Detail.Should().Contain("Up to date").And.Contain("2026-07-18");
    }

    [Fact]
    public void Collect_NoUpdateStatusFile_ReportsSoftwareUpdateUnknown()
    {
        var disabled = CreateCollector(updateCheckEnabled: false).Collect(new ScheduledReportOptions(), DateTimeOffset.Now);
        disabled.Health.Should().ContainSingle(h => h.Component == "Software Update")
            .Which.Should().Match<HealthItem>(h => h.Status == HealthStatus.Unknown && h.Detail == "Update check disabled");

        var enabled = CreateCollector(updateCheckEnabled: true).Collect(new ScheduledReportOptions(), DateTimeOffset.Now);
        enabled.Health.Should().ContainSingle(h => h.Component == "Software Update")
            .Which.Should().Match<HealthItem>(h => h.Status == HealthStatus.Unknown && h.Detail == "No check has run yet");
    }

    [Fact]
    public void Collect_UpdateCheckFailedWithoutResult_ReportsSoftwareUpdateUnknown()
    {
        WriteUpdateStatus(new UpdateCheckStatus
        {
            LastError = "GitHub unreachable",
            LastCheckUtc = DateTime.UtcNow,
        });

        var data = CreateCollector(updateCheckEnabled: true).Collect(new ScheduledReportOptions(), DateTimeOffset.Now);

        var row = data.Health.Should().ContainSingle(h => h.Component == "Software Update").Subject;
        row.Status.Should().Be(HealthStatus.Unknown);
        row.Detail.Should().Contain("GitHub unreachable");
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
