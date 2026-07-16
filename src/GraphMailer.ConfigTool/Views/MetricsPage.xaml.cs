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

public partial class MetricsPage : UserControl
{
    private readonly DispatcherTimer _timer;

    private List<double> _memSamples = [];
    private List<double> _cpuSamples = [];
    private List<double> _diskSamples = [];
    private int _perfRangeDays = 1;

    private static string DbPath => Path.Combine(AppPaths.DataDir, "metrics.db");

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

    private void PerfRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out var days)) return;
        _perfRangeDays = days;

        foreach (var b in new[] { PerfRange24h, PerfRange7d, PerfRange30d })
            b.Style = (Style)FindResource("SmallButton");
        btn.Style = (Style)FindResource("SmallPrimaryButton");

        if (!File.Exists(DbPath)) return;
        try
        {
            var perfCutoff = DateTime.UtcNow.AddDays(-_perfRangeDays).ToString("O");
            using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            conn.Open();
            _memSamples = QueryPerfSamples(conn, "memory_mb", perfCutoff);
            _cpuSamples = QueryPerfSamples(conn, "cpu_percent", perfCutoff);
            _diskSamples = QueryPerfSamples(conn, "disk_free_percent", perfCutoff);
        }
        catch { /* silently ignore on toggle */ }

        NoPerfText.Visibility = (_memSamples.Count < 2 && _cpuSamples.Count < 2 && _diskSamples.Count < 2)
            ? Visibility.Visible : Visibility.Collapsed;
        DrawPerfCharts();
    }

    private void PerfCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPerfCharts();

    private void LoadData()
    {
        if (!File.Exists(DbPath))
        {
            M30Sent.Text = "—";
            M30Failed.Text = "—";
            M30Avg.Text = "—";
            M30Senders.Text = "—";
            PerfMemory.Text = "—";
            PerfCpu.Text = "—";
            PerfDisk.Text = "—";
            _memSamples = [];
            _cpuSamples = [];
            _diskSamples = [];
            ActivityGrid.ItemsSource = Array.Empty<ActivityRow>();
            NoDataText.Visibility = Visibility.Visible;
            NoPerfText.Visibility = Visibility.Visible;
            DrawPerfCharts();
            return;
        }

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30).ToString("O");

            using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            conn.Open();

            // ── Email KPIs ───────────────────────────────────────────────────
            M30Sent.Text = QueryScalar(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent'   AND occurred_at >= $c", cutoff).ToString();
            M30Failed.Text = QueryScalar(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='failed' AND occurred_at >= $c", cutoff).ToString();
            M30Senders.Text = QueryScalar(conn, "SELECT COUNT(DISTINCT from_addr) FROM email_events WHERE event_type='sent' AND occurred_at >= $c", cutoff).ToString();

            var avgMs = QueryAvg(conn, "SELECT AVG(duration_ms) FROM email_events WHERE event_type='sent' AND duration_ms > 0 AND occurred_at >= $c", cutoff);
            M30Avg.Text = avgMs.HasValue ? $"{avgMs.Value:F0}" : "—";

            // ── Performance data (chronological for chart, filtered by range) ──
            var perfCutoff = DateTime.UtcNow.AddDays(-_perfRangeDays).ToString("O");
            _memSamples = QueryPerfSamples(conn, "memory_mb", perfCutoff);
            _cpuSamples = QueryPerfSamples(conn, "cpu_percent", perfCutoff);
            _diskSamples = QueryPerfSamples(conn, "disk_free_percent", perfCutoff);

            PerfMemory.Text = _memSamples.Count > 0 ? $"{_memSamples.Last():F0} MB" : "—";
            PerfCpu.Text = _cpuSamples.Count > 0 ? $"{_cpuSamples.Last():F1} %" : "—";
            PerfDisk.Text = _diskSamples.Count > 0 ? $"{_diskSamples.Last():F1} %" : "—";

            NoPerfText.Visibility = (_memSamples.Count < 2 && _cpuSamples.Count < 2 && _diskSamples.Count < 2)
                ? Visibility.Visible : Visibility.Collapsed;

            // Draw after layout pass so ActualWidth is valid
            Dispatcher.InvokeAsync(DrawPerfCharts, DispatcherPriority.Render);

            // ── Recent activity ──────────────────────────────────────────────
            var rows = new List<ActivityRow>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT occurred_at, event_type, from_addr, to_addrs, message_id, subject, size_bytes, duration_ms, error_detail
                FROM email_events
                ORDER BY occurred_at DESC
                LIMIT 200
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ts      = reader.GetString(0);
                var evt     = reader.GetString(1);
                var from    = reader.GetString(2);
                var toAddrs = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var msgId   = reader.GetString(4);
                var subject = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var sizeB   = reader.GetInt64(6);
                var durMs   = reader.GetInt32(7);
                var error   = reader.IsDBNull(8) ? "" : reader.GetString(8);

                var displayTs = DateTime.TryParse(ts, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                    ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : ts;

                var sizeStr = sizeB > 0
                    ? sizeB >= 1_048_576 ? $"{sizeB / 1_048_576.0:F1} MB"
                    : sizeB >= 1_024 ? $"{sizeB / 1_024.0:F0} KB"
                    : $"{sizeB} B"
                    : "—";

                var durStr = durMs > 0 ? $"{durMs} ms" : "—";

                rows.Add(new ActivityRow(
                    Timestamp: displayTs,
                    Event: evt,
                    From: from,
                    To: !string.IsNullOrEmpty(toAddrs) ? toAddrs : "—",
                    Subject: subject,
                    Size: sizeStr,
                    Duration: durStr,
                    Detail: !string.IsNullOrEmpty(error) ? error : msgId));
            }

            ActivityGrid.ItemsSource = rows;
            NoDataText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("MetricsPage", ex, "Could not read metrics database");
            M30Sent.Text = M30Failed.Text = M30Avg.Text = M30Senders.Text = "ERR";
            PerfMemory.Text = PerfCpu.Text = PerfDisk.Text = "ERR";
            NoDataText.Text = $"Could not read metrics database: {ex.Message}";
            NoDataText.Visibility = Visibility.Visible;
        }
    }

    // ── Chart drawing ────────────────────────────────────────────────────────

    private void DrawPerfCharts()
    {
        DrawSparkline(MemoryCanvas, _memSamples, Color.FromRgb(0x42, 0x90, 0xF5), " MB");
        DrawSparkline(CpuCanvas, _cpuSamples, Color.FromRgb(0xFF, 0x98, 0x00), "%");
        DrawSparkline(DiskCanvas, _diskSamples, Color.FromRgb(0x4C, 0xAF, 0x50), "%");
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

    private static long QueryScalar(SqliteConnection conn, string sql, string cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$c", cutoff);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : 0;
    }

    private static double? QueryAvg(SqliteConnection conn, string sql, string cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$c", cutoff);
        var result = cmd.ExecuteScalar();
        return result is double d ? d : (double?)null;
    }
}

public record ActivityRow(
    string Timestamp,
    string Event,
    string From,
    string To,
    string Subject,
    string Size,
    string Duration,
    string Detail);
