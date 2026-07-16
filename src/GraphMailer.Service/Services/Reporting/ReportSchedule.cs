using GraphMailer.Service.Configuration;

namespace GraphMailer.Service.Services.Reporting;

/// <summary>
/// Computes the next scheduled report time from <see cref="ScheduledReportOptions"/>.
/// Pure and time-injectable so it can be unit-tested without waiting.
/// </summary>
internal static class ReportSchedule
{
    /// <summary>
    /// Next run strictly after <paramref name="now"/>, or <see langword="null"/> when
    /// <see cref="ScheduledReportOptions.TimeOfDay"/> is not a valid "HH:mm" value.
    /// </summary>
    internal static DateTimeOffset? NextRun(ScheduledReportOptions opts, DateTimeOffset now)
    {
        if (!TimeOnly.TryParse(opts.TimeOfDay, out var time))
            return null;

        return opts.Frequency == ReportFrequency.Monthly
            ? NextMonthly(time, opts.DayOfMonth, now)
            : NextWeekly(time, opts.DayOfWeek, now);
    }

    private static DateTimeOffset NextWeekly(TimeOnly time, DayOfWeek weekday, DateTimeOffset now)
    {
        var candidate = new DateTimeOffset(
            now.Year, now.Month, now.Day, time.Hour, time.Minute, 0, now.Offset);

        var daysUntil = ((int)weekday - (int)now.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(daysUntil);

        if (candidate <= now)
            candidate = candidate.AddDays(7);

        return candidate;
    }

    private static DateTimeOffset NextMonthly(TimeOnly time, int dayOfMonth, DateTimeOffset now)
    {
        // Clamp to 1..28 so the chosen day exists in every month (no Feb-29 surprises).
        var day = Math.Clamp(dayOfMonth, 1, 28);

        var candidate = new DateTimeOffset(
            now.Year, now.Month, day, time.Hour, time.Minute, 0, now.Offset);

        if (candidate <= now)
            candidate = candidate.AddMonths(1);

        return candidate;
    }
}
