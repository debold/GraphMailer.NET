using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Backup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// Runs scheduled configuration backups (daily/weekly at a configured local time), applies
/// rotation, and optionally emails each backup. Re-reads <see cref="BackupOptions"/> at least
/// hourly so schedule changes are picked up without a restart.
/// </summary>
internal sealed class BackupBackgroundService : BackgroundService
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(5);

    private readonly IConfigBackupService _backup;
    private readonly IOptionsMonitor<BackupOptions> _options;
    private readonly IGraphApiClient _graph;
    private readonly IOptionsMonitor<AdminNotificationsOptions> _adminOptions;
    private readonly IAdminNotificationService _notify;
    private readonly ILogger<BackupBackgroundService> _logger;

    // The time we are currently waiting to fire at. Held across ticks: recomputing the
    // "next run" every tick always yields a future time, so the fire moment would never
    // be detected — we keep the target until it is reached, then clear it to recompute.
    private DateTimeOffset? _nextRun;
    private bool _pausedWarned;

    // Hot reload: an options change wakes the sleeping loop and forces a fresh re-schedule
    // so a changed time/frequency takes effect without a service restart.
    private volatile bool _configChanged;
    private volatile CancellationTokenSource? _wakeCts;

    internal enum TickAction { Idle, Wait, Run }

    public BackupBackgroundService(
        IConfigBackupService backup,
        IOptionsMonitor<BackupOptions> options,
        IGraphApiClient graph,
        IOptionsMonitor<AdminNotificationsOptions> adminOptions,
        IAdminNotificationService notify,
        ILogger<BackupBackgroundService> logger)
    {
        _backup = backup;
        _options = options;
        _graph = graph;
        _adminOptions = adminOptions;
        _notify = notify;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Backup] Scheduler started");

        // Wake the loop and force a re-schedule whenever the backup options change.
        using var changeReg = _options.OnChange(_ => MarkOptionsChanged());

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var (action, wait) = PlanTick(_options.CurrentValue, DateTimeOffset.Now);
                if (action == TickAction.Run)
                {
                    await RunBackupAsync(_options.CurrentValue, stoppingToken);
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
        _logger.LogInformation("[Backup] Scheduler stopped");
    }

    /// <summary>Sleeps for <paramref name="delay"/>, returning early if an options change wakes us.</summary>
    private async Task SleepAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _wakeCts = cts;
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // woken by a config change — loop re-evaluates
        }
        finally
        {
            _wakeCts = null;
        }
    }

    /// <summary>
    /// Decides what to do this tick. Holds <see cref="_nextRun"/> across calls so the fire
    /// moment is actually detected (recomputing "next run" each tick always returns a future
    /// time and would never fire). internal for unit testing.
    /// </summary>
    /// <summary>Signals that the backup options changed: wakes the sleeping loop and forces a re-schedule.</summary>
    internal void MarkOptionsChanged()
    {
        _configChanged = true;
        try { _wakeCts?.Cancel(); }
        catch (ObjectDisposedException) { /* sleep already ended */ }
    }

    internal (TickAction Action, TimeSpan Wait) PlanTick(BackupOptions opts, DateTimeOffset now)
    {
        if (_configChanged)
        {
            _configChanged = false;
            _nextRun = null;   // recompute from the new options
        }

        if (!opts.Enabled)
        {
            _nextRun = null;
            _pausedWarned = false;
            return (TickAction.Idle, IdlePoll);
        }

        string? pauseReason = null;
        DateTimeOffset? computed = null;
        if (string.IsNullOrEmpty(opts.Password))
            pauseReason = "no backup password is configured";
        else if ((computed = BackupSchedule.NextRun(opts, now)) is null)
            pauseReason = $"TimeOfDay '{opts.TimeOfDay}' is invalid";

        if (pauseReason is not null)
        {
            if (!_pausedWarned)
            {
                _logger.LogWarning("[Backup] Scheduled backups paused — {Reason}", pauseReason);
                _pausedWarned = true;
            }
            _nextRun = null;
            return (TickAction.Idle, IdlePoll);
        }
        _pausedWarned = false;

        // Adopt a fresh target when we have none (first run, after firing, or after an
        // options change clears it). A stable config keeps the same target until it fires.
        // pauseReason == null guarantees computed is set (see the checks above); unwrap the
        // field into a local once so the flow analysis can see it is non-null from here on.
        if (_nextRun is not DateTimeOffset next)
        {
            next = computed!.Value;
            _nextRun = next;
            _logger.LogInformation("[Backup] Next backup scheduled for {Time:yyyy-MM-dd HH:mm}",
                next.LocalDateTime);
        }

        if (now >= next)
        {
            _nextRun = null;   // recomputed (to the next occurrence) on the next tick
            return (TickAction.Run, TimeSpan.Zero);
        }

        var wait = next - now;
        return (TickAction.Wait, wait > MaxSleep ? MaxSleep : wait);
    }

    // internal so unit tests can trigger a single run without the timer loop
    internal async Task RunBackupAsync(BackupOptions opts, CancellationToken ct)
    {
        var directory = string.IsNullOrWhiteSpace(opts.Directory) ? AppPaths.BackupsDir : opts.Directory!;

        string path;
        int removed;
        try
        {
            path = _backup.WriteBackup(opts.Password!, directory);
            _logger.LogInformation("[Backup] Created configuration backup {Path}", path);

            removed = _backup.Rotate(directory, opts.MaxBackups);
            if (removed > 0)
                _logger.LogInformation("[Backup] Rotation removed {Count} old backup(s) (max {Max})", removed, opts.MaxBackups);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[Backup] Scheduled backup failed");
            await _notify.NotifyBackupResultAsync(false, $"Backup failed: {ex.Message}", ct);
            return;
        }

        var emailNote = string.Empty;
        if (opts.Email.Enabled && opts.Email.Recipients.Count > 0)
            emailNote = "\n" + await EmailBackupAsync(path, opts, ct);

        var summary =
            $"File: {Path.GetFileName(path)}{FormatSize(path)}\n" +
            $"Location: {directory}\n" +
            $"Retention: keep newest {opts.MaxBackups}" + (removed > 0 ? $" (removed {removed} old)" : "") +
            emailNote;
        await _notify.NotifyBackupResultAsync(true, summary, ct);
    }

    /// <summary>Emails the backup file; returns a one-line note for the status notification.</summary>
    private async Task<string> EmailBackupAsync(string path, BackupOptions opts, CancellationToken ct)
    {
        var sender = _adminOptions.CurrentValue.SenderAddress;
        if (string.IsNullOrWhiteSpace(sender))
        {
            _logger.LogWarning("[Backup] Email is enabled but AdminNotifications.SenderAddress is not set — backup not emailed");
            return "Email: not sent (no sender address configured)";
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var name = Path.GetFileName(path);
            await _graph.SendNotificationWithAttachmentAsync(
                sender, opts.Email.Recipients,
                subject: $"GraphMailer configuration backup — {name}",
                bodyText: "Attached is an encrypted GraphMailer configuration backup. " +
                          "Restore it via the ConfigTool's Backup & Restore page using the configured backup password.",
                attachmentName: name, attachmentBytes: bytes, attachmentContentType: "application/octet-stream",
                ct);
            _logger.LogInformation("[Backup] Emailed backup {Name} to {Count} recipient(s)", name, opts.Email.Recipients.Count);
            return $"Email: sent to {opts.Email.Recipients.Count} recipient(s)";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Backup] Failed to email backup");
            return $"Email: FAILED ({ex.Message})";
        }
    }

    private static string FormatSize(string path)
    {
        try { return $" ({new FileInfo(path).Length:N0} bytes)"; }
        catch { return string.Empty; }
    }
}
