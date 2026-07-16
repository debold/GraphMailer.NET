using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// BackgroundService that monitors available disk space on the mail queue drive.
/// Sends an admin notification when free space falls below <see cref="DiskSpaceMonitoringOptions.ThresholdPercent"/>.
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            _logger.LogDebug("[DiskMonitor] Disk space monitoring disabled");
            return;
        }

        _logger.LogInformation("[DiskMonitor] Started (interval: {Min}min, threshold: {Pct}%)",
            opts.CheckIntervalMinutes, opts.ThresholdPercent);

        // Check immediately on startup
        await CheckDiskSpaceAsync(opts, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(opts.CheckIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckDiskSpaceAsync(_options.CurrentValue, stoppingToken);
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("[DiskMonitor] Stopped");
    }

    private async Task CheckDiskSpaceAsync(DiskSpaceMonitoringOptions opts, CancellationToken ct)
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
                _logger.LogWarning("[DiskMonitor] Low disk space: {Free:F1}% free on {Drive}", freePct, driveRoot);
                await _notify.NotifyLowDiskSpaceAsync(driveRoot, freePct, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiskMonitor] Disk space check failed");
        }
    }
}
