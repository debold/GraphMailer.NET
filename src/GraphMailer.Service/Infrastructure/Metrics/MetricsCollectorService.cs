using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace GraphMailer.Service.Infrastructure.Metrics;

/// <summary>
/// BackgroundService that periodically samples system performance metrics
/// (memory, disk) and purges old records from the SQLite database.
/// </summary>
internal sealed class MetricsCollectorService : BackgroundService
{
    private readonly MetricsService _metrics;
    private readonly IOptionsMonitor<MetricsOptions> _options;
    private readonly IOptionsMonitor<MailQueueOptions> _queueOptions;
    private readonly ILogger<MetricsCollectorService> _logger;

    // CPU sampling requires two measurements to compute a delta
    private DateTime _lastCpuSampleTime = DateTime.MinValue;
    private TimeSpan _lastCpuTime = TimeSpan.Zero;

    public MetricsCollectorService(
        MetricsService metrics,
        IOptionsMonitor<MetricsOptions> options,
        IOptionsMonitor<MailQueueOptions> queueOptions,
        ILogger<MetricsCollectorService> logger)
    {
        _metrics = metrics;
        _options = options;
        _queueOptions = queueOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            _logger.LogDebug("[Metrics] Metrics collection disabled – collector not started");
            return;
        }

        _logger.LogInformation("[Metrics] Collector started");

        var memInterval = TimeSpan.FromSeconds(opts.PerformanceMetrics.MemoryIntervalSeconds);
        var cpuInterval = TimeSpan.FromSeconds(opts.PerformanceMetrics.CpuIntervalSeconds);
        var diskInterval = TimeSpan.FromSeconds(opts.PerformanceMetrics.DiskIntervalSeconds);
        var cleanupInterval = TimeSpan.FromHours(opts.CleanupIntervalHours);

        using var memTimer = new PeriodicTimer(memInterval);
        using var cpuTimer = new PeriodicTimer(cpuInterval);
        using var diskTimer = new PeriodicTimer(diskInterval);
        using var cleanupTimer = new PeriodicTimer(cleanupInterval);

        async Task MemLoop()
        {
            try { while (await memTimer.WaitForNextTickAsync(stoppingToken)) await SampleMemoryAsync(stoppingToken); }
            catch (OperationCanceledException) { }
        }

        async Task CpuLoop()
        {
            try { while (await cpuTimer.WaitForNextTickAsync(stoppingToken)) await SampleCpuAsync(stoppingToken); }
            catch (OperationCanceledException) { }
        }

        async Task DiskLoop()
        {
            try { while (await diskTimer.WaitForNextTickAsync(stoppingToken)) await SampleDiskAsync(stoppingToken); }
            catch (OperationCanceledException) { }
        }

        async Task CleanupLoop()
        {
            try { while (await cleanupTimer.WaitForNextTickAsync(stoppingToken)) await _metrics.CleanupOldRecordsAsync(stoppingToken); }
            catch (OperationCanceledException) { }
        }

        await Task.WhenAll(MemLoop(), CpuLoop(), DiskLoop(), CleanupLoop());

        _logger.LogInformation("[Metrics] Collector stopped");
    }

    private async Task SampleMemoryAsync(CancellationToken ct)
    {
        try
        {
            // WorkingSet = total process memory as shown in Task Manager
            var memMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
            await _metrics.RecordPerfMetricAsync("memory_mb", memMb, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to sample memory");
        }
    }

    private async Task SampleCpuAsync(CancellationToken ct)
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpuTime = proc.TotalProcessorTime;

            if (_lastCpuSampleTime != DateTime.MinValue)
            {
                var wallMs = (now - _lastCpuSampleTime).TotalMilliseconds;
                var cpuMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                if (wallMs > 0)
                {
                    var cpuPct = Math.Min(100.0, cpuMs / (wallMs * Environment.ProcessorCount) * 100.0);
                    await _metrics.RecordPerfMetricAsync("cpu_percent", cpuPct, ct);
                }
            }

            _lastCpuSampleTime = now;
            _lastCpuTime = cpuTime;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to sample CPU");
        }
    }

    private async Task SampleDiskAsync(CancellationToken ct)
    {
        try
        {
            var mailDirOpt = _queueOptions.CurrentValue.MailDir;
            var mailDir = string.IsNullOrEmpty(mailDirOpt) ? AppPaths.MailDir : mailDirOpt;
            var drive = new DriveInfo(Path.GetPathRoot(mailDir) ?? mailDir);
            var freePct = drive.TotalSize > 0
                ? (double)drive.AvailableFreeSpace / drive.TotalSize * 100.0
                : 100.0;

            await _metrics.RecordPerfMetricAsync("disk_free_percent", freePct, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Metrics] Failed to sample disk space");
        }
    }
}
