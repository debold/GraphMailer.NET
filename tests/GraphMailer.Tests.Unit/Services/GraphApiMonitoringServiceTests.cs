using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Tests for the down/restored state machine and the permission check of
/// <see cref="GraphApiMonitoringService"/>.
/// Regression background: the previous implementation probed via
/// SendNotificationAsync, which swallows all exceptions — the monitor could
/// never detect an outage and spammed a warning per check.
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
    public async Task Check_ProbeFails_NotifiesOutageOnce()
    {
        var probe = Substitute.For<IGraphConnectivityProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("token endpoint unreachable"));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(probe, notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);
        await sut.CheckConnectivityAsync(CancellationToken.None);   // still down

        await notify.ReceivedWithAnyArgs(1).NotifyGraphApiErrorAsync(default!, default);
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
    public async Task Check_ProbeHealthy_NoNotifications()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(ProbeReturning(AllRoles), notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);

        await notify.DidNotReceiveWithAnyArgs().NotifyGraphApiErrorAsync(default!, default);
        await notify.DidNotReceiveWithAnyArgs().NotifyGraphApiRestoredAsync(default);
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
    public async Task Check_MissingMailReadWrite_AlertsOncePerGap()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(ProbeReturning("Mail.Send"), notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);
        await sut.CheckConnectivityAsync(CancellationToken.None);   // same gap → no repeat

        await notify.Received(1).NotifyGraphApiErrorAsync(
            Arg.Is<string>(m => m.Contains("Mail.ReadWrite")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_UserReadAll_RequiredOnlyWhenSenderValidationEnabled()
    {
        var grantedWithoutUserRead = new[] { "Mail.Send", "Mail.ReadWrite" };

        var notifyDisabled = Substitute.For<IAdminNotificationService>();
        var disabled = CreateService(ProbeReturning(grantedWithoutUserRead), notifyDisabled);
        await disabled.CheckConnectivityAsync(CancellationToken.None);
        await notifyDisabled.DidNotReceiveWithAnyArgs().NotifyGraphApiErrorAsync(default!, default);

        var notifyEnabled = Substitute.For<IAdminNotificationService>();
        var enabled = CreateService(ProbeReturning(grantedWithoutUserRead), notifyEnabled,
            senderValidationEnabled: true);
        await enabled.CheckConnectivityAsync(CancellationToken.None);
        await notifyEnabled.Received(1).NotifyGraphApiErrorAsync(
            Arg.Is<string>(m => m.Contains("User.Read.All")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_PermissionGapFixedAndReopened_AlertsAgain()
    {
        var probe = Substitute.For<IGraphConnectivityProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult(["Mail.Send"]));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateService(probe, notify);

        await sut.CheckConnectivityAsync(CancellationToken.None);   // gap → alert 1

        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult(AllRoles));
        await sut.CheckConnectivityAsync(CancellationToken.None);   // fixed → reset

        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(new GraphProbeResult(["Mail.Send"]));
        await sut.CheckConnectivityAsync(CancellationToken.None);   // reopened → alert 2

        await notify.ReceivedWithAnyArgs(2).NotifyGraphApiErrorAsync(default!, default);
    }
}
