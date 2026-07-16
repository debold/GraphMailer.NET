using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using GraphMailer.Service.Services.Reporting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services.Reporting;

public sealed class ScheduledReportServiceTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);
    private static DateTimeOffset At(int h, int m, int s = 0) => new(2026, 6, 15, h, m, s, Offset); // Monday

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(value);
        return m;
    }

    private static ReportDataCollector StubCollector()
        => new(
            Monitor(new MailQueueOptions { MailDir = "" }),
            Monitor(new MetricsOptions { BasePath = Path.Combine(Path.GetTempPath(), "gm-report-" + Guid.NewGuid().ToString("N")) }),
            Monitor(new CertificateOptions()),
            Monitor(new CertificateMonitoringOptions()),
            Monitor(new DiskSpaceMonitoringOptions()),
            Monitor(new List<SmtpServerEntry>()),
            new EphemeralDataProtectionProvider(),
            NullLogger<ReportDataCollector>.Instance);

    private static AdminNotificationsOptions Build(
        bool enabled = true, string? sender = "relay@corp.com", bool withRecipients = true, string time = "07:00")
        => new()
        {
            SenderAddress = sender,
            RecipientAddresses = withRecipients ? ["ops@corp.com"] : [],
            ScheduledReport = new ScheduledReportOptions
            {
                Enabled = enabled,
                Frequency = ReportFrequency.Weekly,
                DayOfWeek = DayOfWeek.Monday,
                TimeOfDay = time,
            },
        };

    private static (ScheduledReportService Sut, IGraphApiClient Graph) Create(AdminNotificationsOptions opts)
    {
        var graph = Substitute.For<IGraphApiClient>();
        graph.SendHtmlNotificationAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<GraphInlineImage?>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(true));
        var sut = new ScheduledReportService(Monitor(opts), StubCollector(), graph, NullLogger<ScheduledReportService>.Instance);
        return (sut, graph);
    }

    [Fact]
    public void PlanTick_ReportDisabled_ReturnsIdle()
    {
        var opts = Build(enabled: false);
        var (sut, _) = Create(opts);

        sut.PlanTick(opts, At(7, 0, 1)).Action.Should().Be(ScheduledReportService.TickAction.Idle);
    }

    [Fact]
    public void PlanTick_NoSenderAddress_ReturnsIdle()
    {
        var opts = Build(sender: null);
        var (sut, _) = Create(opts);

        sut.PlanTick(opts, At(7, 0, 1)).Action.Should().Be(ScheduledReportService.TickAction.Idle);
    }

    [Fact]
    public void PlanTick_NoRecipients_ReturnsIdle()
    {
        var opts = Build(withRecipients: false);
        var (sut, _) = Create(opts);

        sut.PlanTick(opts, At(7, 0, 1)).Action.Should().Be(ScheduledReportService.TickAction.Idle);
    }

    [Fact]
    public void PlanTick_BeforeSlot_Waits_AtSlot_Runs_ThenReschedules()
    {
        var opts = Build(time: "07:30");
        var (sut, _) = Create(opts);

        sut.PlanTick(opts, At(7, 0)).Action.Should().Be(ScheduledReportService.TickAction.Wait);
        sut.PlanTick(opts, At(7, 30, 1)).Action.Should().Be(ScheduledReportService.TickAction.Run);
        sut.PlanTick(opts, At(7, 30, 30)).Action.Should().Be(ScheduledReportService.TickAction.Wait);
    }

    [Fact]
    public async Task RunReportAsync_SendsHtmlToRecipients()
    {
        var opts = Build();
        var (sut, graph) = Create(opts);

        await sut.RunReportAsync(opts, CancellationToken.None);

        await graph.Received(1).SendHtmlNotificationAsync(
            "relay@corp.com",
            Arg.Is<IEnumerable<string>>(r => r.Contains("ops@corp.com")),
            Arg.Is<string>(s => s.Contains("Operations Report")),
            Arg.Is<string>(html => html.Contains("<!DOCTYPE html>")),
            Arg.Any<GraphInlineImage?>(),
            Arg.Any<CancellationToken>());
    }
}
