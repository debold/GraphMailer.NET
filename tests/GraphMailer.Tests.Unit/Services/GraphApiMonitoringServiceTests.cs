using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Tests for the connectivity and permission reporting of <see cref="GraphApiMonitoringService"/>.
///
/// The monitor reports the <i>current state</i> on every check and deliberately holds no
/// notification state: how often that turns into an email is owned by
/// <see cref="IAlertStateStore"/> (covered by <see cref="AlertStateStoreTests"/> and
/// <see cref="AdminNotificationServiceTests"/>). Asserting "notified once" here would re-test the
/// wrong layer — the earlier per-monitor deduplication is exactly what made the notification
/// cadence differ between monitors.
///
/// Regression background: an even earlier implementation probed via SendNotificationAsync, which
/// swallows all exceptions — the monitor could never detect an outage and spammed a warning per check.
/// </summary>
public sealed class GraphApiMonitoringServiceTests
{
    private static readonly string[] AllRoles = ["Mail.Send", "Mail.ReadWrite", "User.Read.All"];

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private static IGraphConnectivityProbe ProbeReturning(params string[] roles)
    {
        var probe = Substitute.For<IGraphConnectivityProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult(roles));
        return probe;
    }

    private static GraphApiMonitoringService CreateService(
        IGraphConnectivityProbe probe,
        IAdminNotificationService? notify = null,
        GraphApiOptions? graphOpts = null,
        bool senderValidationEnabled = false)
        => new(
            probe,
            notify ?? Substitute.For<IAdminNotificationService>(),
            Monitor(new GraphApiMonitoringOptions()),
            Monitor(graphOpts ?? new GraphApiOptions
            {
                TenantId = "tenant-id",
                ClientId = "client-id",
                ClientSecret = "s3cr3t",
            }),
            Monitor(new SenderValidationOptions { Enabled = senderValidationEnabled }),
            NullLogger<GraphApiMonitoringService>.Instance);

    // ── Connectivity state machine ───────────────────────────────────────────

    [Fact]
    public async Task Check_ProbeFails_ReportsOutageOnEveryCheck()
    {
        var probe = Substitute.For<IGraphConnectivityProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("token endpoint unreachable"));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(probe, notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);
        await sut.CheckConnectivityAsync(CancellationToken.None);   // still down

        // Reported every time — the alert store, not the monitor, decides what gets mailed.
        await notify.ReceivedWithAnyArgs(2).NotifyGraphApiErrorAsync(default!, default);
        await notify.DidNotReceiveWithAnyArgs().NotifyGraphApiRestoredAsync(default);
    }

    [Fact]
    public async Task Check_ProbeRecovers_NotifiesRestored()
    {
        var probe = Substitute.For<IGraphConnectivityProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down"));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(probe, notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);   // outage

        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult(AllRoles));
        await sut.CheckConnectivityAsync(CancellationToken.None);   // recovery

        await notify.ReceivedWithAnyArgs(1).NotifyGraphApiRestoredAsync(default);
    }

    [Fact]
    public async Task Check_ProbeHealthy_ReportsHealthyStateAndNoFault()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(ProbeReturning(AllRoles), notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);

        await notify.DidNotReceiveWithAnyArgs().NotifyGraphApiErrorAsync(default!, default);
        await notify.DidNotReceiveWithAnyArgs().NotifyGraphPermissionsMissingAsync(default!, default!, default);

        // The healthy state is reported even without a preceding outage: that is what lets an
        // outage which started before a service restart still produce its recovery mail.
        await notify.ReceivedWithAnyArgs(1).NotifyGraphApiRestoredAsync(default);
        await notify.ReceivedWithAnyArgs(1).NotifyGraphPermissionsRestoredAsync(default);
    }

    [Fact]
    public async Task Check_GraphNotConfigured_DoesNotProbe()
    {
        var probe = Substitute.For<IGraphConnectivityProbe>();
        var sut = CreateService(probe, graphOpts: new GraphApiOptions());

        await sut.CheckConnectivityAsync(CancellationToken.None);

        await probe.DidNotReceiveWithAnyArgs().ProbeAsync(default);
    }

    // ── Permission check ─────────────────────────────────────────────────────

    [Fact]
    public async Task Check_MissingMailReadWrite_ReportsGapWithRoleAndDetail()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(ProbeReturning("Mail.Send"), notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);

        await notify.Received(1).NotifyGraphPermissionsMissingAsync(
            Arg.Is<IReadOnlyList<string>>(r => r != null && r.Contains("Mail.ReadWrite")),
            Arg.Is<string>(m => m != null && m.Contains("Mail.ReadWrite")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_SameGapTwice_ReportsBothTimesWithSameRoleKey()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(ProbeReturning("Mail.Send"), notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);
        await sut.CheckConnectivityAsync(CancellationToken.None);

        // Same role list both times: that is what lets the alert store recognise it as the same
        // condition and apply the repeat interval instead of treating it as a new problem.
        await notify.Received(2).NotifyGraphPermissionsMissingAsync(
            Arg.Is<IReadOnlyList<string>>(r => r != null && r.Count == 1 && r[0] == "Mail.ReadWrite"),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_UserReadAll_RequiredOnlyWhenSenderValidationEnabled()
    {
        var grantedWithoutUserRead = new[] { "Mail.Send", "Mail.ReadWrite" };

        var notifyDisabled = Substitute.For<IAdminNotificationService>();
        var disabled = CreateService(ProbeReturning(grantedWithoutUserRead), notifyDisabled);
        await disabled.CheckConnectivityAsync(CancellationToken.None);
        await notifyDisabled.DidNotReceiveWithAnyArgs().NotifyGraphPermissionsMissingAsync(default!, default!, default);

        var notifyEnabled = Substitute.For<IAdminNotificationService>();
        var enabled = CreateService(ProbeReturning(grantedWithoutUserRead), notifyEnabled,
            senderValidationEnabled: true);
        await enabled.CheckConnectivityAsync(CancellationToken.None);
        await notifyEnabled.Received(1).NotifyGraphPermissionsMissingAsync(
            Arg.Is<IReadOnlyList<string>>(r => r != null && r.Contains("User.Read.All")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_PermissionGapFixed_ReportsRestored()
    {
        var probe = Substitute.For<IGraphConnectivityProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult(["Mail.Send"]));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(probe, notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);   // gap
        await notify.DidNotReceiveWithAnyArgs().NotifyGraphPermissionsRestoredAsync(default);

        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult(AllRoles));
        await sut.CheckConnectivityAsync(CancellationToken.None);   // fixed

        await notify.ReceivedWithAnyArgs(1).NotifyGraphPermissionsRestoredAsync(default);
    }

    [Fact]
    public async Task Check_PermissionGapWidens_ReportsChangedRoleSet()
    {
        var probe = Substitute.For<IGraphConnectivityProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult(["Mail.Send"]));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(probe, notify, senderValidationEnabled: true);

        await sut.CheckConnectivityAsync(CancellationToken.None);

        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult([]));   // Mail.Send revoked as well
        await sut.CheckConnectivityAsync(CancellationToken.None);

        // A widened gap is a different role set, so the alert store sees a changed condition and
        // reports it immediately instead of waiting out the repeat interval.
        await notify.Received(1).NotifyGraphPermissionsMissingAsync(
            Arg.Is<IReadOnlyList<string>>(r => r != null && r.Count == 2),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await notify.Received(1).NotifyGraphPermissionsMissingAsync(
            Arg.Is<IReadOnlyList<string>>(r => r != null && r.Count == 3),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
