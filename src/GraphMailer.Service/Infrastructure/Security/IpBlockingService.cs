using System.Collections.Concurrent;
using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Infrastructure.Security;

/// <summary>
/// Thread-safe, in-memory IP blocking service.
///
/// Tracks authentication failures and blocked-sender attempts per source IP.
/// Once the failure threshold is reached within the configured time window,
/// the IP is blocked for the configured duration.
///
/// All timestamps are UTC. Expired blocks are removed lazily on each check.
/// </summary>
internal sealed class IpBlockingService : IDisposable
{
    private readonly record struct FailureRecord(DateTime Timestamp, string Type);
    private readonly record struct BlockEntry(DateTime BlockedAt, DateTime ExpiresAt);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    // IP → ordered list of recent failures (guarded by lock on the list itself)
    private readonly ConcurrentDictionary<string, List<FailureRecord>> _failures = new();

    // IP → current block expiry
    private readonly ConcurrentDictionary<string, BlockEntry> _blockedIps = new();

    private readonly IOptionsMonitor<IpBlockingProtectionOptions> _options;
    private readonly ILogger<IpBlockingService> _logger;
    private readonly TimeProvider _clock;
    private readonly Timer _sweepTimer;

    public IpBlockingService(
        IOptionsMonitor<IpBlockingProtectionOptions> options,
        ILogger<IpBlockingService> logger,
        TimeProvider? clock = null)
    {
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _sweepTimer = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public void Dispose() => _sweepTimer.Dispose();

    /// <summary>Number of IPs currently holding a failure history — sweep-test observability.</summary>
    internal int TrackedFailureIpCount => _failures.Count;

    /// <summary>
    /// Periodic global cleanup: removes expired blocks and failure histories whose
    /// entries all fell out of the tracking window. The lazy per-IP cleanup only runs
    /// when the SAME IP causes another event — without this sweep, one-off source
    /// addresses (trivially rotated over IPv6) would keep a dictionary entry forever.
    /// </summary>
    internal void Sweep()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddSeconds(-_options.CurrentValue.TimeframeSeconds);

        foreach (var (ip, entry) in _blockedIps)
            if (entry.ExpiresAt <= now)
                _blockedIps.TryRemove(ip, out _);

        foreach (var (ip, list) in _failures)
        {
            lock (list)
            {
                list.RemoveAll(f => f.Timestamp < cutoff);
                // Worst case a concurrent RecordFailure loses one entry to the removal
                // race and re-creates the list — one undercounted failure is harmless.
                if (list.Count == 0)
                    _failures.TryRemove(ip, out _);
            }
        }
    }

    /// <summary>
    /// Returns true if the IP is currently blocked. Expired blocks are
    /// removed on this call (lazy cleanup).
    /// </summary>
    public bool IsBlocked(string ip) => IsBlocked(ip, out _);

    /// <summary>
    /// Same as <see cref="IsBlocked(string)"/> but also reports when the
    /// block expires (UTC) — used for log messages at rejection sites.
    /// </summary>
    public bool IsBlocked(string ip, out DateTime expiresAtUtc)
    {
        expiresAtUtc = default;

        if (!_options.CurrentValue.Enabled)
            return false;

        if (_blockedIps.TryGetValue(ip, out var entry))
        {
            if (_clock.GetUtcNow().UtcDateTime < entry.ExpiresAt)
            {
                expiresAtUtc = entry.ExpiresAt;
                return true;
            }

            _blockedIps.TryRemove(ip, out _);
        }

        return false;
    }

    /// <summary>
    /// Records a failure for the given IP. If the failure threshold within
    /// the time window is exceeded the IP is blocked immediately.
    /// </summary>
    /// <param name="ip">Remote IP address.</param>
    /// <param name="failureType">Human-readable type tag, e.g. "authFailure".</param>
    public void RecordFailure(string ip, string failureType)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
            return;

        var now = _clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddSeconds(-opts.TimeframeSeconds);

        var failures = _failures.GetOrAdd(ip, _ => []);
        int countAfterAdd;

        lock (failures)
        {
            failures.RemoveAll(f => f.Timestamp < cutoff);
            failures.Add(new FailureRecord(now, failureType));
            countAfterAdd = failures.Count;
        }

        if (countAfterAdd >= opts.FailureThreshold)
        {
            var expiresAt = now.AddSeconds(opts.BlockDurationSeconds);
            _blockedIps.AddOrUpdate(
                ip,
                new BlockEntry(now, expiresAt),
                (_, existing) => existing.ExpiresAt < expiresAt ? new BlockEntry(now, expiresAt) : existing);

            _logger.LogWarning(
                "[IpBlocking] Blocked {Ip} until {Expires:HH:mm:ss} UTC " +
                "after {Count} {Type} failures in {Timeframe}s window",
                ip, expiresAt, countAfterAdd, failureType, opts.TimeframeSeconds);
        }
    }

    /// <summary>Returns a snapshot of currently blocked IPs with their expiry times.</summary>
    public IReadOnlyDictionary<string, DateTime> GetBlockedIps()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return _blockedIps
            .Where(kv => kv.Value.ExpiresAt > now)
            .ToDictionary(kv => kv.Key, kv => kv.Value.ExpiresAt);
    }
}
