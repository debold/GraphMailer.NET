using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Services.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services.Telemetry;

public sealed class TelemetryServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "gm-telemetry-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _statusPath;

    private readonly ITelemetrySender _sender = Substitute.For<ITelemetrySender>();
    private readonly IMetricsService _metrics = Substitute.For<IMetricsService>();
    private readonly ErrorReportCollector _collector = new();

    public TelemetryServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _statusPath = Path.Combine(_dir, "telemetry-status.json");
        _sender.FlushAsync(Arg.Any<CancellationToken>()).Returns(true);
        _metrics.GetEventCountsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EmailEventCounts(Received: 12, Sent: 10, Failed: 2));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private TelemetryService CreateService()
    {
        var options = Substitute.For<IOptionsMonitor<TelemetryOptions>>();
        options.CurrentValue.Returns(new TelemetryOptions { Enabled = true });

        var servers = Substitute.For<IOptionsMonitor<List<SmtpServerEntry>>>();
        servers.CurrentValue.Returns(
        [
            new SmtpServerEntry { Enabled = true, Port = 25, Mode = "Plain" },
            new SmtpServerEntry { Enabled = true, Port = 587, Mode = "StartTls", AuthRequired = true },
            new SmtpServerEntry { Enabled = false, Port = 465, Mode = "Ssl" },
        ]);

        var mailQueue = Substitute.For<IOptionsMonitor<MailQueueOptions>>();
        mailQueue.CurrentValue.Returns(new MailQueueOptions());

        return new TelemetryService(
            _sender, _collector, _metrics, options, servers, mailQueue,
            NullLogger<TelemetryService>.Instance)
        {
            StatusPath = _statusPath,
        };
    }

    // ── Due-time logic (daily cadence persisted across restarts) ──────────

    [Fact]
    public void IsHeartbeatDue_NoStatusFile_IsDue()
        => CreateService().IsHeartbeatDue(DateTime.UtcNow).Should().BeTrue(
            "the first heartbeat runs right after enabling");

    [Fact]
    public void IsHeartbeatDue_NextInFuture_IsNotDue()
    {
        new TelemetryStatus { NextHeartbeatUtc = DateTime.UtcNow.AddHours(12) }.Save(_statusPath);

        CreateService().IsHeartbeatDue(DateTime.UtcNow).Should().BeFalse(
            "a service restart within the daily window must not re-send");
    }

    [Fact]
    public void IsHeartbeatDue_NextPassed_IsDue()
    {
        new TelemetryStatus { NextHeartbeatUtc = DateTime.UtcNow.AddMinutes(-1) }.Save(_statusPath);

        CreateService().IsHeartbeatDue(DateTime.UtcNow).Should().BeTrue();
    }

    // ── Successful heartbeat ──────────────────────────────────────────────

    [Fact]
    public async Task RunHeartbeat_Success_WritesStatus_WithDailyNextAndAdvancedWatermark()
    {
        await CreateService().RunHeartbeatAsync(CancellationToken.None);

        var status = TelemetryStatus.TryLoad(_statusPath)!;
        status.Should().NotBeNull();
        Guid.TryParse(status.InstallId, out _).Should().BeTrue("the install id is a random GUID");
        status.LastError.Should().BeNull();
        status.LastHeartbeatUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        status.NextHeartbeatUtc.Should().BeCloseTo(DateTime.UtcNow + TelemetryService.HeartbeatInterval, TimeSpan.FromMinutes(1));
        status.CountersSinceUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1),
            "counters must not be double-reported on the next heartbeat");
    }

    [Fact]
    public async Task RunHeartbeat_InstallId_IsStableAcrossHeartbeats()
    {
        var svc = CreateService();
        await svc.RunHeartbeatAsync(CancellationToken.None);
        var first = TelemetryStatus.TryLoad(_statusPath)!.InstallId;

        await svc.RunHeartbeatAsync(CancellationToken.None);

        TelemetryStatus.TryLoad(_statusPath)!.InstallId.Should().Be(first,
            "distinct-counting the install base requires a stable id");
    }

    [Fact]
    public async Task RunHeartbeat_SendsCountersAndConfigShape()
    {
        IReadOnlyDictionary<string, string>? props = null;
        IReadOnlyDictionary<string, double>? metrics = null;
        _sender.When(s => s.TrackHeartbeat(
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<IReadOnlyDictionary<string, double>>()))
            .Do(c => { props = c.Arg<IReadOnlyDictionary<string, string>>(); metrics = c.Arg<IReadOnlyDictionary<string, double>>(); });

        await CreateService().RunHeartbeatAsync(CancellationToken.None);

        props.Should().NotBeNull();
        props!["listenerCount"].Should().Be("2", "disabled listeners do not count");
        props["tlsEnabled"].Should().Be("True");
        props["authRequired"].Should().Be("True");
        props["archivingEnabled"].Should().Be("False");
        props.Should().ContainKeys("installId", "appVersion", "osVersion", "runtime");
        props.Values.Should().NotContain(v => v.Contains(Environment.MachineName),
            "the hostname must never be part of the heartbeat");

        metrics.Should().NotBeNull();
        metrics!["received"].Should().Be(12);
        metrics["sent"].Should().Be(10);
        metrics["failed"].Should().Be(2);
    }

    [Fact]
    public async Task RunHeartbeat_SendsPendingErrorReports_AndDrainsCollector()
    {
        _collector.Record("[Test] Broken {Id}", "Test", "System.Exception", null, null, null);

        await CreateService().RunHeartbeatAsync(CancellationToken.None);

        _sender.Received(1).TrackErrorReport(Arg.Is<IReadOnlyDictionary<string, string>>(p =>
            p["messageTemplate"] == "[Test] Broken {Id}" && p.ContainsKey("installId")));
        _collector.Drain().Reports.Should().BeEmpty("sent reports must not be re-sent tomorrow");
    }

    // ── Failed transmission ───────────────────────────────────────────────

    [Fact]
    public async Task RunHeartbeat_FlushFails_SetsError_RetriesHourly_KeepsWatermarkAndReports()
    {
        _sender.FlushAsync(Arg.Any<CancellationToken>()).Returns(false);
        _collector.Record("[Test] Broken", "Test", null, null, null, null);
        var watermark = DateTime.UtcNow.AddHours(-20);
        new TelemetryStatus { InstallId = Guid.NewGuid().ToString(), CountersSinceUtc = watermark }.Save(_statusPath);

        await CreateService().RunHeartbeatAsync(CancellationToken.None);

        var status = TelemetryStatus.TryLoad(_statusPath)!;
        status.LastError.Should().NotBeNull();
        status.NextHeartbeatUtc.Should().BeCloseTo(DateTime.UtcNow + TelemetryService.RetryInterval, TimeSpan.FromMinutes(1),
            "a failed send retries hourly instead of daily");
        status.CountersSinceUtc.Should().BeCloseTo(watermark, TimeSpan.FromSeconds(1),
            "unsent counters must be included in the retry");
        _collector.Drain().Reports.Should().ContainSingle("unsent error reports must survive for the retry");
    }

    [Fact]
    public async Task RunHeartbeat_SenderThrows_IsHandled_AndRetriesHourly()
    {
        _sender.When(s => s.TrackHeartbeat(Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<IReadOnlyDictionary<string, double>>()))
            .Do(_ => throw new InvalidOperationException("channel broken"));

        await CreateService().RunHeartbeatAsync(CancellationToken.None);

        var status = TelemetryStatus.TryLoad(_statusPath)!;
        status.LastError.Should().Contain("channel broken");
        status.NextHeartbeatUtc.Should().BeCloseTo(DateTime.UtcNow + TelemetryService.RetryInterval, TimeSpan.FromMinutes(1));
    }
}
