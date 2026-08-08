using System.Net.Sockets;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// BackgroundService that periodically performs TCP health checks on configured SMTP ports.
/// Reports a port as down once it has been unreachable for longer than
/// <see cref="PortMonitoringOptions.OutageAlertThresholdMinutes"/>, and as healthy on every
/// successful check. Whether that becomes an email is decided centrally by
/// <see cref="IAdminNotificationService"/>.
/// </summary>
internal sealed class PortMonitoringService : BackgroundService
{
    private readonly IAdminNotificationService _notify;
    private readonly IOptionsMonitor<PortMonitoringOptions> _monOpts;
    private readonly IOptionsMonitor<List<SmtpServerEntry>> _serversOpts;
    private readonly PortProbeRegistry _probeRegistry;
    private readonly ILogger<PortMonitoringService> _logger;

    /// <summary>
    /// When the current outage per port started — drives the "unreachable for long enough to be
    /// worth reporting" threshold and the log message. Not notification state: how often the alert
    /// is repeated is owned by <see cref="IAlertStateStore"/>.
    /// </summary>
    private readonly Dictionary<int, DateTime> _outageSince = [];

    /// <summary>
    /// Ports whose outage has already been logged at Error. Purely log verbosity — without it a
    /// long outage would write one Error per check into error-*.log. The notification cadence is
    /// unrelated and lives in <see cref="IAlertStateStore"/>.
    /// </summary>
    private readonly HashSet<int> _outageLogged = [];

    public PortMonitoringService(
        IAdminNotificationService notify,
        IOptionsMonitor<PortMonitoringOptions> monOpts,
        IOptionsMonitor<List<SmtpServerEntry>> serversOpts,
        PortProbeRegistry probeRegistry,
        ILogger<PortMonitoringService> logger)
    {
        _notify = notify;
        _monOpts = monOpts;
        _serversOpts = serversOpts;
        _probeRegistry = probeRegistry;
        _logger = logger;
    }

    /// <summary>Guards against a hand-edited 0/negative interval turning into an invalid timer period.</summary>
    internal static TimeSpan Interval(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _monOpts.CurrentValue;
        _logger.LogInformation("[PortMonitor] Started (enabled: {Enabled}, interval: {Min}min, outage threshold: {Threshold}min)",
            opts.Enabled, opts.CheckIntervalMinutes, opts.OutageAlertThresholdMinutes);

        // The loop runs even while disabled so switching the monitor on takes effect without a
        // service restart — same for the interval, which is re-read on every tick.
        using var timer = new PeriodicTimer(Interval(opts.CheckIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var current = _monOpts.CurrentValue;
                timer.Period = Interval(current.CheckIntervalMinutes);
                if (!current.Enabled) continue;

                await CheckAllPortsAsync(current, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("[PortMonitor] Stopped");
    }

    private async Task CheckAllPortsAsync(PortMonitoringOptions opts, CancellationToken ct)
    {
        var servers = _serversOpts.CurrentValue;
        if (servers is null || servers.Count == 0) return;

        foreach (var server in servers)
        {
            await CheckPortAsync(server.Port, opts, ct);
        }
    }

    // internal so unit tests can drive single checks without the timer
    internal async Task CheckPortAsync(int port, PortMonitoringOptions opts, CancellationToken ct)
    {
        // Announce the probe so SmtpRelayService logs the resulting loopback
        // connection at Debug instead of treating it like a real client.
        _probeRegistry.MarkProbe(port);
        var reachable = await IsTcpPortOpenAsync(port, ct);

        if (reachable)
        {
            if (_outageSince.Remove(port, out var since))
                _logger.LogInformation("[PortMonitor] Port {Port} restored after {Min:F0} min",
                    port, (DateTime.UtcNow - since).TotalMinutes);
            else
                _logger.LogDebug("[PortMonitor] Port {Port} reachable", port);

            _outageLogged.Remove(port);

            // Reported on every healthy check, not only on the transition: the alert store knows
            // whether anything was raised, and this way an outage that predates a service restart
            // still gets its recovery mail.
            await _notify.NotifyPortRestoredAsync(port, ct);
        }
        else
        {
            if (!_outageSince.ContainsKey(port))
            {
                _outageSince[port] = DateTime.UtcNow;
                _logger.LogWarning("[PortMonitor] Port {Port} unreachable – outage started", port);
            }

            var outageDuration = DateTime.UtcNow - _outageSince[port];
            _logger.LogDebug("[PortMonitor] Port {Port} still down – {Min:F0} min", port, outageDuration.TotalMinutes);

            // Below the threshold the port is down but not yet alert-worthy: neither raise nor
            // clear, or a brief blip would resolve an alert that was never sent.
            if (outageDuration.TotalMinutes >= opts.OutageAlertThresholdMinutes)
            {
                if (_outageLogged.Add(port))
                    _logger.LogError("[PortMonitor] Port {Port} outage – unreachable for {Min:F0} min", port, outageDuration.TotalMinutes);

                await _notify.NotifyPortOutageAsync(port, $"Port unreachable for {outageDuration.TotalMinutes:F0} min", ct);
            }
        }
    }

    private static async Task<bool> IsTcpPortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
