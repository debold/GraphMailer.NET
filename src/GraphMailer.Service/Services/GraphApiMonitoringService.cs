using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// BackgroundService that periodically verifies Graph API connectivity by acquiring an
/// OAuth2 access token (see <see cref="GraphConnectivityProbe"/>). Sends admin
/// notifications when connectivity is lost or restored.
/// </summary>
internal sealed class GraphApiMonitoringService : BackgroundService
{
    private readonly IGraphConnectivityProbe _probe;
    private readonly IAdminNotificationService _notify;
    private readonly IOptionsMonitor<GraphApiMonitoringOptions> _options;
    private readonly IOptionsMonitor<GraphApiOptions> _graphOptions;
    private readonly IOptionsMonitor<SenderValidationOptions> _senderValidation;
    private readonly ILogger<GraphApiMonitoringService> _logger;

    /// <summary>
    /// Whether the previous probe failed. Purely log verbosity — without it a long outage would
    /// write one Error per check into error-*.log. The notification cadence is unrelated and lives
    /// in <see cref="IAlertStateStore"/>.
    /// </summary>
    private bool _wasDown = false;

    /// <summary>
    /// The permission gap last written to the log ("Mail.ReadWrite,User.Read.All"). Log verbosity
    /// only, same reasoning as <see cref="_wasDown"/>.
    /// </summary>
    private string? _loggedMissingRoles;

    public GraphApiMonitoringService(
        IGraphConnectivityProbe probe,
        IAdminNotificationService notify,
        IOptionsMonitor<GraphApiMonitoringOptions> options,
        IOptionsMonitor<GraphApiOptions> graphOptions,
        IOptionsMonitor<SenderValidationOptions> senderValidation,
        ILogger<GraphApiMonitoringService> logger)
    {
        _probe = probe;
        _notify = notify;
        _options = options;
        _graphOptions = graphOptions;
        _senderValidation = senderValidation;
        _logger = logger;
    }

    /// <summary>Guards against a hand-edited 0/negative interval turning into an invalid timer period.</summary>
    internal static TimeSpan Interval(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        _logger.LogInformation("[GraphMonitor] Started (enabled: {Enabled}, interval: {Min}min)",
            opts.Enabled, opts.CheckIntervalMinutes);

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

                await CheckConnectivityAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("[GraphMonitor] Stopped");
    }

    // internal so unit tests can drive single checks without the timer
    internal async Task CheckConnectivityAsync(CancellationToken ct)
    {
        if (!_graphOptions.CurrentValue.IsConfigured)
        {
            _logger.LogDebug("[GraphMonitor] Graph API not configured – skipping check");
            return;
        }

        try
        {
            var result = await _probe.ProbeAsync(ct);

            if (_wasDown)
            {
                _logger.LogInformation("[GraphMonitor] Graph API connectivity restored");
                _wasDown = false;
            }
            else
            {
                _logger.LogDebug("[GraphMonitor] Graph API reachable");
            }

            // Offered on every healthy probe, not only on the transition: the alert store knows
            // whether anything was raised, and this way an outage that predates a service restart
            // still gets its recovery mail.
            await _notify.NotifyGraphApiRestoredAsync(ct);

            await CheckPermissionsAsync(result.GrantedRoles, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown — not an outage
        }
        catch (Exception ex)
        {
            if (!_wasDown)
            {
                _wasDown = true;
                _logger.LogError(ex, "[GraphMonitor] Graph API connectivity error");
            }
            else
            {
                _logger.LogDebug("[GraphMonitor] Graph API still unavailable: {Msg}", ex.Message);
            }

            await _notify.NotifyGraphApiErrorAsync(ex.Message, ct);
        }
    }

    /// <summary>
    /// Compares the application permissions granted to the token against what GraphMailer needs and
    /// reports the current state. A changed gap counts as a new condition and is mailed immediately;
    /// an unchanged one follows the global repeat interval.
    /// </summary>
    private async Task CheckPermissionsAsync(IReadOnlyCollection<string> granted, CancellationToken ct)
    {
        var required = new List<(string Role, string Purpose)>
        {
            ("Mail.Send", "mail delivery"),
            ("Mail.ReadWrite", "attachments ≥ 3 MB"),
        };
        var senderValidation = _senderValidation.CurrentValue;
        if (senderValidation.Enabled)
        {
            required.Add(("User.Read.All", "sender validation"));

            // Needed whenever validation runs, not only for the opt-in below: the tenant's mail
            // domains decide which of a mailbox's synced addresses can actually send.
            required.Add(("Domain.Read.All", "recognising the tenant's mail domains"));

            if (senderValidation.AcceptMailboxlessSenders)
                required.Add(("Group.Read.All", "groups as senders"));
        }

        var missing = required
            .Where(r => !granted.Contains(r.Role, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var missingRoles = missing.Select(m => m.Role).ToList();

        if (missing.Count == 0)
        {
            if (_loggedMissingRoles is not null)
            {
                _logger.LogInformation("[GraphMonitor] All required Graph permissions are granted again");
                _loggedMissingRoles = null;
            }
            await _notify.NotifyGraphPermissionsRestoredAsync(ct);
            return;
        }

        var detail = string.Join(", ", missing.Select(m => $"{m.Role} (needed for {m.Purpose})"));
        var missingKey = string.Join(",", missingRoles);

        if (_loggedMissingRoles != missingKey)
        {
            _loggedMissingRoles = missingKey;
            _logger.LogError(
                "[GraphMonitor] The app registration is missing required application permissions: {Missing}. " +
                "Re-run the Entra setup wizard or grant them in Entra ID (admin consent required).",
                detail);
        }

        await _notify.NotifyGraphPermissionsMissingAsync(missingRoles, detail, ct);
    }
}
