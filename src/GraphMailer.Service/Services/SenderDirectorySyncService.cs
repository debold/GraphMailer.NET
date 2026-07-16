using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// Periodically refreshes the <see cref="TenantSenderDirectory"/> while sender
/// validation is enabled. Options are re-read every tick so enabling the feature
/// or changing the interval in the ConfigTool takes effect without a restart.
///
/// After every sync a status file is written (entry counts, next sync time) for
/// the ConfigTool to display, and the loop honours a "sync now" request file the
/// ConfigTool can drop (see <see cref="SenderDirectoryStatus"/>).
/// </summary>
internal sealed class SenderDirectorySyncService : BackgroundService
{
    // Short tick so a "sync now" request is picked up promptly; the actual
    // sync cadence is governed by RefreshIntervalMinutes.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly ITenantSenderDirectory _directory;
    private readonly IOptionsMonitor<SenderValidationOptions> _options;
    private readonly IOptionsMonitor<GraphApiOptions> _graphOptions;
    private readonly ILogger<SenderDirectorySyncService> _logger;

    public SenderDirectorySyncService(
        ITenantSenderDirectory directory,
        IOptionsMonitor<SenderValidationOptions> options,
        IOptionsMonitor<GraphApiOptions> graphOptions,
        ILogger<SenderDirectorySyncService> logger)
    {
        _directory = directory;
        _options = options;
        _graphOptions = graphOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[SenderValidation] Sync service started (enabled: {Enabled})",
            _options.CurrentValue.Enabled);

        var nextSyncUtc = DateTime.MinValue;   // sync immediately once active

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            var active = opts.Enabled && _graphOptions.CurrentValue.IsConfigured;
            var syncRequested = ConsumeSyncRequest();

            if (active && (syncRequested || DateTime.UtcNow >= nextSyncUtc))
            {
                if (syncRequested)
                    _logger.LogInformation("[SenderValidation] Manual sync requested via ConfigTool");

                var result = await _directory.RefreshAsync(stoppingToken);   // never throws
                nextSyncUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, opts.RefreshIntervalMinutes));
                WriteStatus(result, nextSyncUtc);
            }
            else if (!active)
            {
                nextSyncUtc = DateTime.MinValue;   // re-enable triggers an immediate sync
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[SenderValidation] Sync service stopped");
    }

    /// <summary>Returns true when a sync-now request file was present (and removes it).</summary>
    private bool ConsumeSyncRequest()
    {
        var path = SenderDirectoryStatus.SyncRequestFilePath;
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SenderValidation] Could not consume sync request file");
            return false;
        }
    }

    private void WriteStatus(SenderDirectoryRefreshResult result, DateTime nextSyncUtc)
    {
        try
        {
            new SenderDirectoryStatus
            {
                LastSyncUtc = DateTime.UtcNow,
                LastSyncSuccess = result.Success,
                UserCount = result.UserCount,
                AddressCount = result.AddressCount,
                LastError = result.Error,
                NextSyncUtc = nextSyncUtc,
            }.Save(SenderDirectoryStatus.StatusFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SenderValidation] Could not write sync status file");
        }
    }
}
