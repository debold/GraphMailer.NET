namespace GraphMailer.Service.Configuration;

/// <summary>
/// Opt-in anonymous usage telemetry: one daily heartbeat (random install id, version,
/// OS/runtime, aggregated mail counters, configuration shape) plus PII-free error
/// reports (exception type, stack trace, log message <em>template</em> — never rendered
/// messages, addresses, IPs or hostnames) sent to the developer's Application Insights
/// instance. Fully opt-in: while disabled nothing ever leaves the machine.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public bool Enabled { get; init; } = false;
}
