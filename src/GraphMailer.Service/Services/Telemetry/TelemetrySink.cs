using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models.ODataErrors;
using Serilog.Core;
using Serilog.Events;

namespace GraphMailer.Service.Services.Telemetry;

/// <summary>
/// Serilog sink feeding Error/Fatal log events into the <see cref="ErrorReportCollector"/>
/// while telemetry is enabled (checked per event — hot-reload friendly, no restart).
///
/// PII guarantee: only the raw message <em>template</em> (<c>"[Queue] Delivery failed for
/// {MessageId}"</c>) is captured, never the rendered message or property values, and
/// exception <em>messages</em> are dropped — only type names and stack frames are kept.
/// Structured Graph error codes / HTTP status are machine constants, not user data.
/// </summary>
internal sealed class TelemetrySink : ILogEventSink
{
    private readonly ErrorReportCollector _collector;
    private readonly IOptionsMonitor<TelemetryOptions> _options;

    public TelemetrySink(ErrorReportCollector collector, IOptionsMonitor<TelemetryOptions> options)
    {
        _collector = collector;
        _options = options;
    }

    public void Emit(LogEvent logEvent)
    {
        // A sink must never disturb the logging pipeline — swallow everything.
        try
        {
            if (logEvent.Level < LogEventLevel.Error) return;
            if (!_options.CurrentValue.Enabled) return;

            var template = logEvent.MessageTemplate.Text;
            var (graphCode, httpStatus) = ExtractGraphError(logEvent.Exception);

            _collector.Record(
                template,
                ExtractComponent(template),
                ExceptionTypeChain(logEvent.Exception),
                logEvent.Exception?.StackTrace,
                graphCode,
                httpStatus);
        }
        catch
        {
            // Never let telemetry break logging.
        }
    }

    /// <summary>Leading <c>[Component]</c> prefix per the logging conventions; "unknown" otherwise.</summary>
    internal static string ExtractComponent(string messageTemplate)
    {
        if (messageTemplate.StartsWith('['))
        {
            var end = messageTemplate.IndexOf(']');
            if (end > 1) return messageTemplate[1..end];
        }
        return "unknown";
    }

    /// <summary>Outer-to-inner exception type names (<c>A -&gt; B</c>) — types only, no messages.</summary>
    internal static string? ExceptionTypeChain(Exception? ex)
    {
        if (ex is null) return null;
        var types = new List<string>();
        for (var e = ex; e is not null && types.Count < 5; e = e.InnerException)
            types.Add(e.GetType().FullName ?? e.GetType().Name);
        return string.Join(" -> ", types);
    }

    /// <summary>Structured Graph error code + HTTP status from an <see cref="ODataError"/> anywhere in the chain.</summary>
    internal static (string? Code, int? HttpStatus) ExtractGraphError(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is ODataError odata)
                return (odata.Error?.Code, odata.ResponseStatusCode);
        return (null, null);
    }
}
