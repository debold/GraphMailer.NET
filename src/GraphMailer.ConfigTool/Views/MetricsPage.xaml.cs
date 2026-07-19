using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure;
using WpfShapes = System.Windows.Shapes;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Statistics page: five tabs (Overview, Reception, Delivery, End-to-End, Server)
/// over the read-only metrics.db (schema v2). All queries are best-effort — a
/// missing database, table or column degrades the affected section to "—".
/// </summary>
public partial class MetricsPage : UserControl
{
    private readonly DispatcherTimer _timer;

    private List<double> _memSamples = [];
    private List<double> _cpuSamples = [];
    private List<double> _diskSamples = [];
    private List<DailyBar> _dailyBars = [];
    private int _rangeDays = 7;

    private static string DbPath => Path.Combine(AppPaths.DataDir, "metrics.db");

    private static readonly Color DeliveredColor = Color.FromRgb(0x00, 0x78, 0xD4);   // accent
    private static readonly Color FailedColor = Color.FromRgb(0xC4, 0x2B, 0x1C);      // danger
    private static readonly Color AbortColor = Color.FromRgb(0x9D, 0x5D, 0x00);       // warn

    public MetricsPage()
    {
        InitializeComponent();
        // No LoadData() here: IsVisibleChanged below loads the data when the
        // page is first shown.

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => LoadData();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { LoadData(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadData();

    private void Range_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out var days)) return;
        _rangeDays = days;

        foreach (var b in new[] { Range24h, Range7d, Range30d, Range90d })
            b.Style = (Style)FindResource("SmallButton");
        btn.Style = (Style)FindResource("SmallPrimaryButton");

        LoadData();
    }

    private void PerfCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPerfCharts();
    private void DailyCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDailyChart();

    // ═════════════════════════════════════════════════════════════════════════
    // Data loading
    // ═════════════════════════════════════════════════════════════════════════

    private void LoadData()
    {
        if (!File.Exists(DbPath))
        {
            ClearAll();
            return;
        }

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_rangeDays).ToString("O");
            var cutoffBucket = DateTime.UtcNow.AddDays(-_rangeDays).ToString("yyyy-MM-dd'T'HH");

            using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            conn.Open();

            LoadOverview(conn, cutoff);
            TrySection(() => LoadReception(conn, cutoff, cutoffBucket));
            TrySection(() => LoadDelivery(conn, cutoff));
            TrySection(() => LoadEndToEnd(conn, cutoff));
            LoadServer(conn);
            LoadActivity(conn);
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("MetricsPage", ex, "Could not read metrics database");
            NoDataText.Text = $"Could not read metrics database: {ex.Message}";
            NoDataText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>A section touching v2 columns must not take down the whole page on an old DB.</summary>
    private static void TrySection(Action load)
    {
        try { load(); }
        catch (SqliteException) { /* pre-v2 database — service migrates it on next start */ }
    }

    private void ClearAll()
    {
        foreach (var t in new[]
        {
            OvReceived, OvDelivered, OvFailed, OvSuccessRate, OvVolume, OvAvgDelivery, OvSenders, OvQueuedNow,
            RcSessions, RcAborted, RcRejected, RcTlsShare, RcAuthShare,
            DlDelivered, DlAvgTime, DlFirstTry, DlRetriesPerMail,
            E2AvgLatency, E2P95Latency, E2MaxLatency, E2FirstTry,
            PerfMemory, PerfCpu, PerfDisk, SrvDbSize,
        })
            t.Text = "—";

        _memSamples = [];
        _cpuSamples = [];
        _diskSamples = [];
        _dailyBars = [];
        ActivityGrid.ItemsSource = Array.Empty<ActivityRow>();
        RcAbortStages.ItemsSource = null;
        RcRejectionsGrid.ItemsSource = null;
        RcTopHostsGrid.ItemsSource = null;
        RcListenersGrid.ItemsSource = null;
        DlRetryHistogram.ItemsSource = null;
        DlVariants.ItemsSource = null;
        DlErrorsGrid.ItemsSource = null;
        E2Funnel.ItemsSource = null;
        E2Failures.ItemsSource = null;
        NoDataText.Visibility = Visibility.Visible;
        NoPerfText.Visibility = Visibility.Visible;
        DrawPerfCharts();
        DrawDailyChart();
    }

    // ── Overview ─────────────────────────────────────────────────────────────

    private void LoadOverview(SqliteConnection conn, string cutoff)
    {
        var received = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='received' AND occurred_at >= $c", cutoff);
        var sent = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff);
        var failed = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='failed' AND occurred_at >= $c", cutoff);

