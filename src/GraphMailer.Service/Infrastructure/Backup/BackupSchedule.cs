using GraphMailer.Service.Configuration;

namespace GraphMailer.Service.Infrastructure.Backup;

/// <summary>
/// Computes the next scheduled backup time from <see cref="BackupOptions"/>.
/// Pure and time-injectable so it can be unit-tested without waiting.
/// </summary>
internal static class BackupSchedule
{
    /// <summary>
    /// Next run strictly after <paramref name="now"/>, or <see langword="null"/> when
    /// <see cref="BackupOptions.TimeOfDay"/> is not a valid "HH:mm" value.
    /// </summary>
    internal static DateTimeOffset? NextRun(BackupOptions opts, DateTimeOffset now)
    {
        if (!TimeOnly.TryParse(opts.TimeOfDay, out var time))
            return null;
        return NextRun(opts.Frequency, time, opts.DayOfWeek, now);
    }

    internal static DateTimeOffset NextRun(
        BackupFrequency frequency, TimeOnly time, DayOfWeek weekday, DateTimeOffset now)
    {
        var candidate = new DateTimeOffset(
            now.Year, now.Month, now.Day, time.Hour, time.Minute, 0, now.Offset);

        if (frequency == BackupFrequency.Weekly)
        {
            var daysUntil = ((int)weekday - (int)now.DayOfWeek + 7) % 7;
            candidate = candidate.AddDays(daysUntil);
        }

        // If today's slot has already passed, move to the next day (daily) or next week (weekly).
        if (candidate <= now)
            candidate = candidate.AddDays(frequency == BackupFrequency.Weekly ? 7 : 1);

        return candidate;
    }
}
