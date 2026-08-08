using System.Text.Json;
using System.Text.Json.Serialization;
using GraphMailer.Service.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GraphMailer.Service.Services;

/// <summary>Alert keys used by the state-based monitors. One key per condition, not per message.</summary>
internal static class AlertKeys
{
    internal const string DiskSpace = "disk-low";

    /// <summary>TLS listener certificate. Expiring and expired share this key so an escalation from
    /// one to the other is reported immediately and a renewal clears whichever was active.</summary>
    internal const string TlsCertificate = "cert-tls";

    internal const string GraphCertificate = "cert-graph";
    internal const string GraphApi = "graph-api";
    internal const string GraphPermissions = "graph-permissions";

    internal static string Port(int port) => $"port-{port}";
}

/// <summary>
/// Remembers which state-based alert conditions are currently active and when each was last
/// reported, so every monitor follows the same notification cadence instead of implementing its
/// own (some used to mail on every single check, others exactly once per process lifetime).
///
/// The state is persisted under <c>data\alert-state.json</c>: a service restart during an ongoing
/// outage must not re-alert, and an outage that started before the restart must still produce its
/// recovery mail once it clears.
/// </summary>
internal interface IAlertStateStore
{
    /// <summary>
    /// Records that <paramref name="key"/> is currently in the problem state and returns whether a
    /// notification is due: on first raise, whenever <paramref name="detail"/> changes (an escalation
    /// or a different cause is worth reporting straight away), or once <paramref name="renotifyMinutes"/>
    /// have passed since the last report. <c>0</c> disables repetition entirely.
    /// </summary>
    bool ShouldNotify(string key, string detail, int renotifyMinutes);

    /// <summary>
    /// Clears <paramref name="key"/> and returns whether it had been reported before — i.e. whether
    /// a recovery notification is warranted. Safe (and expected) to call on every healthy check.
    /// </summary>
    bool Clear(string key);
}

internal sealed class AlertStateStore : IAlertStateStore
{
    internal sealed class AlertEntry
    {
        public string Detail { get; set; } = string.Empty;
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastNotifiedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger<AlertStateStore> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, AlertEntry> _entries;

    public AlertStateStore(ILogger<AlertStateStore> logger)
        : this(Path.Combine(AppPaths.DataDir, "alert-state.json"), logger)
    {
    }

    // internal so tests can point the store at a temp file (AppPaths is fixed per process) and
    // drive the repeat interval without waiting it out
    internal AlertStateStore(string path, ILogger<AlertStateStore> logger, TimeProvider? timeProvider = null)
    {
        _path = path;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _entries = Load();
    }

    public bool ShouldNotify(string key, string detail, int renotifyMinutes)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        lock (_gate)
        {
            var known = _entries.TryGetValue(key, out var existing);

            if (known && existing!.Detail == detail)
            {
                // Same condition, unchanged: repeat only after the configured gap.
                if (renotifyMinutes <= 0) return false;
                if (now - existing.LastNotifiedUtc < TimeSpan.FromMinutes(renotifyMinutes)) return false;
            }

            _entries[key] = new AlertEntry
            {
                Detail = detail,
                FirstSeenUtc = known ? existing!.FirstSeenUtc : now,
                LastNotifiedUtc = now,
            };
            Save();
            return true;
        }
    }

    public bool Clear(string key)
    {
        lock (_gate)
        {
            if (!_entries.Remove(key)) return false;
            Save();
            return true;
        }
    }

    private Dictionary<string, AlertEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, AlertEntry>>(json, SerializerOptions) ?? [];
        }
        catch (Exception ex)
        {
            // A lost state file only costs one redundant notification — never worth failing a check over.
            _logger.LogDebug(ex, "[AlertState] Could not read {Path} – starting with an empty alert state", _path);
            return [];
        }
    }

    /// <summary>Called under <see cref="_gate"/>.</summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, SerializerOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AlertState] Could not persist alert state to {Path}", _path);
        }
    }
}
