using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services.UpdateCheck;

/// <summary>
/// Runs the opt-in weekly GitHub release check. The last check time is persisted in the
/// status file (<see cref="UpdateCheckStatus"/>), so service restarts do not re-check;
/// a failed check retries after one day instead of a week. The ConfigTool can force an
/// immediate check by dropping a request file — honoured even while the periodic check
/// is disabled, because it is an explicit user action (mirrors
/// <see cref="SenderDirectorySyncService"/>). A new release triggers at most one admin
/// e-mail per version (<see cref="UpdateCheckStatus.LastNotifiedVersion"/>).
/// </summary>
internal sealed class UpdateCheckService : BackgroundService
{
    // Short tick so a "check now" request is picked up promptly; the actual
    // check cadence is governed by CheckInterval below.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromDays(1);

    private readonly IUpdateChecker _checker;
    private readonly IOptionsMonitor<UpdateCheckOptions> _options;
    private readonly IAdminNotificationService _notifications;
    private readonly ILogger<UpdateCheckService> _logger;

    // Overridable for unit tests (AppPaths is fixed per process).
    internal string StatusPath { get; set; } = UpdateCheckStatus.StatusFilePath;
    internal string RequestPath { get; set; } = UpdateCheckStatus.CheckRequestFilePath;

    public UpdateCheckService(
        IUpdateChecker checker,
        IOptionsMonitor<UpdateCheckOptions> options,
        IAdminNotificationService notifications,
        ILogger<UpdateCheckService> logger)
    {
        _checker = checker;
        _options = options;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[UpdateCheck] Service started (enabled: {Enabled})",
            _options.CurrentValue.Enabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            var checkRequested = ConsumeCheckRequest();
            if (checkRequested)
                _logger.LogInformation("[UpdateCheck] Manual check requested via ConfigTool");

            if (checkRequested || (_options.CurrentValue.Enabled && IsCheckDue(DateTime.UtcNow)))
                await RunCheckAsync(stoppingToken);

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[UpdateCheck] Service stopped");
    }

    /// <summary>The periodic check fires when no successful schedule exists yet or the persisted next-check time passed.</summary>
    internal bool IsCheckDue(DateTime nowUtc)
    {
        var status = UpdateCheckStatus.TryLoad(StatusPath);
        return status?.NextCheckUtc is not DateTime next || nowUtc >= next;
    }

    /// <summary>Runs one check, persists the status file and sends the (opt-in) admin mail once per new version.</summary>
    internal async Task RunCheckAsync(CancellationToken ct)
    {
        var result = await _checker.CheckAsync(ct);   // never throws
        var now = DateTime.UtcNow;
        var previous = UpdateCheckStatus.TryLoad(StatusPath);

        var status = new UpdateCheckStatus
        {
            CurrentVersion = result.CurrentVersion,
            LastCheckUtc = now,
            NextCheckUtc = now + (result.Success ? CheckInterval : RetryInterval),
            LastNotifiedVersion = previous?.LastNotifiedVersion,
        };

        if (result.Success)
        {
            status.LatestVersion = result.LatestVersion;
            status.UpdateAvailable = result.UpdateAvailable;
            status.ReleaseUrl = result.ReleaseUrl;
            status.ReleaseName = result.ReleaseName;
            status.PublishedUtc = result.PublishedUtc;

            if (result.UpdateAvailable)
            {
                _logger.LogInformation("[UpdateCheck] Update available: {Latest} (running {Current})",
                    result.LatestVersion, result.CurrentVersion);

                if (!string.Equals(status.LastNotifiedVersion, result.LatestVersion, StringComparison.Ordinal))
                {
                    await _notifications.NotifyUpdateAvailableAsync(
                        result.CurrentVersion, result.LatestVersion!, result.ReleaseUrl, ct);
                    status.LastNotifiedVersion = result.LatestVersion;
                }
            }
            else
            {
                _logger.LogInformation("[UpdateCheck] Up to date ({Current})", result.CurrentVersion);
            }
        }
        else
        {
            // Keep the last successful release info visible in the ConfigTool.
            status.LatestVersion = previous?.LatestVersion;
            status.UpdateAvailable = previous?.UpdateAvailable ?? false;
            status.ReleaseUrl = previous?.ReleaseUrl;
            status.ReleaseName = previous?.ReleaseName;
            status.PublishedUtc = previous?.PublishedUtc;
            status.LastError = result.Error;
            // Failure details were already logged by the checker; no alert mail —
            // an unreachable github.com is not an operational fault of the relay.
        }

        WriteStatus(status);
    }

    /// <summary>Returns true when a check-now request file was present (and removes it).</summary>
    internal bool ConsumeCheckRequest()
    {
        try
        {
            if (!File.Exists(RequestPath)) return false;
            File.Delete(RequestPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[UpdateCheck] Could not consume check request file");
            return false;
        }
    }

    private void WriteStatus(UpdateCheckStatus status)
    {
        try
        {
            status.Save(StatusPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[UpdateCheck] Could not write status file");
        }
    }
}
