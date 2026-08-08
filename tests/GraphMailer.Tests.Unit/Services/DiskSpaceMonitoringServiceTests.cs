using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// The monitor reports the current disk state on every check and holds no notification state —
/// how often that becomes an email is owned by <see cref="IAlertStateStore"/>.
/// </summary>
public sealed class DiskSpaceMonitoringServiceTests
{
    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(value);
        return m;
    }

    private static DiskSpaceMonitoringService CreateService(
        IAdminNotificationService notify, DiskSpaceMonitoringOptions opts)
        => new(
            notify,
            Monitor(opts),
            Monitor(new MailQueueOptions { MailDir = Path.GetTempPath() }),
            NullLogger<DiskSpaceMonitoringService>.Instance);

    [Fact]
    public async Task Check_AboveThreshold_ReportsRecoveredState()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        // 0 % means "any free space is enough" — the drive is healthy whatever the machine's state.
        var opts = new DiskSpaceMonitoringOptions { Enabled = true, ThresholdPercent = 0 };
        var sut = CreateService(notify, opts);

        await sut.CheckDiskSpaceAsync(opts, CancellationToken.None);

        await notify.DidNotReceiveWithAnyArgs().NotifyLowDiskSpaceAsync(default!, default, default);
        await notify.ReceivedWithAnyArgs(1).NotifyDiskSpaceRecoveredAsync(default!, default, default);
    }

    [Fact]
    public async Task Check_BelowThreshold_ReportsLowDiskSpace()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        // 100 % can never be satisfied — the drive always counts as low.
        var opts = new DiskSpaceMonitoringOptions { Enabled = true, ThresholdPercent = 100 };
        var sut = CreateService(notify, opts);

        await sut.CheckDiskSpaceAsync(opts, CancellationToken.None);

        await notify.ReceivedWithAnyArgs(1).NotifyLowDiskSpaceAsync(default!, default, default);
        await notify.DidNotReceiveWithAnyArgs().NotifyDiskSpaceRecoveredAsync(default!, default, default);
    }

    [Fact]
    public async Task Check_StillBelowThreshold_ReportsOnEveryCheck()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var opts = new DiskSpaceMonitoringOptions { Enabled = true, ThresholdPercent = 100 };
        var sut = CreateService(notify, opts);

        await sut.CheckDiskSpaceAsync(opts, CancellationToken.None);
        await sut.CheckDiskSpaceAsync(opts, CancellationToken.None);

        // Reported every time — deduplicating here is what used to make this monitor mail hourly
        // while others mailed once.
        await notify.ReceivedWithAnyArgs(2).NotifyLowDiskSpaceAsync(default!, default, default);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(60, 60)]
    [InlineData(99999, 1440)]
    public void Interval_OutOfRangeValues_AreClampedToAUsablePeriod(int configured, int expectedMinutes)
    {
        // A hand-edited 0 would otherwise throw when assigned to PeriodicTimer.Period and take
        // the monitor down for good.
        DiskSpaceMonitoringService.Interval(configured).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }
}
