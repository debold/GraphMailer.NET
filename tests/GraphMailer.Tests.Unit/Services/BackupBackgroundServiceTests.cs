using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Backup;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GraphMailer.Tests.Unit.Services;

public sealed class BackupBackgroundServiceTests
{
    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(value);
        return m;
    }

    private static (BackupBackgroundService Sut, IConfigBackupService Backup, IAdminNotificationService Notify)
        Create(BackupOptions? opts = null)
    {
        var backup = Substitute.For<IConfigBackupService>();
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = new BackupBackgroundService(
            backup,
            Monitor(opts ?? new BackupOptions { Enabled = true, Password = "pw", MaxBackups = 5 }),
            Substitute.For<IGraphApiClient>(),
            Monitor(new AdminNotificationsOptions()),
            notify,
            NullLogger<BackupBackgroundService>.Instance);
        return (sut, backup, notify);
    }

    [Fact]
    public async Task RunBackup_Success_NotifiesSucceeded()
    {
        var (sut, backup, notify) = Create();
        backup.WriteBackup(Arg.Any<string>(), Arg.Any<string>()).Returns(@"C:\backups\b.gmbak");

        await sut.RunBackupAsync(new BackupOptions { Enabled = true, Password = "pw", MaxBackups = 5 }, CancellationToken.None);

        backup.Received(1).WriteBackup("pw", Arg.Any<string>());
        backup.Received(1).Rotate(Arg.Any<string>(), 5);
        await notify.Received(1).NotifyBackupResultAsync(true, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await notify.DidNotReceive().NotifyBackupResultAsync(false, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);
    private static DateTimeOffset At(int h, int m, int s = 0) => new(2026, 6, 13, h, m, s, Offset);

    private static BackupOptions Daily(string time) =>
        new() { Enabled = true, Password = "pw", Frequency = BackupFrequency.Daily, TimeOfDay = time };

    [Fact]
    public void PlanTick_HoldsTarget_FiresAtScheduledTime_ThenRescheduges()
    {
        var (sut, _, _) = Create();
        var opts = Daily("13:25");

        // Before the time → wait (regression: the old loop recomputed and never fired)
        sut.PlanTick(opts, At(13, 20)).Action.Should().Be(BackupBackgroundService.TickAction.Wait);

        // At/after the time → run
        sut.PlanTick(opts, At(13, 25, 1)).Action.Should().Be(BackupBackgroundService.TickAction.Run);

        // After running, the next tick schedules the following occurrence (tomorrow) → wait, not run again
        sut.PlanTick(opts, At(13, 25, 30)).Action.Should().Be(BackupBackgroundService.TickAction.Wait);
    }

    [Fact]
    public void PlanTick_AfterOptionsChange_AdoptsNewTime_EvenIfLater()
    {
        var (sut, _, _) = Create();

        // Scheduled for 13:25 → waiting
        sut.PlanTick(Daily("13:25"), At(13, 20)).Action.Should().Be(BackupBackgroundService.TickAction.Wait);

        // Options change to a LATER time → the held target is replaced (hot reload)
        sut.MarkOptionsChanged();
        var result = sut.PlanTick(Daily("14:00"), At(13, 30));

        result.Action.Should().Be(BackupBackgroundService.TickAction.Wait);
        result.Wait.Should().Be(TimeSpan.FromMinutes(30));   // 13:30 → 14:00, not the old 13:25
    }

    [Fact]
    public void PlanTick_Disabled_ReturnsIdle()
    {
        var (sut, _, _) = Create();
        sut.PlanTick(new BackupOptions { Enabled = false }, At(13, 25))
            .Action.Should().Be(BackupBackgroundService.TickAction.Idle);
    }

    [Fact]
    public void PlanTick_EnabledButNoPassword_ReturnsIdle()
    {
        var (sut, _, _) = Create();
        sut.PlanTick(new BackupOptions { Enabled = true, Password = null, TimeOfDay = "13:25" }, At(13, 25))
            .Action.Should().Be(BackupBackgroundService.TickAction.Idle);
    }

    [Fact]
    public void PlanTick_InvalidTime_ReturnsIdle()
    {
        var (sut, _, _) = Create();
        sut.PlanTick(new BackupOptions { Enabled = true, Password = "pw", TimeOfDay = "nonsense" }, At(13, 25))
            .Action.Should().Be(BackupBackgroundService.TickAction.Idle);
    }

    [Fact]
    public async Task RunBackup_WriteFails_NotifiesFailed_AndDoesNotRotate()
    {
        var (sut, backup, notify) = Create();
        backup.WriteBackup(Arg.Any<string>(), Arg.Any<string>()).Throws(new IOException("disk full"));

        await sut.RunBackupAsync(new BackupOptions { Enabled = true, Password = "pw", MaxBackups = 5 }, CancellationToken.None);

        backup.DidNotReceive().Rotate(Arg.Any<string>(), Arg.Any<int>());
        await notify.Received(1).NotifyBackupResultAsync(false,
            Arg.Is<string>(s => s != null && s.Contains("disk full")), Arg.Any<CancellationToken>());
    }
}
