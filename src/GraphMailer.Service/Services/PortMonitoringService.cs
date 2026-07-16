using System.Net.Sockets;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// BackgroundService that periodically performs TCP health checks on configured SMTP ports.
/// Sends admin notifications when a port becomes unreachable for longer than
/// <see cref="PortMonitoringOptions.OutageAlertThresholdMinutes"/>, and again when it recovers.
/// </summary>
internal sealed class PortMonitoringService : BackgroundService
{
    private readonly IAdminNotificationService _notify;
    private readonly IOptionsMonitor<PortMonitoringOptions> _monOpts;
    private readonly IOptionsMonitor<List<SmtpServerEntry>> _serversOpts;
    private readonly PortProbeRegistry _probeRegistry;
    private readonly ILogger<PortMonitoringService> _logger;

    // Per-port outage tracking
    private readonly Dictionary<int, DateTime> _outageSince = [];
    private readonly Dictionary<int, bool> _alerted = [];

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _monOpts.CurrentValue;
        if (!opts.Enabled)
        {
            _logger.LogDebug("[PortMonitor] Port monitoring disabled");
            return;
        }

        _logger.LogInformation("[PortMonitor] Started (interval: {Min}min)", opts.CheckIntervalMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(opts.CheckIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckAllPortsAsync(_monOpts.CurrentValue, stoppingToken);
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

    private async Task CheckPortAsync(int port, PortMonitoringOptions opts, CancellationToken ct)
    {
        // Announce the probe so SmtpRelayService logs the resulting loopback
        // connection at Debug instead of treating it like a real client.
        _probeRegistry.MarkProbe(port);
        var reachable = await IsTcpPortOpenAsync(port, ct);

        if (reachable)
        {
            if (_outageSince.ContainsKey(port))
            {
                var duration = DateTime.UtcNow - _outageSince[port];
                _logger.LogInformation("[PortMonitor] Port {Port} restored after {Min:F0} min", port, duration.TotalMinutes);
                _outageSince.Remove(port);
                _alerted.Remove(port);
                await _notify.NotifyPortRestoredAsync(port, ct);
            }
            else
            {
                _logger.LogDebug("[PortMonitor] Port {Port} reachable", port);
            }
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

            if (!_alerted.GetValueOrDefault(port) && outageDuration.TotalMinutes >= opts.OutageAlertThresholdMinutes)
            {
                _alerted[port] = true;
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
