using System.Security.Cryptography;
using System.Text;

namespace GraphMailer.Service.Services.Telemetry;

/// <summary>
/// One aggregated, PII-free error observation, keyed by <see cref="Fingerprint"/>.
/// Carries only the log message <em>template</em> (placeholders, never rendered values),
/// exception type names and stack frames — no exception messages, addresses or hosts.
/// </summary>
internal sealed record ErrorReport(
    string Fingerprint,
    string MessageTemplate,
    string Component,
    string? ExceptionType,
    string? StackTrace,
    string? GraphErrorCode,
    int? HttpStatus,
    int Count,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

/// <summary>
/// Thread-safe in-memory aggregator between the Serilog sink and the daily telemetry
/// flush. Bounded: at most <see cref="MaxDistinctReports"/> distinct fingerprints are
/// kept per flush window; further new fingerprints only increment
/// <see cref="OverflowCount"/> so a log storm can never grow memory or the payload.
/// </summary>
internal sealed class ErrorReportCollector
{
    internal const int MaxDistinctReports = 50;

    private readonly object _lock = new();
    private readonly Dictionary<string, ErrorReport> _reports = [];
    private int _overflow;

    internal int OverflowCount { get { lock (_lock) return _overflow; } }

    internal void Record(
        string messageTemplate,
        string component,
        string? exceptionType,
        string? stackTrace,
        string? graphErrorCode,
        int? httpStatus)
    {
        var now = DateTime.UtcNow;
        var fingerprint = ComputeFingerprint(messageTemplate, exceptionType, stackTrace);

        lock (_lock)
        {
            if (_reports.TryGetValue(fingerprint, out var existing))
            {
                _reports[fingerprint] = existing with { Count = existing.Count + 1, LastSeenUtc = now };
                return;
            }

            if (_reports.Count >= MaxDistinctReports)
            {
                _overflow++;
                return;
            }

            _reports[fingerprint] = new ErrorReport(
                fingerprint, messageTemplate, component, exceptionType, stackTrace,
                graphErrorCode, httpStatus, Count: 1, FirstSeenUtc: now, LastSeenUtc: now);
        }
    }

    /// <summary>Returns all pending reports plus the overflow count and clears the collector.</summary>
    internal (IReadOnlyList<ErrorReport> Reports, int Overflow) Drain()
    {
        lock (_lock)
        {
            var reports = _reports.Values.ToList();
            var overflow = _overflow;
            _reports.Clear();
            _overflow = 0;
            return (reports, overflow);
        }
    }

    /// <summary>
    /// Puts drained reports back after a failed transmission (best effort — counts are
    /// merged when the same fingerprint re-occurred in the meantime).
    /// </summary>
    internal void Restore(IReadOnlyList<ErrorReport> reports, int overflow)
    {
        lock (_lock)
        {
            foreach (var report in reports)
            {
                if (_reports.TryGetValue(report.Fingerprint, out var existing))
                    _reports[report.Fingerprint] = existing with
                    {
                        Count = existing.Count + report.Count,
                        FirstSeenUtc = report.FirstSeenUtc < existing.FirstSeenUtc ? report.FirstSeenUtc : existing.FirstSeenUtc,
                    };
                else if (_reports.Count < MaxDistinctReports)
                    _reports[report.Fingerprint] = report;
                else
                    _overflow += report.Count;
            }
            _overflow += overflow;
        }
    }

    /// <summary>
    /// Stable identity of an error site: template + exception type + topmost stack frame.
    /// Two installs hitting the same bug produce the same fingerprint (App Insights
    /// groups on it), while distinct call sites of a shared template stay separate.
    /// </summary>
    internal static string ComputeFingerprint(string messageTemplate, string? exceptionType, string? stackTrace)
    {
        var topFrame = stackTrace?.Split('\n', 2)[0].Trim() ?? string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{messageTemplate}|{exceptionType}|{topFrame}"));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}
