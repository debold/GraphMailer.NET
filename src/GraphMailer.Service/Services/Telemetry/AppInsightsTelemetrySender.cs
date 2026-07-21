using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace GraphMailer.Service.Services.Telemetry;

/// <summary>
/// Sends telemetry as <c>customEvents</c> ("Heartbeat" / "ErrorReport") to the
/// developer's Application Insights instance. The connection string is deliberately
/// compiled in: it is a write-only ingestion address (no read or management access),
/// which is the standard model for client-side telemetry; the ingestion side is
/// protected by a daily cap. Machine-identifying context (role instance / node name,
/// normally auto-filled with the hostname by the SDK) is scrubbed before send.
/// </summary>
internal sealed class AppInsightsTelemetrySender : ITelemetrySender, IDisposable
{
    internal const string ConnectionString =
        "InstrumentationKey=d13f4f8f-ba5e-4c52-8730-fffb38e85def;" +
        "IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/;" +
        "LiveEndpoint=https://westeurope.livediagnostics.monitor.azure.com/;" +
        "ApplicationId=454ac8e2-078b-4870-92c0-01fc27274c45";

    private readonly TelemetryConfiguration _configuration;
    private readonly TelemetryClient _client;

    public AppInsightsTelemetrySender()
    {
        _configuration = new TelemetryConfiguration { ConnectionString = ConnectionString };
        _client = new TelemetryClient(_configuration);
    }

    public void TrackHeartbeat(IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, double> metrics)
        => Track("Heartbeat", properties, metrics);

    public void TrackErrorReport(IReadOnlyDictionary<string, string> properties)
        => Track("ErrorReport", properties, metrics: null);

    private void Track(string eventName, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, double>? metrics)
    {
        var telemetry = new EventTelemetry(eventName);
        Scrub(telemetry.Context);
        foreach (var (key, value) in properties) telemetry.Properties[key] = value;
        // SDK 3.x dropped EventTelemetry.Metrics: an AI custom event maps to an OpenTelemetry
        // log record, which has no counterpart to customMeasurements. The heartbeat numbers ride
        // along as customDimensions instead — invariant culture so "1.5" never becomes "1,5" on
        // a German machine and breaks todouble() on the query side.
        if (metrics is not null)
            foreach (var (key, value) in metrics)
                telemetry.Properties[key] = value.ToString(CultureInfo.InvariantCulture);
        _client.TrackEvent(telemetry);
    }

    /// <summary>
    /// PII guarantee: the SDK defaults the role instance to the machine's hostname.
    /// SDK 2.x additionally carried an internal context with a NodeName tag that had to be
    /// cleared; 3.x has no such context — CloudContext is the only place a hostname can surface.
    ///
    /// Location.Ip is set explicitly because the ingestion endpoint otherwise geo-resolves the
    /// connection IP to city level and stores it as client_City/StateOrProvince/CountryOrRegion.
    /// The IP itself is masked either way, but the derived location is not — and the help page
    /// promises users an exhaustive list of what leaves their machine. Sending a placeholder
    /// makes the endpoint use that instead of looking anything up.
    /// </summary>
    private static void Scrub(TelemetryContext context)
    {
        context.Cloud.RoleName = "GraphMailer";
        context.Cloud.RoleInstance = "-";
        context.Location.Ip = "0.0.0.0";
    }

    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        try
        {
            return await _client.FlushAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            // DNS failure, proxy error, TLS problem — all map to "transmission failed".
            return false;
        }
    }

    public void Dispose() => _configuration.Dispose();
}
