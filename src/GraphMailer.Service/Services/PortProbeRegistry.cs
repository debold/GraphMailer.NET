using System.Collections.Concurrent;
using System.Net;

namespace GraphMailer.Service.Services;

/// <summary>
/// Lets the in-process port health checks (PortMonitoringService) announce their
/// TCP probes so the SMTP session logging can tell them apart from real clients
/// and log them at Debug instead of Information/Warning.
///
/// Trade-off: a real loopback client connecting within the probe window of the
/// same port is also demoted to Debug — acceptable, since loopback traffic is
/// dev/test only and the window is short.
/// </summary>
internal sealed class PortProbeRegistry
{
    internal static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<int, DateTime> _lastProbeUtc = new();
    private readonly TimeProvider _clock;

    public PortProbeRegistry(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Called by the port monitor immediately before it opens a probe connection.</summary>
    public void MarkProbe(int port)
        => _lastProbeUtc[port] = _clock.GetUtcNow().UtcDateTime;

    /// <summary>True when the connection is most likely a recent local health probe.</summary>
    public bool IsProbeConnection(int port, string? remoteIp)
        => IsLoopback(remoteIp)
           && _lastProbeUtc.TryGetValue(port, out var probedAt)
           && _clock.GetUtcNow().UtcDateTime - probedAt < ProbeWindow;

    private static bool IsLoopback(string? ip)
        => ip is not null && IPAddress.TryParse(ip, out var address) && IPAddress.IsLoopback(address);
}
