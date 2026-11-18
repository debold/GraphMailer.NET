namespace GraphMailer.Service.Configuration;

/// <summary>
/// How long the service keeps its rolling log files (<c>logs\graphmailer-*.log</c> and
/// <c>logs\error-*.log</c>), in days. The file sinks are built once at startup, so a change
/// takes effect after a service restart.
/// </summary>
public sealed class LogFilesOptions
{
    public const string SectionName = "LogFiles";

    internal const int MinDays = 1;
    internal const int MaxDays = 3650;
    internal const int DefaultRetentionDays = 7;
    internal const int DefaultErrorRetentionDays = 30;

    /// <summary>
    /// Upper bound on the number of files per log, per retained day. A day normally fits in one
    /// file; a day that exceeds the 100 MB size limit rolls to <c>_001</c>, <c>_002</c> …. The cap
    /// keeps a log flood from filling the disk while the day-based retention is in force.
    /// </summary>
    internal const int MaxFilesPerDay = 4;

    /// <summary>Days to keep <c>graphmailer-*.log</c>. Default 7, range 1 – 3650.</summary>
    public int RetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>Days to keep <c>error-*.log</c>. Default 30, range 1 – 3650.</summary>
    public int ErrorRetentionDays { get; init; } = DefaultErrorRetentionDays;

    /// <summary>
    /// Clamps a configured value into the supported range. Out-of-range values never stop the
    /// service from starting: the logs are what the operator needs to see what went wrong.
    /// </summary>
    internal static int Clamp(int days) => Math.Clamp(days, MinDays, MaxDays);

    /// <summary>Time limit for Serilog's file sink for the given (clamped) number of days.</summary>
    internal static TimeSpan TimeLimit(int days) => TimeSpan.FromDays(Clamp(days));

    /// <summary>File-count safety cap for Serilog's file sink for the given (clamped) number of days.</summary>
    internal static int FileCountLimit(int days) => Clamp(days) * MaxFilesPerDay;
}
