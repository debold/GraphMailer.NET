using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services.Reporting;

/// <summary>
/// Assembles a <see cref="ReportData"/> snapshot for the periodic operations report from
/// the SQLite metrics database (read-only), the mail queue folders and live health checks.
/// Every section degrades gracefully: a failure in one area yields empty/"Unknown" values
/// rather than aborting the whole report.
/// </summary>
internal sealed class ReportDataCollector
{
    private readonly IOptionsMonitor<MailQueueOptions> _mailQueue;
    private readonly IOptionsMonitor<MetricsOptions> _metrics;
    private readonly IOptionsMonitor<CertificateOptions> _cert;
    private readonly IOptionsMonitor<CertificateMonitoringOptions> _certMon;
    private readonly IOptionsMonitor<DiskSpaceMonitoringOptions> _diskMon;
    private readonly IOptionsMonitor<List<SmtpServerEntry>> _servers;
    private readonly IDataProtector _configProtector;
    private readonly ILogger<ReportDataCollector> _logger;

    public ReportDataCollector(
        IOptionsMonitor<MailQueueOptions> mailQueue,
        IOptionsMonitor<MetricsOptions> metrics,
        IOptionsMonitor<CertificateOptions> cert,
        IOptionsMonitor<CertificateMonitoringOptions> certMon,
        IOptionsMonitor<DiskSpaceMonitoringOptions> diskMon,
        IOptionsMonitor<List<SmtpServerEntry>> servers,
        IDataProtectionProvider dpProvider,
        ILogger<ReportDataCollector> logger)
    {
        _mailQueue = mailQueue;
        _metrics = metrics;
        _cert = cert;
        _certMon = certMon;
        _diskMon = diskMon;
        _servers = servers;
        _configProtector = dpProvider.CreateProtector(DataProtectionExtensions.ConfigPurpose);
        _logger = logger;
    }

    // Mirror MetricsService: empty BasePath → AppPaths.BaseDir, else the override.
    private string DbPath
    {
        get
        {
            var baseDir = string.IsNullOrEmpty(_metrics.CurrentValue.BasePath)
                ? AppPaths.BaseDir
                : _metrics.CurrentValue.BasePath;
            return Path.Combine(baseDir, "data", "metrics.db");
        }
    }

    private string MailDir => string.IsNullOrEmpty(_mailQueue.CurrentValue.MailDir)
        ? AppPaths.MailDir
        : _mailQueue.CurrentValue.MailDir;

    /// <summary>Builds the full report snapshot for the period ending at <paramref name="now"/>.</summary>
    public ReportData Collect(ScheduledReportOptions opts, DateTimeOffset now)
    {
        var monthly = opts.Frequency == ReportFrequency.Monthly;
        var span = TimeSpan.FromDays(monthly ? 30 : 7);
        var end = now;
        var start = end - span;
        var prevStart = start - span;

        var data = new ReportData
        {
            Host = Environment.MachineName,
            Version = BuildInfo.FileVersion,
            GeneratedAt = now,
            PeriodStart = DateOnly.FromDateTime(start.UtcDateTime),
            PeriodEnd = DateOnly.FromDateTime(end.UtcDateTime),
            Title = monthly ? "Monthly Operations Report" : "Weekly Operations Report",
            PeriodLabel = monthly ? "last 30 days" : "last 7 days",
            Uptime = GetUptime(),
            DiskFreePct = TryDiskFreePercent(),
        };

        var (queuedNow, failedCount, failedItems) = ReadQueueFolders();
        data = data with
        {
            QueuedNow = queuedNow,
            FailedQueueCount = failedCount,
            FailedQueueItems = failedItems,
            Health = BuildHealth(queuedNow, failedCount, now),
        };

        if (!File.Exists(DbPath))
            return data;

        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            conn.Open();

            var startIso = start.UtcDateTime.ToString("O");
            var endIso = end.UtcDateTime.ToString("O");
            var prevStartIso = prevStart.UtcDateTime.ToString("O");

            var delivered = CountEvents(conn, "sent", startIso, endIso);
            var failed = CountEvents(conn, "failed", startIso, endIso);
            var total = delivered + failed;

            data = data with
            {
                Delivered = delivered,
                Failed = failed,
                PrevDelivered = CountEvents(conn, "sent", prevStartIso, startIso),
                PrevFailed = CountEvents(conn, "failed", prevStartIso, startIso),
                SuccessRatePercent = total > 0 ? delivered * 100.0 / total : null,
                AvgDeliveryMs = QueryDouble(conn,
                    "SELECT AVG(duration_ms) FROM email_events WHERE event_type='sent' AND duration_ms>0 AND occurred_at>=$s AND occurred_at<$e", startIso, endIso),
                PeakDeliveryMs = QueryDouble(conn,
                    "SELECT MAX(duration_ms) FROM email_events WHERE event_type='sent' AND occurred_at>=$s AND occurred_at<$e", startIso, endIso),
                DistinctSenders = (int)QueryLong(conn,
                    "SELECT COUNT(DISTINCT from_addr) FROM email_events WHERE event_type='sent' AND from_addr<>'' AND occurred_at>=$s AND occurred_at<$e", startIso, endIso),
                VolumeBytes = QueryLong(conn,
                    "SELECT COALESCE(SUM(size_bytes),0) FROM email_events WHERE event_type='sent' AND occurred_at>=$s AND occurred_at<$e", startIso, endIso),
                Daily = ReadDaily(conn, start, end),
                TopSenders = ReadTopSenders(conn, startIso, endIso),
                TopHosts = ReadTopHosts(conn, startIso, endIso),
                MemAvgMb = QueryDouble(conn, "SELECT AVG(value) FROM perf_metrics WHERE metric_type='memory_mb' AND recorded_at>=$s AND recorded_at<$e", startIso, endIso),
                MemPeakMb = QueryDouble(conn, "SELECT MAX(value) FROM perf_metrics WHERE metric_type='memory_mb' AND recorded_at>=$s AND recorded_at<$e", startIso, endIso),
                CpuAvgPct = QueryDouble(conn, "SELECT AVG(value) FROM perf_metrics WHERE metric_type='cpu_percent' AND recorded_at>=$s AND recorded_at<$e", startIso, endIso),
                CpuPeakPct = QueryDouble(conn, "SELECT MAX(value) FROM perf_metrics WHERE metric_type='cpu_percent' AND recorded_at>=$s AND recorded_at<$e", startIso, endIso),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Report] Could not read metrics database — statistics omitted");
        }