        OvReceived.Text = Num(received);
        OvDelivered.Text = Num(sent);
        OvFailed.Text = Num(failed);
        OvSuccessRate.Text = sent + failed > 0 ? $"{sent * 100.0 / (sent + failed):F1} %" : "—";

        var volume = QueryLong(conn, "SELECT COALESCE(SUM(size_bytes),0) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff);
        OvVolume.Text = volume > 0 ? FormatBytes(volume) : "—";
        OvVolumeSub.Text = sent > 0 ? $"Ø {FormatBytes(volume / sent)} per mail" : "Ø per mail";

        var avgMs = QueryDouble(conn, "SELECT AVG(duration_ms) FROM email_events WHERE event_type='sent' AND duration_ms > 0 AND occurred_at >= $c", cutoff);
        var maxMs = QueryDouble(conn, "SELECT MAX(duration_ms) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff);
        OvAvgDelivery.Text = avgMs.HasValue ? FormatMs(avgMs.Value) : "—";
        OvAvgDeliverySub.Text = maxMs.HasValue ? $"max {FormatMs(maxMs.Value)}" : "max —";

        OvSenders.Text = Num(QueryLong(conn, "SELECT COUNT(DISTINCT from_addr) FROM email_events WHERE event_type='sent' AND from_addr<>'' AND occurred_at >= $c", cutoff));
        var topSender = QueryString(conn, "SELECT from_addr FROM email_events WHERE event_type='sent' AND from_addr<>'' AND occurred_at >= $c GROUP BY from_addr ORDER BY COUNT(*) DESC LIMIT 1", cutoff);
        OvSendersSub.Text = topSender is not null ? $"top: {topSender}" : "";

        OvQueuedNow.Text = Num(CountQueueFiles());

        var permanentFailed = TryQueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='failed' AND permanent=1 AND occurred_at >= $c", cutoff);
        OvFailedSub.Text = permanentFailed.HasValue ? $"{permanentFailed.Value} rejected permanently" : "permanently failed";

        // Daily chart: hourly buckets for the 24 h range, daily buckets otherwise.
        _dailyBars = ReadFlowBars(conn, cutoff);
        Dispatcher.InvokeAsync(DrawDailyChart, DispatcherPriority.Render);
    }

    private List<DailyBar> ReadFlowBars(SqliteConnection conn, string cutoff)
    {
        var hourly = _rangeDays <= 1;
        var keyLen = hourly ? 13 : 10;   // 'yyyy-MM-ddTHH' vs 'yyyy-MM-dd'

        var sent = new Dictionary<string, long>();
        var failed = new Dictionary<string, long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT substr(occurred_at,1,{keyLen}) AS bucket, event_type, COUNT(*)
                FROM email_events
                WHERE event_type IN ('sent','failed') AND occurred_at >= $c
                GROUP BY bucket, event_type
                """;
            cmd.Parameters.AddWithValue("$c", cutoff);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var map = reader.GetString(1) == "sent" ? sent : failed;
                map[reader.GetString(0)] = reader.GetInt64(2);
            }
        }

        // Fill gaps so quiet periods show as empty slots, not missing slots.
        var bars = new List<DailyBar>();
        var nowUtc = DateTime.UtcNow;
        if (hourly)
        {
            for (var t = nowUtc.AddHours(-23); t <= nowUtc; t = t.AddHours(1))
            {
                var key = t.ToString("yyyy-MM-dd'T'HH");
                bars.Add(new DailyBar(t.ToLocalTime().ToString("HH"), sent.GetValueOrDefault(key), failed.GetValueOrDefault(key)));
            }
        }
        else
        {
            for (var d = DateOnly.FromDateTime(nowUtc.AddDays(-_rangeDays + 1)); d <= DateOnly.FromDateTime(nowUtc); d = d.AddDays(1))
            {
                var key = d.ToString("yyyy-MM-dd");
                bars.Add(new DailyBar(d.ToString("MM-dd"), sent.GetValueOrDefault(key), failed.GetValueOrDefault(key)));
            }
        }
        return bars;
    }

    // ── Reception ────────────────────────────────────────────────────────────

    private void LoadReception(SqliteConnection conn, string cutoff, string cutoffBucket)
    {
        long sessions = 0, aborted = 0, tls = 0, auth = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(SUM(count),0),
                       COALESCE(SUM(CASE WHEN outcome='aborted' THEN count ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN tls=1 THEN count ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN authenticated=1 THEN count ELSE 0 END),0)
                FROM smtp_session_stats WHERE bucket_hour >= $c
                """;
            cmd.Parameters.AddWithValue("$c", cutoffBucket);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                sessions = reader.GetInt64(0);
                aborted = reader.GetInt64(1);
                tls = reader.GetInt64(2);
                auth = reader.GetInt64(3);
            }
        }

