using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;

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
        if (metrics is not null)
            foreach (var (key, value) in metrics) telemetry.Metrics[key] = value;
        _client.TrackEvent(telemetry);
    }

    /// <summary>PII guarantee: the SDK defaults these tags to the machine's hostname.</summary>
    private static void Scrub(TelemetryContext context)
    {
        context.Cloud.RoleName = "GraphMailer";
        context.Cloud.RoleInstance = "-";
        context.GetInternalContext().NodeName = "-";
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