        return data;
    }

    // ── Metrics queries ──────────────────────────────────────────────────────

    private static long CountEvents(SqliteConnection conn, string type, string startIso, string endIso)
        => QueryLong(conn,
            "SELECT COUNT(*) FROM email_events WHERE event_type=$t AND occurred_at>=$s AND occurred_at<$e",
            startIso, endIso, type);

    private static List<DailyPoint> ReadDaily(SqliteConnection conn, DateTimeOffset start, DateTimeOffset end)
    {
        // One bucket per calendar day (UTC) across the period; missing days stay at zero.
        var sent = ReadDayCounts(conn, "sent", start, end);
        var failed = ReadDayCounts(conn, "failed", start, end);

        var points = new List<DailyPoint>();
        for (var day = DateOnly.FromDateTime(start.UtcDateTime); day <= DateOnly.FromDateTime(end.UtcDateTime); day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd");
            points.Add(new DailyPoint(day, sent.GetValueOrDefault(key), failed.GetValueOrDefault(key)));
        }
        return points;
    }

    private static Dictionary<string, long> ReadDayCounts(SqliteConnection conn, string type, DateTimeOffset start, DateTimeOffset end)
    {
        var result = new Dictionary<string, long>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT substr(occurred_at,1,10) AS day, COUNT(*) AS n
            FROM email_events
            WHERE event_type=$t AND occurred_at>=$s AND occurred_at<$e
            GROUP BY day
            """;
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$s", start.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$e", end.UtcDateTime.ToString("O"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    private static List<NamedCount> ReadTopSenders(SqliteConnection conn, string startIso, string endIso)
        => ReadTopN(conn, """
            SELECT from_addr, COUNT(*) AS n FROM email_events
            WHERE event_type='sent' AND from_addr<>'' AND occurred_at>=$s AND occurred_at<$e
            GROUP BY from_addr ORDER BY n DESC LIMIT 5
            """, startIso, endIso);

    private List<NamedCount> ReadTopHosts(SqliteConnection conn, string startIso, string endIso)
    {
        // Tolerate an older metrics DB that predates the client_ip column: degrade to an
        // empty top-hosts list instead of failing the whole statistics block.
        try
        {
            return ReadTopN(conn, """
                SELECT client_ip, COUNT(*) AS n FROM email_events
                WHERE event_type='received' AND client_ip IS NOT NULL AND client_ip<>'' AND occurred_at>=$s AND occurred_at<$e
                GROUP BY client_ip ORDER BY n DESC LIMIT 5
                """, startIso, endIso);
        }
        catch (SqliteException ex)
        {
            _logger.LogDebug(ex, "[Report] Top-hosts query skipped (client_ip column unavailable)");
            return [];
        }
    }

    private static List<NamedCount> ReadTopN(SqliteConnection conn, string sql, string startIso, string endIso)
    {
        var list = new List<NamedCount>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", startIso);
        cmd.Parameters.AddWithValue("$e", endIso);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new NamedCount(reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    private static long QueryLong(SqliteConnection conn, string sql, string startIso, string endIso, string? type = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", startIso);
        cmd.Parameters.AddWithValue("$e", endIso);
        if (type is not null) cmd.Parameters.AddWithValue("$t", type);
        var r = cmd.ExecuteScalar();
        return r is long l ? l : 0;
    }

    private static double? QueryDouble(SqliteConnection conn, string sql, string startIso, string endIso)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", startIso);
        cmd.Parameters.AddWithValue("$e", endIso);
        var r = cmd.ExecuteScalar();
        // SQLite keeps the column affinity for MAX/MIN: an INT column yields a long,
        // while AVG always yields a double — accept both.
        return r switch
        {
            double d => d,
            long l => l,
            _ => null,
        };
    }

    private long? LastSentAgoMinutes(SqliteConnection conn, DateTimeOffset now)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(occurred_at) FROM email_events WHERE event_type='sent'";
        var r = cmd.ExecuteScalar();
        if (r is not string s || !DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last))
            return null;
        return (long)Math.Max(0, (now.UtcDateTime - last).TotalMinutes);
    }

    // ── Mail queue folders ───────────────────────────────────────────────────

    private (int Queued, int Failed, List<FailedQueueItem> Items) ReadQueueFolders()
    {
        var queueDir = Path.Combine(MailDir, "queue");
        var failedDir = Path.Combine(MailDir, "failed");

        int queued = CountMeta(queueDir);
        var items = new List<FailedQueueItem>();

        if (Directory.Exists(failedDir))
        {
            foreach (var path in Directory.EnumerateFiles(failedDir, "*.meta.json"))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<MailMetadata>(File.ReadAllText(path));
                    if (meta is null) continue;
                    items.Add(new FailedQueueItem(
                        FailedAt: (meta.LastAttemptAt ?? meta.ReceivedAt),
                        From: meta.From,
                        To: meta.To.Count > 0 ? string.Join(", ", meta.To) : "(unknown)",
                        Subject: string.IsNullOrEmpty(meta.Subject) ? "(no subject)" : meta.Subject,
                        LastError: meta.LastError ?? "(no error recorded)",
                        RetryCount: meta.RetryCount));
                }
                catch
                {
                    // corrupt or mid-write — skip
                }
            }
        }

        var failedCount = items.Count;
        var top = items.OrderByDescending(i => i.FailedAt).Take(10).ToList();
        return (queued, failedCount, top);
    }

    private static int CountMeta(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.meta.json").Length : 0; }
        catch { return 0; }
    }

    // ── Health checks ────────────────────────────────────────────────────────

    private List<HealthItem> BuildHealth(int queuedNow, int failedCount, DateTimeOffset now)
    {
        return
        [
            new HealthItem("SMTP Service", HealthStatus.Ok, "Running"),
            CheckSecrets(),
            CheckCertificate(),
            CheckPorts(),
            CheckDisk(),
            CheckQueue(queuedNow, failedCount),
            CheckGraphApi(now),
        ];
    }

    private HealthItem CheckSecrets()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFilePath))
                return new HealthItem("Config Secrets", HealthStatus.Unknown, "No configuration file");

            var scan = SecretIntegrityChecker.Scan(File.ReadAllText(AppPaths.ConfigFilePath), _configProtector);
            if (scan.TotalEncrypted == 0)
                return new HealthItem("Config Secrets", HealthStatus.Unknown, "No encrypted secrets configured");
            if (scan.Undecryptable.Count == 0)
                return new HealthItem("Config Secrets", HealthStatus.Ok, $"{scan.TotalEncrypted} encrypted secret(s) decryptable");
            return new HealthItem("Config Secrets", HealthStatus.Error, "Cannot decrypt: " + string.Join(", ", scan.Undecryptable));
        }
        catch (Exception ex)
        {
            return new HealthItem("Config Secrets", HealthStatus.Unknown, ex.Message);
        }
    }

    private HealthItem CheckCertificate()
    {
        try
        {
            var subject = _cert.CurrentValue.SubjectName;
            if (string.IsNullOrEmpty(subject))
                return new HealthItem("TLS Certificate", HealthStatus.Unknown, "SubjectName not configured");

            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var cert = store.Certificates
                .Cast<X509Certificate2>()
                .Where(c => c.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase)
                         || c.GetNameInfo(X509NameType.DnsName, false).Contains(subject, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();

            if (cert is null)
                return new HealthItem("TLS Certificate", HealthStatus.Error, $"No cert found for '{subject}'");

            var daysLeft = (int)(cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
            var detail = $"{subject} — expires {cert.NotAfter:yyyy-MM-dd} ({daysLeft}d)";
            return daysLeft < 0 ? new HealthItem("TLS Certificate", HealthStatus.Error, "EXPIRED: " + detail)
                 : daysLeft < _certMon.CurrentValue.WarningThresholdDays ? new HealthItem("TLS Certificate", HealthStatus.Warning, detail)
                 : new HealthItem("TLS Certificate", HealthStatus.Ok, detail);
        }
        catch (Exception ex)
        {
            return new HealthItem("TLS Certificate", HealthStatus.Unknown, ex.Message);
        }
    }

    private HealthItem CheckPorts()
    {
        try
        {
            var servers = _servers.CurrentValue;
            if (servers.Count == 0)
                return new HealthItem("SMTP Ports", HealthStatus.Unknown, "No SMTP servers configured");

            var listening = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(e => e.Port)
                .ToHashSet();

            var results = new List<string>();
            var anyDown = false;
            foreach (var s in servers)
            {
                if (listening.Contains(s.Port)) results.Add($"{s.Name}:{s.Port} OK");
                else { results.Add($"{s.Name}:{s.Port} not listening"); anyDown = true; }
            }

            return new HealthItem("SMTP Ports", anyDown ? HealthStatus.Error : HealthStatus.Ok, string.Join(", ", results));
        }
        catch (Exception ex)
        {
            return new HealthItem("SMTP Ports", HealthStatus.Unknown, ex.Message);
        }
    }

    private HealthItem CheckDisk()
    {
        try
        {
            var dir = Directory.Exists(MailDir) ? MailDir : AppPaths.BaseDir;
            var root = Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return new HealthItem("Disk Space", HealthStatus.Warning, $"Drive {root} not ready");

            var freePct = drive.AvailableFreeSpace * 100.0 / drive.TotalSize;
            var freeGb = drive.AvailableFreeSpace / 1_073_741_824.0;
            var detail = $"{freeGb:F1} GB free ({freePct:F1}%) on {root}";
            var threshold = _diskMon.CurrentValue.ThresholdPercent;
            return freePct < threshold / 2.0 ? new HealthItem("Disk Space", HealthStatus.Error, detail)
                 : freePct < threshold ? new HealthItem("Disk Space", HealthStatus.Warning, detail)
                 : new HealthItem("Disk Space", HealthStatus.Ok, detail);
        }
        catch (Exception ex)
        {
            return new HealthItem("Disk Space", HealthStatus.Unknown, ex.Message);
        }
    }

    private static HealthItem CheckQueue(int queued, int failed)
    {
        var detail = $"{queued} queued, {failed} failed";
        return failed > 0 ? new HealthItem("Mail Queue", HealthStatus.Warning, detail)
                          : new HealthItem("Mail Queue", HealthStatus.Ok, detail);
    }

    private HealthItem CheckGraphApi(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(DbPath))
                return new HealthItem("Graph API", HealthStatus.Unknown, "No delivery data");

            using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            conn.Open();
            var ago = LastSentAgoMinutes(conn, now);
            if (ago is null)
                return new HealthItem("Graph API", HealthStatus.Unknown, "No deliveries recorded");

            var agoStr = ago >= 1440 ? $"{ago / 1440}d ago" : ago >= 60 ? $"{ago / 60}h ago" : $"{ago}m ago";
            return new HealthItem("Graph API", HealthStatus.Ok, $"Last delivery: {agoStr}");
        }
        catch (Exception ex)
        {
            return new HealthItem("Graph API", HealthStatus.Unknown, ex.Message);
        }
    }

    // ── System / performance ─────────────────────────────────────────────────

    private static string GetUptime()
    {
        try
        {
            var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
            return elapsed.TotalDays >= 1 ? $"{(int)elapsed.TotalDays}d {elapsed.Hours}h"
                 : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                 : $"{elapsed.Minutes}m";
        }
        catch { return "—"; }
    }

    private double? TryDiskFreePercent()
    {
        try
        {
            var dir = Directory.Exists(MailDir) ? MailDir : AppPaths.BaseDir;
            var root = Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace * 100.0 / drive.TotalSize : null;
        }
        catch { return null; }
    }
}
