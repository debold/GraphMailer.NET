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
    private readonly IOptionsMonitor<SenderRoutingOptions> _routingOptions;
    private readonly ILogger<SenderDirectorySyncService> _logger;

    public SenderDirectorySyncService(
        ITenantSenderDirectory directory,
        IOptionsMonitor<SenderValidationOptions> options,
        IOptionsMonitor<GraphApiOptions> graphOptions,
        IOptionsMonitor<SenderRoutingOptions> routingOptions,
        ILogger<SenderDirectorySyncService> logger)
    {
        _directory = directory;
        _options = options;
        _graphOptions = graphOptions;
        _routingOptions = routingOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[SenderValidation] Sync service started (enabled: {Enabled})",
            _options.CurrentValue.Enabled);

        WarnAboutUndeliverableAcceptance();

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
                if (result.Success) WriteSnapshot();
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

    /// <summary>
    /// Warns about a combination the ConfigTool prevents but a hand-edited config can still hold:
    /// accepting senders that have no mailbox, with no relay mailbox to deliver them through.
    /// Those senders then pass MAIL FROM and are bounced by Graph with a 404 instead — an NDR
    /// where the operator would otherwise have received a clean 550 during the SMTP session.
    /// </summary>
    internal void WarnAboutUndeliverableAcceptance()
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled || !opts.AcceptMailboxlessSenders) return;

        var routing = _routingOptions.CurrentValue;
        if (routing.Enabled && !string.IsNullOrWhiteSpace(routing.RelayMailbox)) return;

        _logger.LogWarning(
            "[SenderValidation] AcceptMailboxlessSenders accepts senders that have no mailbox, but " +
            "SenderRouting has no relay mailbox — those senders pass MAIL FROM and are then rejected by " +
            "Graph, producing an NDR instead of an immediate 550. Configure a relay mailbox under " +
            "SenderRouting, or turn AcceptMailboxlessSenders off.");
    }

    /// <summary>
    /// Writes the recognised recipients out for the ConfigTool's read-only directory viewer.
    /// Purely informational — a failure here must never disturb mail flow, so it is logged at
    /// Debug and otherwise ignored, exactly like the status file.
    /// </summary>
    private void WriteSnapshot()
    {
        try
        {
            SenderDirectorySnapshot
                .From(_directory.Recipients(), _directory.MailDomains(), DateTime.UtcNow)
                .Save(SenderDirectorySnapshot.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SenderValidation] Could not write directory snapshot file");
        }
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
                GroupCount = result.GroupCount,
                DomainCount = result.DomainCount,
                LastError = result.Error,
                LastWarning = result.Warning,
                NextSyncUtc = nextSyncUtc,
            }.Save(SenderDirectoryStatus.StatusFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SenderValidation] Could not write sync status file");
        }
    }
}
