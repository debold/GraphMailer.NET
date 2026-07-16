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

    private bool _wasDown = false;

    // The set of missing roles last alerted on ("Mail.ReadWrite,User.Read.All"),
    // so each distinct gap is reported exactly once until it changes or is fixed.
    private string? _notifiedMissingRoles;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            _logger.LogDebug("[GraphMonitor] Graph API monitoring disabled");
            return;
        }

        _logger.LogInformation("[GraphMonitor] Started (interval: {Min}min)", opts.CheckIntervalMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(opts.CheckIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckConnectivityAsync(stoppingToken);
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
                await _notify.NotifyGraphApiRestoredAsync(ct);
            }
            else
            {
                _logger.LogDebug("[GraphMonitor] Graph API reachable");
            }

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
                await _notify.NotifyGraphApiErrorAsync(ex.Message, ct);
            }
            else
            {
                _logger.LogDebug("[GraphMonitor] Graph API still unavailable: {Msg}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Compares the application permissions granted to the token against what
    /// GraphMailer needs and alerts once per distinct gap.
    /// </summary>
    private async Task CheckPermissionsAsync(IReadOnlyCollection<string> granted, CancellationToken ct)
    {
        var required = new List<(string Role, string Purpose)>
        {
            ("Mail.Send", "mail delivery"),
            ("Mail.ReadWrite", "attachments ≥ 3 MB"),
        };
        if (_senderValidation.CurrentValue.Enabled)
            required.Add(("User.Read.All", "sender validation"));

        var missing = required
            .Where(r => !granted.Contains(r.Role, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count == 0)
        {
            if (_notifiedMissingRoles is not null)
            {
                _logger.LogInformation("[GraphMonitor] All required Graph permissions are granted again");
                _notifiedMissingRoles = null;
            }
            return;
        }

        var missingKey = string.Join(",", missing.Select(m => m.Role));
        if (missingKey == _notifiedMissingRoles)
            return;   // this exact gap was already reported

        _notifiedMissingRoles = missingKey;
        var detail = string.Join(", ", missing.Select(m => $"{m.Role} (needed for {m.Purpose})"));

        _logger.LogError(
            "[GraphMonitor] The app registration is missing required application permissions: {Missing}. " +
            "Re-run the Entra setup wizard or grant them in Entra ID (admin consent required).",
            detail);
        await _notify.NotifyGraphApiErrorAsync(
            $"Missing Graph application permissions: {detail}. " +
            "Re-run the Entra setup wizard or grant them in Entra ID (admin consent required).", ct);
    }
}