        RcSessions.Text = Num(sessions);
        RcAborted.Text = Num(aborted);
        RcAbortedSub.Text = sessions > 0 ? $"{aborted * 100.0 / sessions:F1} % of sessions" : "";
        RcTlsShare.Text = sessions > 0 ? $"{tls * 100.0 / sessions:F0} %" : "—";
        RcTlsShareSub.Text = sessions > 0 ? $"{Num(tls)} encrypted sessions" : "";
        RcAuthShare.Text = sessions > 0 ? $"{auth * 100.0 / sessions:F0} %" : "—";
        RcAuthShareSub.Text = sessions > 0 ? $"{Num(auth)} authenticated" : "";

        var rejectedTotal = QueryLong(conn, "SELECT COALESCE(SUM(count),0) FROM smtp_rejection_stats WHERE bucket_hour >= $c", cutoffBucket);
        RcRejected.Text = Num(rejectedTotal);

        // Aborted sessions by last stage
        var stages = ReadNameCounts(conn, """
            SELECT last_stage, SUM(count) FROM smtp_session_stats
            WHERE outcome='aborted' AND bucket_hour >= $c
            GROUP BY last_stage ORDER BY SUM(count) DESC
            """, cutoffBucket);
        RcAbortStages.ItemsSource = BuildHBars(
            stages.Select(s => (StageLabel(s.Name), s.Count)).ToList(), AbortColor);

        // Rejections by reason
        RcRejectionsGrid.ItemsSource = ReadNameCounts(conn, """
            SELECT reason, SUM(count) FROM smtp_rejection_stats
            WHERE bucket_hour >= $c GROUP BY reason ORDER BY SUM(count) DESC
            """, cutoffBucket).Select(r => new NameCount(ReasonLabel(r.Name), r.Count)).ToList();

        // Top hosts: sessions + aborted from session stats, mails from email_events
        var mailsByIp = ReadNameCounts(conn, """
            SELECT client_ip, COUNT(*) FROM email_events
            WHERE event_type='received' AND client_ip IS NOT NULL AND occurred_at >= $c
            GROUP BY client_ip
            """, cutoff).ToDictionary(x => x.Name, x => x.Count);

