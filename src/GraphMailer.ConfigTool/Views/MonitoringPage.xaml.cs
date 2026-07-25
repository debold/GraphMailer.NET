using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.ConfigTool.Views;

public partial class MonitoringPage : UserControl
{
    private readonly Action _markDirty;
    private readonly DispatcherTimer _telemetryStatusTimer;

    public MonitoringPage(Action markDirty)
    {
        _markDirty = markDirty;
        InitializeComponent();
        LogLevel.ItemsSource = LogLevels;

        // The "Last transmission" line mirrors a file the service rewrites on every
        // heartbeat — refresh it while the page is visible (StatusPage idiom), not
        // only on LoadFrom, so a send while the tool is open becomes visible.
        _telemetryStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _telemetryStatusTimer.Tick += (_, _) => TelemetryStatusText.Text = DescribeTelemetryStatus();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                TelemetryStatusText.Text = DescribeTelemetryStatus();
                _telemetryStatusTimer.Start();
            }
            else
            {
                _telemetryStatusTimer.Stop();
            }
        };
    }

    private Action? _openRecommendations;

    /// <summary>Badges each card whose setting an open recommendation targets on this page; the
    /// badges link to the Recommendations page via <paramref name="openRecommendations"/>.</summary>
    internal void ApplyRecommendations(IReadOnlyList<Recommendation> open, Action openRecommendations)
    {
        _openRecommendations = openRecommendations;
        RecommendationBadgeStyle.ApplyLabel(UpdateCheckRecBadge, UpdateCheckRecBadgeText,
            [.. open.Where(r => r.Id == RecommendationIds.UpdateCheck)]);
        RecommendationBadgeStyle.ApplyLabel(LogLevelRecBadge, LogLevelRecBadgeText,
            [.. open.Where(r => r.Id == RecommendationIds.LogLevel)]);
        RecommendationBadgeStyle.ApplyLabel(TelemetryRecBadge, TelemetryRecBadgeText,
            [.. open.Where(r => r.Id == RecommendationIds.Telemetry)]);
    }

    private void RecommendationBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _openRecommendations?.Invoke();

    // All valid Serilog levels; sole source of the LogLevel ComboBox items.
    private static readonly string[] LogLevels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    internal void LoadFrom(ConfigDocument doc)
    {
        CertWarnDays.Text = doc.Monitoring.CertWarnDays.ToString();
        DiskWarnPct.Text = doc.Monitoring.DiskWarnPct.ToString();
        PortCheckInterval.Text = doc.Monitoring.PortCheckIntervalMinutes.ToString();
        GraphCheckInterval.Text = doc.Monitoring.GraphCheckIntervalMinutes.ToString();
        UpdateCheckEnabled.IsChecked = doc.Monitoring.UpdateCheckEnabled;
        TelemetryEnabled.IsChecked = doc.Monitoring.TelemetryEnabled;
        TelemetryStatusText.Text = DescribeTelemetryStatus();

        MetricsEnabled.IsChecked = doc.Metrics.Enabled;
        MetricsRetentionDays.Text = doc.Metrics.RetentionDays.ToString();
        MetricsCleanupIntervalHours.Text = doc.Metrics.CleanupIntervalHours.ToString();
        PerfMetricsEnabled.IsChecked = doc.Metrics.PerfMetricsEnabled;
        PerfMemoryInterval.Text = doc.Metrics.PerfMemoryIntervalSeconds.ToString();
        PerfCpuInterval.Text = doc.Metrics.PerfCpuIntervalSeconds.ToString();
        PerfDiskInterval.Text = doc.Metrics.PerfDiskIntervalSeconds.ToString();

        var idx = Array.FindIndex(LogLevels,
            l => l.Equals(doc.Logging.DefaultLevel, StringComparison.OrdinalIgnoreCase));
        LogLevel.SelectedIndex = idx >= 0 ? idx : 2;
    }

    internal void CollectTo(ConfigDocument doc)
    {
        doc.Monitoring.CertWarnDays = int.TryParse(CertWarnDays.Text, out var cw) ? cw : 14;
        doc.Monitoring.DiskWarnPct = int.TryParse(DiskWarnPct.Text, out var dw) ? dw : 10;
        doc.Monitoring.PortCheckIntervalMinutes = int.TryParse(PortCheckInterval.Text, out var pc) ? pc : 5;
        doc.Monitoring.GraphCheckIntervalMinutes = int.TryParse(GraphCheckInterval.Text, out var gc) ? gc : 15;
        doc.Monitoring.UpdateCheckEnabled = UpdateCheckEnabled.IsChecked == true;
        doc.Monitoring.TelemetryEnabled = TelemetryEnabled.IsChecked == true;

        doc.Metrics.Enabled = MetricsEnabled.IsChecked == true;
        doc.Metrics.RetentionDays = int.TryParse(MetricsRetentionDays.Text, out var rd) ? rd : 90;
        doc.Metrics.CleanupIntervalHours = int.TryParse(MetricsCleanupIntervalHours.Text, out var ci) ? ci : 24;
        doc.Metrics.PerfMetricsEnabled = PerfMetricsEnabled.IsChecked == true;
        doc.Metrics.PerfMemoryIntervalSeconds = int.TryParse(PerfMemoryInterval.Text, out var mem) ? mem : 60;
        doc.Metrics.PerfCpuIntervalSeconds = int.TryParse(PerfCpuInterval.Text, out var cpu) ? cpu : 60;
        doc.Metrics.PerfDiskIntervalSeconds = int.TryParse(PerfDiskInterval.Text, out var disk) ? disk : 300;

        if (LogLevel.SelectedIndex >= 0)
            doc.Logging.DefaultLevel = LogLevels[LogLevel.SelectedIndex];
    }

    /// <summary>Transparency line: install id + last heartbeat from the service-written status file.</summary>
    private static string DescribeTelemetryStatus()
    {
        var status = GraphMailer.Service.Services.Telemetry.TelemetryStatus.TryLoad(
            GraphMailer.Service.Services.Telemetry.TelemetryStatus.StatusFilePath);
        if (status?.LastHeartbeatUtc is not DateTime last)
            return "No telemetry sent yet.";

        var line = $"Install id {status.InstallId} — last heartbeat {last.ToLocalTime():g}";
        return status.LastError is null ? line : $"{line} (failed: {status.LastError})";
    }

    private void AnyField_Changed(object sender, TextChangedEventArgs e) => _markDirty();
    private void AnyCheckBox_Changed(object sender, RoutedEventArgs e) => _markDirty();
    private void LogLevel_Changed(object sender, SelectionChangedEventArgs e) => _markDirty();
}
