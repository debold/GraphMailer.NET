using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// BackgroundService that monitors available disk space on the mail queue drive and reports the
/// result to <see cref="IAdminNotificationService"/> on every check — below
/// <see cref="DiskSpaceMonitoringOptions.ThresholdPercent"/> raises the alert, above it clears it.
/// How often that turns into an actual email is decided centrally, not here.
/// </summary>
internal sealed class DiskSpaceMonitoringService : BackgroundService
{
    private readonly IAdminNotificationService _notify;
    private readonly IOptionsMonitor<DiskSpaceMonitoringOptions> _options;
    private readonly IOptionsMonitor<MailQueueOptions> _queueOptions;
    private readonly ILogger<DiskSpaceMonitoringService> _logger;

    public DiskSpaceMonitoringService(
        IAdminNotificationService notify,
        IOptionsMonitor<DiskSpaceMonitoringOptions> options,
        IOptionsMonitor<MailQueueOptions> queueOptions,
        ILogger<DiskSpaceMonitoringService> logger)
    {
        _notify = notify;
        _options = options;
        _queueOptions = queueOptions;
        _logger = logger;
    }

    /// <summary>Guards against a hand-edited 0/negative interval turning into an invalid timer period.</summary>
    internal static TimeSpan Interval(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        _logger.LogInformation("[DiskMonitor] Started (enabled: {Enabled}, interval: {Min}min, threshold: {Pct}%)",
            opts.Enabled, opts.CheckIntervalMinutes, opts.ThresholdPercent);

        if (opts.Enabled)
            await CheckDiskSpaceAsync(opts, stoppingToken);   // check immediately on startup

        // The loop runs even while disabled so switching the monitor on takes effect without a
        // service restart — same for the interval, which is re-read on every tick.
        using var timer = new PeriodicTimer(Interval(opts.CheckIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var current = _options.CurrentValue;
                timer.Period = Interval(current.CheckIntervalMinutes);
                if (!current.Enabled) continue;

                await CheckDiskSpaceAsync(current, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("[DiskMonitor] Stopped");
    }

    internal async Task CheckDiskSpaceAsync(DiskSpaceMonitoringOptions opts, CancellationToken ct)
    {
        try
        {
            var mailDirOpt = _queueOptions.CurrentValue.MailDir;
            var mailDir = string.IsNullOrEmpty(mailDirOpt) ? AppPaths.MailDir : mailDirOpt;
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(mailDir)) ?? mailDir;
            var drive = new DriveInfo(driveRoot);

            if (!drive.IsReady)
            {
                _logger.LogWarning("[DiskMonitor] Drive {Drive} is not ready", driveRoot);
                return;
            }

            var freePct = (double)drive.AvailableFreeSpace / drive.TotalSize * 100.0;

            _logger.LogDebug("[DiskMonitor] Drive {Drive}: {Free:F1}% free ({FreeMb:F0} MB of {TotalGb:F1} GB)",
                driveRoot, freePct,
                drive.AvailableFreeSpace / 1024.0 / 1024.0,
                drive.TotalSize / 1024.0 / 1024.0 / 1024.0);

            if (freePct < opts.ThresholdPercent)
            {
                _logger.LogWarning("[DiskMonitor] Low disk space: {Free:F1}% free on {Drive} (threshold {Pct}%)",
                    freePct, driveRoot, opts.ThresholdPercent);
                await _notify.NotifyLowDiskSpaceAsync(driveRoot, freePct, ct);
            }
            else
            {
                // Reported on every healthy check, not only on the transition: the alert store knows
                // whether anything was raised, and this way an alert that predates a service restart
                // still gets its recovery mail.
                await _notify.NotifyDiskSpaceRecoveredAsync(driveRoot, freePct, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiskMonitor] Disk space check failed");
        }
    }
}
