using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services.Reporting;

/// <summary>
/// Sends the periodic (weekly/monthly) HTML operations report to the admin notification
/// recipients at the configured local time. Mirrors <c>BackupBackgroundService</c>: it holds
/// the next-run target across ticks, sleeps in ≤1 h slices, and re-schedules immediately when
/// <see cref="AdminNotificationsOptions"/> changes (hot reload, no restart).
/// </summary>
internal sealed class ScheduledReportService : BackgroundService
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<AdminNotificationsOptions> _options;
    private readonly ReportDataCollector _collector;
    private readonly IGraphApiClient _graph;
    private readonly ILogger<ScheduledReportService> _logger;

    private DateTimeOffset? _nextRun;
    private bool _pausedWarned;
    private volatile bool _configChanged;
    private volatile CancellationTokenSource? _wakeCts;

    internal enum TickAction { Idle, Wait, Run }

    public ScheduledReportService(
        IOptionsMonitor<AdminNotificationsOptions> options,
        ReportDataCollector collector,
        IGraphApiClient graph,
        ILogger<ScheduledReportService> logger)
    {
        _options = options;
        _collector = collector;
        _graph = graph;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Report] Scheduler started");
        using var changeReg = _options.OnChange(_ => MarkOptionsChanged());

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var (action, wait) = PlanTick(_options.CurrentValue, DateTimeOffset.Now);
                if (action == TickAction.Run)
                {
                    await RunReportAsync(_options.CurrentValue, stoppingToken);
                    await SleepAsync(TimeSpan.FromMinutes(1), stoppingToken); // avoid a double run in the same minute
                }
                else
                {
                    await SleepAsync(wait, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        _logger.LogInformation("[Report] Scheduler stopped");
    }

    private async Task SleepAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _wakeCts = cts;
        try { await Task.Delay(delay, cts.Token); }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* woken by config change */ }
        finally { _wakeCts = null; }
    }

    internal void MarkOptionsChanged()
    {
        _configChanged = true;
        try { _wakeCts?.Cancel(); }
        catch (ObjectDisposedException) { /* sleep already ended */ }
    }

    /// <summary>
    /// Decides what to do this tick. Holds <see cref="_nextRun"/> across calls so the fire
    /// moment is detected. internal for unit testing.
    /// </summary>
    internal (TickAction Action, TimeSpan Wait) PlanTick(AdminNotificationsOptions opts, DateTimeOffset now)
    {
        if (_configChanged)
        {
            _configChanged = false;
            _nextRun = null;
        }

        var report = opts.ScheduledReport;
        if (!report.Enabled)
        {
            _nextRun = null;
            _pausedWarned = false;
            return (TickAction.Idle, IdlePoll);
        }

        string? pauseReason = null;
        DateTimeOffset? computed = null;
        if (string.IsNullOrWhiteSpace(opts.SenderAddress))
            pauseReason = "no sender address is configured";
        else if (opts.RecipientAddresses.Count == 0)
            pauseReason = "no notification recipients are configured";
        else if ((computed = ReportSchedule.NextRun(report, now)) is null)
            pauseReason = $"TimeOfDay '{report.TimeOfDay}' is invalid";

        if (pauseReason is not null)
        {
            if (!_pausedWarned)
            {
                _logger.LogWarning("[Report] Scheduled report paused — {Reason}", pauseReason);
                _pausedWarned = true;
            }
            _nextRun = null;
            return (TickAction.Idle, IdlePoll);
        }
        _pausedWarned = false;

        // pauseReason == null guarantees computed is set (see the checks above); unwrap the
        // field into a local once so the flow analysis can see it is non-null from here on.
        if (_nextRun is not DateTimeOffset next)
        {
            next = computed!.Value;
            _nextRun = next;
            _logger.LogInformation("[Report] Next report scheduled for {Time:yyyy-MM-dd HH:mm}", next.LocalDateTime);
        }

        if (now >= next)
        {
            _nextRun = null;
            return (TickAction.Run, TimeSpan.Zero);
        }

        var wait = next - now;
        return (TickAction.Wait, wait > MaxSleep ? MaxSleep : wait);
    }

    // internal so unit tests can trigger a single run without the timer loop
    internal async Task RunReportAsync(AdminNotificationsOptions opts, CancellationToken ct)
    {
        try
        {
            var data = _collector.Collect(opts.ScheduledReport, DateTimeOffset.Now);
            var email = HtmlReportRenderer.Render(data);
            var subject = $"{opts.SubjectPrefix} {data.Title} — {data.PeriodStart:yyyy-MM-dd} to {data.PeriodEnd:yyyy-MM-dd}";

            var inlineChart = email.ChartPng is null
                ? null
                : new GraphInlineImage(email.ChartContentId, "image/png", email.ChartPng);

            _logger.LogInformation("[Report] Sending {Title} to {Count} recipient(s)", data.Title, opts.RecipientAddresses.Count);
            var sent = await _graph.SendHtmlNotificationAsync(opts.SenderAddress!, opts.RecipientAddresses, subject, email.Html, inlineChart, ct);
            if (sent)
                _logger.LogInformation("[Report] {Title} delivered", data.Title);
            else
                _logger.LogWarning("[Report] {Title} could not be sent — see prior Graph API warning", data.Title);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[Report] Failed to build or send the scheduled report");
        }
    }
}
