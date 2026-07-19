using System.Runtime.InteropServices;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services.Telemetry;

/// <summary>
/// Runs the opt-in daily telemetry heartbeat (mirrors <see cref="UpdateCheck.UpdateCheckService"/>):
/// one "Heartbeat" event with anonymous install id, version, environment, aggregated mail
/// counters since the last send and the configuration shape, plus the PII-free error
/// reports accumulated by <see cref="TelemetrySink"/>. The schedule is persisted in
/// <see cref="TelemetryStatus"/> so restarts do not re-send; a failed transmission
/// retries hourly and keeps its counters/reports. Pending error reports are flushed
/// once more on shutdown. An unreachable ingestion endpoint is never an operational
/// fault of the relay — failures log Warning, no alerts.
/// </summary>
internal sealed class TelemetryService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(24);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly ITelemetrySender _sender;
    private readonly ErrorReportCollector _collector;
    private readonly IMetricsService _metrics;
    private readonly IOptionsMonitor<TelemetryOptions> _options;
    private readonly IOptionsMonitor<List<SmtpServerEntry>> _servers;
    private readonly IOptionsMonitor<MailQueueOptions> _mailQueue;
    private readonly ILogger<TelemetryService> _logger;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    // Overridable for unit tests (AppPaths is fixed per process).
    internal string StatusPath { get; set; } = TelemetryStatus.StatusFilePath;

    public TelemetryService(
        ITelemetrySender sender,
        ErrorReportCollector collector,
        IMetricsService metrics,
        IOptionsMonitor<TelemetryOptions> options,
        IOptionsMonitor<List<SmtpServerEntry>> servers,
        IOptionsMonitor<MailQueueOptions> mailQueue,
        ILogger<TelemetryService> logger)
    {
        _sender = sender;
        _collector = collector;
        _metrics = metrics;
        _options = options;
        _servers = servers;
        _mailQueue = mailQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Telemetry] Service started (enabled: {Enabled})",
            _options.CurrentValue.Enabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.CurrentValue.Enabled && IsHeartbeatDue(DateTime.UtcNow))
                await RunHeartbeatAsync(stoppingToken);

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[Telemetry] Service stopped");
    }

    /// <summary>Fires when no successful schedule exists yet or the persisted next-send time passed.</summary>
    internal bool IsHeartbeatDue(DateTime nowUtc)
    {
        var status = TelemetryStatus.TryLoad(StatusPath);
        return status?.NextHeartbeatUtc is not DateTime next || nowUtc >= next;
    }

    /// <summary>Sends one heartbeat + pending error reports and persists the status file.</summary>
    internal async Task RunHeartbeatAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var previous = TelemetryStatus.TryLoad(StatusPath);
        var installId = previous?.InstallId ?? Guid.NewGuid().ToString();
        var countersSince = previous?.CountersSinceUtc ?? now - HeartbeatInterval;

        var counts = await _metrics.GetAggregatesAsync(countersSince, ct);
        var (reports, overflow) = _collector.Drain();

        var success = false;
        string? error = null;
        try
        {
            _sender.TrackHeartbeat(
                BuildHeartbeatProperties(installId),
                BuildHeartbeatMetrics(counts, overflow));
            foreach (var report in reports)
                _sender.TrackErrorReport(BuildErrorReportProperties(report, installId));

            success = await _sender.FlushAsync(ct);
            if (!success)
                error = "Transmission failed (ingestion endpoint unreachable or rejected)";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _collector.Restore(reports, overflow);
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (success)
        {
            _logger.LogInformation(
                "[Telemetry] Heartbeat sent ({Sent} sent / {Failed} failed mails since {Since}, {Errors} error report(s))",
                counts.Sent, counts.Failed, countersSince, reports.Count);
        }
        else
        {
            // Keep the drained reports for the retry instead of losing them.
            _collector.Restore(reports, overflow);
            _logger.LogWarning("[Telemetry] Heartbeat transmission failed — retrying in {Retry}: {Error}",
                RetryInterval, error);
        }

        WriteStatus(new TelemetryStatus
        {
            InstallId = installId,
            LastHeartbeatUtc = now,
            NextHeartbeatUtc = now + (success ? HeartbeatInterval : RetryInterval),
            CountersSinceUtc = success ? now : countersSince,
            LastError = error,
        });
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Best-effort final flush so error reports collected since the last heartbeat
        // survive a service stop (e.g. a crash-loop would otherwise never report).
        try
        {
            if (!_options.CurrentValue.Enabled) return;
            var (reports, overflow) = _collector.Drain();
            if (reports.Count == 0 && overflow == 0) return;

            var installId = TelemetryStatus.TryLoad(StatusPath)?.InstallId ?? Guid.NewGuid().ToString();
            foreach (var report in reports)
                _sender.TrackErrorReport(BuildErrorReportProperties(report, installId));
            await _sender.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Telemetry] Final flush on shutdown failed");
        }
    }

    private IReadOnlyDictionary<string, string> BuildHeartbeatProperties(string installId)
    {
        var servers = (_servers.CurrentValue ?? []).Where(s => s.Enabled).ToList();
        return new Dictionary<string, string>
        {
            ["installId"] = installId,
            ["appVersion"] = BuildInfo.FileVersion,
            ["semVer"] = BuildInfo.InformationalVersion,
            ["osVersion"] = Environment.OSVersion.VersionString,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["listenerCount"] = servers.Count.ToString(),
            ["tlsEnabled"] = servers.Any(s => !string.Equals(s.Mode, "Plain", StringComparison.OrdinalIgnoreCase)).ToString(),
            ["authRequired"] = servers.Any(s => s.AuthRequired).ToString(),
            ["archivingEnabled"] = _mailQueue.CurrentValue.ArchiveSentEmails.ToString(),
        };
    }

    // PII boundary: aggregated counters only — never IPs, addresses or usernames.
    private IReadOnlyDictionary<string, double> BuildHeartbeatMetrics(MetricsAggregates counts, int overflow)
        => new Dictionary<string, double>
        {
            ["received"] = counts.Received,
            ["sent"] = counts.Sent,
            ["failed"] = counts.Failed,
            ["uptimeHours"] = Math.Round((DateTime.UtcNow - _startedUtc).TotalHours, 2),
            ["errorReportOverflow"] = overflow,
            ["sessionsTotal"] = counts.SessionsTotal,
            ["sessionsAborted"] = counts.SessionsAborted,
            ["sessionsFaulted"] = counts.SessionsFaulted,
            ["sessionsTls"] = counts.SessionsTls,
            ["sessionsAuthenticated"] = counts.SessionsAuthenticated,
            ["rejectionsTotal"] = counts.RejectionsTotal,
            ["rejectedIp"] = counts.RejectedIp,
            ["rejectedAuth"] = counts.RejectedAuth,
            ["rejectedSender"] = counts.RejectedSender,
            ["rejectedRecipient"] = counts.RejectedRecipient,
            ["rejectedSize"] = counts.RejectedSize,
            ["mailsWithAttachments"] = counts.MailsWithAttachments,
            ["deliveredFirstTry"] = counts.DeliveredFirstTry,
            ["deliveredAfterRetry"] = counts.DeliveredAfterRetry,
            ["deliveredViaUpload"] = counts.DeliveredViaUpload,
            ["avgQueueLatencyMs"] = Math.Round(counts.AvgQueueLatencyMs ?? 0, 1),
        };

    private static IReadOnlyDictionary<string, string> BuildErrorReportProperties(ErrorReport report, string installId)
    {
        var properties = new Dictionary<string, string>
        {
            ["installId"] = installId,
            ["appVersion"] = BuildInfo.FileVersion,
            ["fingerprint"] = report.Fingerprint,
            ["messageTemplate"] = report.MessageTemplate,
            ["component"] = report.Component,
            ["count"] = report.Count.ToString(),
            ["firstSeenUtc"] = report.FirstSeenUtc.ToString("O"),
            ["lastSeenUtc"] = report.LastSeenUtc.ToString("O"),
        };
        if (report.ExceptionType is not null) properties["exceptionType"] = report.ExceptionType;
        if (report.StackTrace is not null) properties["stackTrace"] = Truncate(report.StackTrace, 4000);
        if (report.GraphErrorCode is not null) properties["graphErrorCode"] = report.GraphErrorCode;
        if (report.HttpStatus is not null) properties["httpStatus"] = report.HttpStatus.Value.ToString();
        return properties;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private void WriteStatus(TelemetryStatus status)
    {
        try
        {
            status.Save(StatusPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Telemetry] Could not write status file");
        }
    }
}