        var hosts = new List<HostRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT client_ip, SUM(count),
                       SUM(CASE WHEN outcome='aborted' THEN count ELSE 0 END)
                FROM smtp_session_stats WHERE bucket_hour >= $c
                GROUP BY client_ip ORDER BY SUM(count) DESC LIMIT 8
                """;
            cmd.Parameters.AddWithValue("$c", cutoffBucket);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ip = reader.GetString(0);
                var total = reader.GetInt64(1);
                var abortedForIp = reader.GetInt64(2);
                var abortedPct = total > 0 ? abortedForIp * 100.0 / total : 0;
                hosts.Add(new HostRow(ip, Num(total), Num(abortedForIp), Num(mailsByIp.GetValueOrDefault(ip)),
                    abortedPct >= 25 && total >= 20 ? $"{abortedPct:F0} % aborted — monitoring?" : ""));
            }
        }
        RcTopHostsGrid.ItemsSource = hosts;

        // Per listener
        var mailsByPort = new Dictionary<long, long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT listener_port, COUNT(*) FROM email_events
                WHERE event_type='received' AND occurred_at >= $c GROUP BY listener_port
                """;
            cmd.Parameters.AddWithValue("$c", cutoff);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                mailsByPort[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        var listeners = new List<ListenerStatsRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT listener_port, SUM(count),
                       SUM(CASE WHEN tls=1 THEN count ELSE 0 END),
                       SUM(CASE WHEN authenticated=1 THEN count ELSE 0 END)
                FROM smtp_session_stats WHERE bucket_hour >= $c
                GROUP BY listener_port ORDER BY SUM(count) DESC
                """;
            cmd.Parameters.AddWithValue("$c", cutoffBucket);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var port = reader.GetInt64(0);
                var total = reader.GetInt64(1);
                listeners.Add(new ListenerStatsRow(
                    port > 0 ? port.ToString() : "unknown",
                    Num(total),
                    Num(mailsByPort.GetValueOrDefault(port)),
                    total > 0 ? $"{reader.GetInt64(2) * 100.0 / total:F0} %" : "—",
                    total > 0 ? $"{reader.GetInt64(3) * 100.0 / total:F0} %" : "—"));
            }
        }
        RcListenersGrid.ItemsSource = listeners;

        // Recipients / attachments hint line
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT AVG(to_count), AVG(cc_count), AVG(bcc_count),
                       AVG(CASE WHEN attachment_count > 0 THEN 1.0 ELSE 0.0 END)
                FROM email_events WHERE event_type='received' AND occurred_at >= $c
                """;
            cmd.Parameters.AddWithValue("$c", cutoff);
            using var reader = cmd.ExecuteReader();
            RcRecipientsHint.Text = reader.Read() && !reader.IsDBNull(0)
                ? $"Recipients per mail: Ø {reader.GetDouble(0):F1} To · {reader.GetDouble(1):F1} CC · {reader.GetDouble(2):F1} BCC   ·   Mails with attachments: {reader.GetDouble(3) * 100:F0} %"
                : "";
        }
    }

    private static string StageLabel(string stage) => stage switch
    {
        "connect" => "before HELO/EHLO",
        "helo" => "after HELO",
        "ehlo" => "after EHLO",
        "starttls" => "after STARTTLS",
        "auth" => "after AUTH",
        "mail" => "after MAIL FROM",
        "rcpt" => "after RCPT TO",
        "data" => "after DATA",
        "quit" => "after QUIT",
        _ => stage,
    };

    private static string ReasonLabel(string reason) => reason switch
    {
        "ip_blacklist" => "IP blacklist match",
        "ip_not_whitelisted" => "IP not whitelisted",
        "ip_blocked" => "IP blocked (repeated failures)",
        "auth_required" => "Authentication required",
        "auth_failed" => "Authentication failed",
        "prior_auth_failed" => "Prior auth attempt failed",
        "from_restriction" => "Sender not allowed for user",
        "blocked_sender" => "Blocked sender",
        "unknown_sender" => "Sender unknown in tenant",
        "sender_validation_unavailable" => "Sender validation unavailable",
        "blocked_recipient" => "Blocked recipient",
        "size_exceeded" => "Message size exceeded",
        "queue_error" => "Local queue error",
        _ => reason,
    };

    // ── Delivery ─────────────────────────────────────────────────────────────

    private void LoadDelivery(SqliteConnection conn, string cutoff)
    {
        var sent = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff);
        DlDelivered.Text = Num(sent);

        var avgMs = QueryDouble(conn, "SELECT AVG(duration_ms) FROM email_events WHERE event_type='sent' AND duration_ms > 0 AND occurred_at >= $c", cutoff);
        var maxMs = QueryDouble(conn, "SELECT MAX(duration_ms) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff);
        DlAvgTime.Text = avgMs.HasValue ? FormatMs(avgMs.Value) : "—";
        DlAvgTimeSub.Text = maxMs.HasValue ? $"max {FormatMs(maxMs.Value)}" : "max —";

        var firstTry = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND retry_count=0 AND occurred_at >= $c", cutoff);
        DlFirstTry.Text = sent > 0 ? $"{firstTry * 100.0 / sent:F1} %" : "—";
        DlFirstTrySub.Text = sent > 0 ? $"{Num(firstTry)} without retry" : "";

        var totalRetries = QueryLong(conn, "SELECT COALESCE(SUM(retry_count),0) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff);
        DlRetriesPerMail.Text = sent > 0 ? $"{(double)totalRetries / sent:F2}" : "—";

        // Retry histogram (attempts until success = retry_count + 1)
        var histogram = new List<(string Name, long Count)>
        {
            ("1st attempt", firstTry),
            ("2nd attempt", QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND retry_count=1 AND occurred_at >= $c", cutoff)),
            ("3rd attempt", QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND retry_count=2 AND occurred_at >= $c", cutoff)),
            ("4+ attempts", QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND retry_count>=3 AND occurred_at >= $c", cutoff)),
        };
        DlRetryHistogram.ItemsSource = BuildHBars(histogram, DeliveredColor);

        // Variant split
        var direct = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND (delivery_variant='sendMail' OR delivery_variant IS NULL) AND occurred_at >= $c", cutoff);
        var upload = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND delivery_variant='draftUpload' AND occurred_at >= $c", cutoff);
        DlVariants.ItemsSource = BuildHBars(
            [("sendMail (< 3 MB)", direct), ("Draft + upload session", upload)], DeliveredColor);

        // Top failure causes, normalized in C# (RequestIds make raw strings unique)
        var errors = new Dictionary<string, long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT error_detail FROM email_events WHERE event_type='failed' AND error_detail IS NOT NULL AND occurred_at >= $c";
            cmd.Parameters.AddWithValue("$c", cutoff);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = NormalizeError(reader.GetString(0));
                errors[key] = errors.GetValueOrDefault(key) + 1;
            }
        }
        DlErrorsGrid.ItemsSource = errors
            .OrderByDescending(kv => kv.Value)
            .Take(6)
            .Select(kv => new NameCount(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>Groups Graph error strings by their stable prefix (drops RequestIds and quotes).</summary>
    internal static string NormalizeError(string error)
    {
        var cut = error.IndexOf("(RequestId", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) error = error[..cut];
        error = error.Trim().TrimEnd(':', '-', ' ');
        return error.Length > 90 ? error[..90] + "…" : error;
    }

    // ── End-to-End ───────────────────────────────────────────────────────────

    private void LoadEndToEnd(SqliteConnection conn, string cutoff)
    {
        var avg = QueryDouble(conn, "SELECT AVG(queue_latency_ms) FROM email_events WHERE event_type='sent' AND queue_latency_ms > 0 AND occurred_at >= $c", cutoff);
        var max = QueryDouble(conn, "SELECT MAX(queue_latency_ms) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff);
        E2AvgLatency.Text = avg.HasValue ? FormatMs(avg.Value) : "—";
        E2MaxLatency.Text = max is > 0 ? FormatMs(max.Value) : "—";

        var count = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND queue_latency_ms > 0 AND occurred_at >= $c", cutoff);
        if (count > 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT queue_latency_ms FROM email_events
                WHERE event_type='sent' AND queue_latency_ms > 0 AND occurred_at >= $c
                ORDER BY queue_latency_ms LIMIT 1 OFFSET $o
                """;
            cmd.Parameters.AddWithValue("$c", cutoff);
            cmd.Parameters.AddWithValue("$o", Math.Max(0, (long)Math.Ceiling(count * 0.95) - 1));
            E2P95Latency.Text = cmd.ExecuteScalar() is long p95 ? FormatMs(p95) : "—";
        }
        else
        {
            E2P95Latency.Text = "—";
        }

        var received = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='received' AND occurred_at >= $c", cutoff);
        var sent = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff);
        var failed = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='failed' AND occurred_at >= $c", cutoff);
        var firstTry = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent' AND retry_count=0 AND occurred_at >= $c", cutoff);
        E2FirstTry.Text = sent > 0 ? $"{firstTry * 100.0 / sent:F1} %" : "—";

        E2Funnel.ItemsSource = new List<HBarRow>
        {
            HBar("Received", received, Math.Max(received, 1), DeliveredColor),
            HBar("Delivered", sent, Math.Max(received, 1), DeliveredColor),
            HBar("Failed", failed, Math.Max(received, 1), FailedColor),
            HBar("Still queued", CountQueueFiles(), Math.Max(received, 1), AbortColor),
        };

        var permanent = QueryLong(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='failed' AND permanent=1 AND occurred_at >= $c", cutoff);
        var expired = failed - permanent;
        var failMax = Math.Max(Math.Max(permanent, expired), 1);
        E2Failures.ItemsSource = new List<HBarRow>
        {
            HBar("Rejected by Graph", permanent, failMax, FailedColor),
            HBar("Expired / other", expired, failMax, FailedColor),
        };
    }

    // ── Server ───────────────────────────────────────────────────────────────

    private void LoadServer(SqliteConnection conn)
    {
        var perfCutoff = DateTime.UtcNow.AddDays(-_rangeDays).ToString("O");
        _memSamples = QueryPerfSamples(conn, "memory_mb", perfCutoff);
        _cpuSamples = QueryPerfSamples(conn, "cpu_percent", perfCutoff);
        _diskSamples = QueryPerfSamples(conn, "disk_free_percent", perfCutoff);

        PerfMemory.Text = _memSamples.Count > 0 ? $"{_memSamples.Last():F0} MB" : "—";
        PerfMemorySub.Text = _memSamples.Count > 0 ? $"peak {_memSamples.Max():F0} MB" : "";
        PerfCpu.Text = _cpuSamples.Count > 0 ? $"{_cpuSamples.Last():F1} %" : "—";
        PerfCpuSub.Text = _cpuSamples.Count > 0 ? $"peak {_cpuSamples.Max():F1} %" : "";
        PerfDisk.Text = _diskSamples.Count > 0 ? $"{_diskSamples.Last():F1} %" : "—";

        try
        {
            var info = new FileInfo(DbPath);
            SrvDbSize.Text = info.Exists ? FormatBytes(info.Length) : "—";
            SrvDbSizeSub.Text = "on-disk size";
        }
        catch
        {
            SrvDbSize.Text = "—";
        }

        NoPerfText.Visibility = (_memSamples.Count < 2 && _cpuSamples.Count < 2 && _diskSamples.Count < 2)
            ? Visibility.Visible : Visibility.Collapsed;

        Dispatcher.InvokeAsync(DrawPerfCharts, DispatcherPriority.Render);
    }

    // ── Recent activity ──────────────────────────────────────────────────────

    private void LoadActivity(SqliteConnection conn)
    {
        var rows = new List<ActivityRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT occurred_at, event_type, from_addr, to_addrs, message_id, subject, size_bytes, duration_ms, error_detail,
                   attachment_count, listener_port, tls, authenticated, auth_user
            FROM email_events
            ORDER BY occurred_at DESC
            LIMIT 200
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var ts = reader.GetString(0);
            var evt = reader.GetString(1);
            var from = reader.GetString(2);
            var toAddrs = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var msgId = reader.GetString(4);
            var subject = reader.IsDBNull(5) ? "" : reader.GetString(5);
            var sizeB = reader.GetInt64(6);
            var durMs = reader.GetInt32(7);
            var error = reader.IsDBNull(8) ? "" : reader.GetString(8);
            var attCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
            var port = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
            var tls = !reader.IsDBNull(11) && reader.GetInt32(11) == 1;
            var authed = !reader.IsDBNull(12) && reader.GetInt32(12) == 1;
            var authUser = reader.IsDBNull(13) ? "" : reader.GetString(13);

            var displayTs = DateTime.TryParse(ts, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : ts;

            var isReceived = evt == "received";
            rows.Add(new ActivityRow(
                Timestamp: displayTs,
                Event: evt,
                From: from,
                To: !string.IsNullOrEmpty(toAddrs) ? toAddrs : "—",
                Subject: subject,
                Attachments: isReceived || evt == "sent" ? attCount.ToString() : "—",
                Listener: isReceived && port > 0 ? port.ToString() : "—",
                Tls: isReceived ? (tls ? "✔" : "—") : "",
                Auth: isReceived ? (authed ? authUser : "—") : "",
                Size: sizeB > 0 ? FormatBytes(sizeB) : "—",
                Duration: durMs > 0 ? $"{durMs} ms" : "—",
                Detail: !string.IsNullOrEmpty(error) ? error : msgId));
        }

        ActivityGrid.ItemsSource = rows;
        NoDataText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Chart drawing
    // ═════════════════════════════════════════════════════════════════════════

    private void DrawPerfCharts()
    {
        DrawSparkline(MemoryCanvas, _memSamples, Color.FromRgb(0x42, 0x90, 0xF5), " MB");
        DrawSparkline(CpuCanvas, _cpuSamples, Color.FromRgb(0xFF, 0x98, 0x00), "%");
        DrawSparkline(DiskCanvas, _diskSamples, Color.FromRgb(0x4C, 0xAF, 0x50), "%");
    }

    /// <summary>Grouped bars per bucket: delivered (wide, blue) + failed (narrow, red).</summary>
    private void DrawDailyChart()
    {
        var canvas = DailyCanvas;
        canvas.Children.Clear();
        if (_dailyBars.Count == 0) return;

        const double padL = 40, padR = 8, padT = 8, padB = 20;
        var cw = canvas.ActualWidth;
        var ch = canvas.ActualHeight;
        if (cw < 40 || ch < 40) return;

        var w = cw - padL - padR;
        var h = ch - padT - padB;

        var maxVal = Math.Max(_dailyBars.Max(b => Math.Max(b.Sent, b.Failed)), 1);

        // Grid lines + labels at 0/50/100 %
        var gridBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128));
        var lblBrush = new SolidColorBrush(Color.FromArgb(170, 110, 110, 110));
        for (int pct = 0; pct <= 100; pct += 50)
        {
            var y = padT + h * (1 - pct / 100.0);
            canvas.Children.Add(new WpfShapes.Line { X1 = padL, Y1 = y, X2 = padL + w, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            var lbl = new TextBlock { Text = Num((long)Math.Round(maxVal * pct / 100.0)), FontSize = 9, Foreground = lblBrush };
            Canvas.SetLeft(lbl, 2);
            Canvas.SetTop(lbl, y - 6);
            canvas.Children.Add(lbl);
        }

        var slot = w / _dailyBars.Count;
        var sentWidth = Math.Max(3, Math.Min(slot * 0.55, 46.0));
        var failWidth = Math.Max(2, sentWidth * 0.3);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(_dailyBars.Count * 60.0 / w));   // ≥60px per label

        for (int i = 0; i < _dailyBars.Count; i++)
        {
            var bar = _dailyBars[i];
            var xCenter = padL + slot * i + slot / 2;

            var sentH = h * bar.Sent / maxVal;
            if (bar.Sent > 0)
            {
                var rect = new WpfShapes.Rectangle
                {
                    Width = sentWidth,
                    Height = Math.Max(2, sentH),
                    RadiusX = 2, RadiusY = 2,
                    Fill = new SolidColorBrush(DeliveredColor),
                    ToolTip = $"{bar.Label}: delivered {Num(bar.Sent)}",
                };
                Canvas.SetLeft(rect, xCenter - sentWidth / 2 - failWidth / 2 - 1);
                Canvas.SetTop(rect, padT + h - rect.Height);
                canvas.Children.Add(rect);
            }

            if (bar.Failed > 0)
            {
                var failH = h * bar.Failed / maxVal;
                var rect = new WpfShapes.Rectangle
                {
                    Width = failWidth,
                    Height = Math.Max(2, failH),
                    RadiusX = 1.5, RadiusY = 1.5,
                    Fill = new SolidColorBrush(FailedColor),
                    ToolTip = $"{bar.Label}: failed {Num(bar.Failed)}",
                };
                Canvas.SetLeft(rect, xCenter + sentWidth / 2 - failWidth / 2 + 1);
                Canvas.SetTop(rect, padT + h - rect.Height);
                canvas.Children.Add(rect);
            }

            if (i % labelEvery == 0)
            {
                var lbl = new TextBlock { Text = bar.Label, FontSize = 9.5, Foreground = lblBrush };
                Canvas.SetLeft(lbl, xCenter - 14);
                Canvas.SetTop(lbl, padT + h + 4);
                canvas.Children.Add(lbl);
            }
        }

        // Baseline
        var axisBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128));
        canvas.Children.Add(new WpfShapes.Line { X1 = padL, Y1 = padT + h, X2 = padL + w, Y2 = padT + h, Stroke = axisBrush, StrokeThickness = 1 });
    }

    private static void DrawSparkline(Canvas canvas, IReadOnlyList<double> values, Color color, string unit)
    {
        canvas.Children.Clear();
        if (values.Count < 2) return;

        const double padL = 36, padR = 6, padT = 6, padB = 16;
        var cw = canvas.ActualWidth;
        var ch = canvas.ActualHeight;
        if (cw < 20 || ch < 20) return;

        var w = cw - padL - padR;
        var h = ch - padT - padB;

        var min = values.Min();
        var max = values.Max();
        if (max - min < 0.5) { min = Math.Max(0, min - 0.5); max = min + 1; }

        double ToX(int i) => padL + i / (double)(values.Count - 1) * w;
        double ToY(double v) => padT + (1.0 - (v - min) / (max - min)) * h;

        // Subtle grid lines at 0 %, 25 %, 50 %, 75 %, 100 % of range
        var gridBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128));
        for (int pct = 0; pct <= 100; pct += 25)
        {
            var y = ToY(min + (max - min) * pct / 100.0);
            canvas.Children.Add(new WpfShapes.Line { X1 = padL, Y1 = y, X2 = padL + w, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
        }

        // Fill polygon under the line
        var fillPts = new PointCollection { new(ToX(0), padT + h) };
        for (int i = 0; i < values.Count; i++) fillPts.Add(new Point(ToX(i), ToY(values[i])));
        fillPts.Add(new Point(ToX(values.Count - 1), padT + h));
        canvas.Children.Add(new WpfShapes.Polygon
        {
            Points = fillPts,
            Fill = new SolidColorBrush(Color.FromArgb(45, color.R, color.G, color.B)),
            StrokeThickness = 0,
        });

        // Data line
        var pts = new PointCollection();
        for (int i = 0; i < values.Count; i++) pts.Add(new Point(ToX(i), ToY(values[i])));
        canvas.Children.Add(new WpfShapes.Polyline
        {
            Points = pts,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        });

        // Axes
        var axisBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128));
        canvas.Children.Add(new WpfShapes.Line { X1 = padL, Y1 = padT, X2 = padL, Y2 = padT + h, Stroke = axisBrush, StrokeThickness = 1 });
        canvas.Children.Add(new WpfShapes.Line { X1 = padL, Y1 = padT + h, X2 = padL + w, Y2 = padT + h, Stroke = axisBrush, StrokeThickness = 1 });

        // Y-axis labels
        var lblBrush = new SolidColorBrush(Color.FromArgb(170, 110, 110, 110));
        var maxLbl = new TextBlock { Text = FormatLabel(max, unit), FontSize = 9, Foreground = lblBrush };
        Canvas.SetLeft(maxLbl, 0); Canvas.SetTop(maxLbl, padT - 1);
        canvas.Children.Add(maxLbl);

        var minLbl = new TextBlock { Text = FormatLabel(min, unit), FontSize = 9, Foreground = lblBrush };
        Canvas.SetLeft(minLbl, 0); Canvas.SetTop(minLbl, padT + h - 10);
        canvas.Children.Add(minLbl);
    }

    private static string FormatLabel(double v, string unit)
        => unit == " MB" ? $"{v:F0}MB" : $"{v:F0}%";

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static List<HBarRow> BuildHBars(IReadOnlyList<(string Name, long Count)> rows, Color color)
    {
        var max = Math.Max(rows.Count > 0 ? rows.Max(r => r.Count) : 0, 1);
        return rows.Select(r => HBar(r.Name, r.Count, max, color)).ToList();
    }

    private static HBarRow HBar(string name, long count, long max, Color color)
    {
        var ratio = Math.Clamp((double)count / max, 0, 1);
        return new HBarRow(
            name,
            Num(count),
            new GridLength(Math.Max(ratio, 0.002), GridUnitType.Star),
            new GridLength(Math.Max(1 - ratio, 0.001), GridUnitType.Star),
            new SolidColorBrush(color));
    }

    private long CountQueueFiles()
    {
        try
        {
            var queueDir = Path.Combine(AppPaths.MailDir, "queue");
            return Directory.Exists(queueDir) ? Directory.GetFiles(queueDir, "*.meta.json").Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string Num(long n) => n.ToString("N0");

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F0} KB",
        _ => $"{bytes} B",
    };

    internal static string FormatMs(double ms) => ms switch
    {
        >= 3_600_000 => $"{ms / 3_600_000:F1} h",
        >= 60_000 => $"{ms / 60_000:F1} min",
        >= 10_000 => $"{ms / 1_000:F1} s",
        >= 1_000 => $"{ms / 1_000:F2} s",
        _ => $"{ms:F0} ms",
    };

    // ── DB helpers ───────────────────────────────────────────────────────────

    /// <summary>Returns samples within the time window in chronological order, downsampled to at most 150 points.</summary>
    private static List<double> QueryPerfSamples(SqliteConnection conn, string metricType, string cutoffUtc)
    {
        var all = new List<double>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT value FROM perf_metrics
            WHERE metric_type = $t AND recorded_at >= $c
            ORDER BY recorded_at ASC
            """;
        cmd.Parameters.AddWithValue("$t", metricType);
        cmd.Parameters.AddWithValue("$c", cutoffUtc);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) all.Add(reader.GetDouble(0));

        if (all.Count <= 150) return all;

        // Downsample evenly to 150 points
        var step = all.Count / 150.0;
        var result = new List<double>(150);
        for (int i = 0; i < 150; i++)
            result.Add(all[(int)(i * step)]);
        return result;
    }

    private static List<NameCountRaw> ReadNameCounts(SqliteConnection conn, string sql, string cutoff)
    {
        var list = new List<NameCountRaw>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$c", cutoff);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new NameCountRaw(reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    private static long QueryLong(SqliteConnection conn, string sql, string cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$c", cutoff);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : 0;
    }

    private static long? TryQueryLong(SqliteConnection conn, string sql, string cutoff)
    {
        try { return QueryLong(conn, sql, cutoff); }
        catch (SqliteException) { return null; }
    }

    private static double? QueryDouble(SqliteConnection conn, string sql, string cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$c", cutoff);
        var result = cmd.ExecuteScalar();
        return result switch
        {
            double d => d,
            long l => l,
            _ => null,
        };
    }

    private static string? QueryString(SqliteConnection conn, string sql, string cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$c", cutoff);
        return cmd.ExecuteScalar() as string;
    }

    private sealed record DailyBar(string Label, long Sent, long Failed);
    private sealed record NameCountRaw(string Name, long Count);
}

public record ActivityRow(
    string Timestamp,
    string Event,
    string From,
    string To,
    string Subject,
    string Attachments,
    string Listener,
    string Tls,
    string Auth,
    string Size,
    string Duration,
    string Detail);

public record NameCount(string Name, long Count);

public record HostRow(string Ip, string Sessions, string Aborted, string Mails, string Note);

public record ListenerStatsRow(string Listener, string Sessions, string Mails, string TlsShare, string AuthShare);

public record HBarRow(
    string Name,
    string CountText,
    System.Windows.GridLength FillStar,
    System.Windows.GridLength RestStar,
    System.Windows.Media.Brush Fill);
