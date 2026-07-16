using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Backup;

namespace GraphMailer.Tests.Unit.Infrastructure.Backup;

public sealed class BackupScheduleTests
{
    // 2026-06-15 is a Monday.
    private static readonly DateTimeOffset MondayMorning =
        new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_TimeLaterToday_ReturnsToday()
    {
        var next = BackupSchedule.NextRun(BackupFrequency.Daily, new TimeOnly(9, 0), default, MondayMorning);

        next.Should().Be(new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Daily_TimeAlreadyPassed_ReturnsTomorrow()
    {
        var next = BackupSchedule.NextRun(BackupFrequency.Daily, new TimeOnly(7, 0), default, MondayMorning);

        next.Should().Be(new DateTimeOffset(2026, 6, 16, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Weekly_SameDayLaterTime_ReturnsToday()
    {
        var next = BackupSchedule.NextRun(BackupFrequency.Weekly, new TimeOnly(9, 0), DayOfWeek.Monday, MondayMorning);

        next.Should().Be(new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Weekly_SameDayPassedTime_ReturnsNextWeek()
    {
        var next = BackupSchedule.NextRun(BackupFrequency.Weekly, new TimeOnly(7, 0), DayOfWeek.Monday, MondayMorning);

        next.Should().Be(new DateTimeOffset(2026, 6, 22, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Weekly_LaterInWeek_ReturnsThatDay()
    {
        var next = BackupSchedule.NextRun(BackupFrequency.Weekly, new TimeOnly(7, 0), DayOfWeek.Thursday, MondayMorning);

        next.Should().Be(new DateTimeOffset(2026, 6, 18, 7, 0, 0, TimeSpan.Zero)); // Thu of the same week
    }

    [Fact]
    public void NextRun_InvalidTimeOfDay_ReturnsNull()
    {
        var opts = new BackupOptions { TimeOfDay = "not-a-time" };

        BackupSchedule.NextRun(opts, MondayMorning).Should().BeNull();
    }

    [Fact]
    public void NextRun_FromOptions_ParsesTimeAndFrequency()
    {
        var opts = new BackupOptions { Frequency = BackupFrequency.Daily, TimeOfDay = "03:30" };

        BackupSchedule.NextRun(opts, MondayMorning)
            .Should().Be(new DateTimeOffset(2026, 6, 16, 3, 30, 0, TimeSpan.Zero)); // 03:30 already passed → tomorrow
    }
}
