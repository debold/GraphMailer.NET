using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Smtp;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// The monitor reports the current port state and holds no notification state — how often that
/// becomes an email is owned by <see cref="IAlertStateStore"/>. What stays here is the outage
/// threshold: how long a port must be down before it counts as a problem at all.
/// </summary>
public sealed class PortMonitoringServiceTests
{
    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(value);
        return m;
    }

    private static PortMonitoringService CreateService(IAdminNotificationService notify, PortMonitoringOptions opts)
        => new(
            notify,
            Monitor(opts),
            Monitor(new List<SmtpServerEntry>()),
            new PortProbeRegistry(),
            NullLogger<PortMonitoringService>.Instance);

    /// <summary>Binds a loopback listener and returns its port; the caller disposes to close it.</summary>
    private static TcpListener ListenOnFreePort(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    /// <summary>A port nothing is listening on: bind one, note it, release it again.</summary>
    private static int FindClosedPort()
    {
        using var listener = ListenOnFreePort(out var port);
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Check_PortReachable_ReportsRestoredState()
    {
        using var listener = ListenOnFreePort(out var port);
        var notify = Substitute.For<IAdminNotificationService>();
        var opts = new PortMonitoringOptions { Enabled = true, OutageAlertThresholdMinutes = 10 };
        var sut = CreateService(notify, opts);

        await sut.CheckPortAsync(port, opts, CancellationToken.None);

        // Reported even without a preceding outage — that is what resolves an alert raised before
        // a service restart.
        await notify.ReceivedWithAnyArgs(1).NotifyPortRestoredAsync(default, default);
        await notify.DidNotReceiveWithAnyArgs().NotifyPortOutageAsync(default, default!, default);
    }

    [Fact]
    public async Task Check_PortDownBelowOutageThreshold_ReportsNothing()
    {
        var port = FindClosedPort();
        var notify = Substitute.For<IAdminNotificationService>();
        var opts = new PortMonitoringOptions { Enabled = true, OutageAlertThresholdMinutes = 10 };
        var sut = CreateService(notify, opts);

        await sut.CheckPortAsync(port, opts, CancellationToken.None);

        // Neither raise nor clear: a brief blip must not alert, but it must also not resolve an
        // alert that was never sent.
        await notify.DidNotReceiveWithAnyArgs().NotifyPortOutageAsync(default, default!, default);
        await notify.DidNotReceiveWithAnyArgs().NotifyPortRestoredAsync(default, default);
    }

    [Fact]
    public async Task Check_PortDownPastOutageThreshold_ReportsOutageOnEveryCheck()
    {
        var port = FindClosedPort();
        var notify = Substitute.For<IAdminNotificationService>();
        // 0 minutes: the very first failed probe is already past the threshold.
        var opts = new PortMonitoringOptions { Enabled = true, OutageAlertThresholdMinutes = 0 };
        var sut = CreateService(notify, opts);

        await sut.CheckPortAsync(port, opts, CancellationToken.None);
        await sut.CheckPortAsync(port, opts, CancellationToken.None);

        await notify.ReceivedWithAnyArgs(2).NotifyPortOutageAsync(default, default!, default);
    }

    [Fact]
    public async Task Check_PortRecovers_ReportsOutageThenRestored()
    {
        var port = FindClosedPort();
        var notify = Substitute.For<IAdminNotificationService>();
        var opts = new PortMonitoringOptions { Enabled = true, OutageAlertThresholdMinutes = 0 };
        var sut = CreateService(notify, opts);

        await sut.CheckPortAsync(port, opts, CancellationToken.None);

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        await sut.CheckPortAsync(port, opts, CancellationToken.None);

        await notify.ReceivedWithAnyArgs(1).NotifyPortOutageAsync(default, default!, default);
        await notify.ReceivedWithAnyArgs(1).NotifyPortRestoredAsync(default, default);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(99999, 1440)]
    public void Interval_OutOfRangeValues_AreClampedToAUsablePeriod(int configured, int expectedMinutes)
        => PortMonitoringService.Interval(configured).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
}
