namespace GraphMailer.Service.Services.Telemetry;

/// <summary>
/// Transport abstraction over the Application Insights client so the telemetry service
/// is unit-testable. Track* methods only buffer; nothing leaves the machine until
/// <see cref="FlushAsync"/> — a failed flush returns false and never throws.
/// </summary>
internal interface ITelemetrySender
{
    void TrackHeartbeat(IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, double> metrics);
    void TrackErrorReport(IReadOnlyDictionary<string, string> properties);
    Task<bool> FlushAsync(CancellationToken ct = default);
}
