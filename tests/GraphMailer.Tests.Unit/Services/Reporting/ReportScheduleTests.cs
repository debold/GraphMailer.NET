using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services.Reporting;

namespace GraphMailer.Tests.Unit.Services.Reporting;

public sealed class ReportScheduleTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);
    private static DateTimeOffset At(int year, int month, int day, int h, int m)
        => new(year, month, day, h, m, 0, Offset);

    [Fact]
    public void NextRun_InvalidTimeOfDay_ReturnsNull()
    {
        var opts = new ScheduledReportOptions { TimeOfDay = "not-a-time" };

        ReportSchedule.NextRun(opts, At(2026, 6, 13, 8, 0)).Should().BeNull();
    }

    [Fact]
    public void NextRun_Weekly_BeforeSlotToday_ReturnsTodayWhenWeekdayMatches()
    {
        // 2026-06-15 is a Monday
        var opts = new ScheduledReportOptions { Frequency = ReportFrequency.Weekly, DayOfWeek = DayOfWeek.Monday, TimeOfDay = "07:00" };

        var next = ReportSchedule.NextRun(opts, At(2026, 6, 15, 6, 0));

        next.Should().Be(At(2026, 6, 15, 7, 0));
    }

    [Fact]
    public void NextRun_Weekly_AfterSlot_RollsToNextWeek()
    {
        var opts = new ScheduledReportOptions { Frequency = ReportFrequency.Weekly, DayOfWeek = DayOfWeek.Monday, TimeOfDay = "07:00" };

        var next = ReportSchedule.NextRun(opts, At(2026, 6, 15, 7, 30));

        next.Should().Be(At(2026, 6, 22, 7, 0)); // next Monday
    }

    [Fact]
    public void NextRun_Weekly_DifferentWeekday_AdvancesToThatDay()
    {
        // From Monday 2026-06-15, next Friday is 2026-06-19
        var opts = new ScheduledReportOptions { Frequency = ReportFrequency.Weekly, DayOfWeek = DayOfWeek.Friday, TimeOfDay = "07:00" };

        var next = ReportSchedule.NextRun(opts, At(2026, 6, 15, 8, 0));

        next.Should().Be(At(2026, 6, 19, 7, 0));
    }

    [Fact]
    public void NextRun_Monthly_BeforeDayThisMonth_ReturnsThisMonth()
    {
        var opts = new ScheduledReportOptions { Frequency = ReportFrequency.Monthly, DayOfMonth = 15, TimeOfDay = "07:00" };

        var next = ReportSchedule.NextRun(opts, At(2026, 6, 10, 9, 0));

        next.Should().Be(At(2026, 6, 15, 7, 0));
    }

    [Fact]
    public void NextRun_Monthly_AfterDay_RollsToNextMonth()
    {
        var opts = new ScheduledReportOptions { Frequency = ReportFrequency.Monthly, DayOfMonth = 15, TimeOfDay = "07:00" };

        var next = ReportSchedule.NextRun(opts, At(2026, 6, 20, 9, 0));

        next.Should().Be(At(2026, 7, 15, 7, 0));
    }

    [Fact]
    public void NextRun_Monthly_DayOfMonthClampedTo28()
    {
        var opts = new ScheduledReportOptions { Frequency = ReportFrequency.Monthly, DayOfMonth = 31, TimeOfDay = "07:00" };

        var next = ReportSchedule.NextRun(opts, At(2026, 2, 1, 9, 0));

        next.Should().Be(At(2026, 2, 28, 7, 0)); // clamped so it exists in February
    }
}
