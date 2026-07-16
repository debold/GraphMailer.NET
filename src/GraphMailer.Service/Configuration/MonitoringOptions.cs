namespace GraphMailer.Service.Configuration;

public sealed class CertificateMonitoringOptions
{
    public const string SectionName = "CertificateMonitoring";

    public bool Enabled { get; init; } = true;
    public int WarningThresholdDays { get; init; } = 14;
    public int CheckIntervalHours { get; init; } = 24;
}

public sealed class DiskSpaceMonitoringOptions
{
    public const string SectionName = "DiskSpaceMonitoring";

    public bool Enabled { get; init; } = true;
    public int CheckIntervalMinutes { get; init; } = 60;
    public int ThresholdPercent { get; init; } = 10;
}

public sealed class PortMonitoringOptions
{
    public const string SectionName = "PortMonitoring";

    public bool Enabled { get; init; } = true;
    public int CheckIntervalMinutes { get; init; } = 5;
    public int OutageAlertThresholdMinutes { get; init; } = 10;
    public int AlertCooldownMinutes { get; init; } = 60;
}

public sealed class GraphApiMonitoringOptions
{
    public const string SectionName = "GraphApiMonitoring";

    public bool Enabled { get; init; } = true;
    public int CheckIntervalMinutes { get; init; } = 15;
}
