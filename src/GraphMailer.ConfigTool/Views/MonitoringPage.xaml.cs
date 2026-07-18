using System.Windows;
using System.Windows.Controls;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.ConfigTool.Views;

public partial class MonitoringPage : UserControl
{
    private readonly Action _markDirty;

    public MonitoringPage(Action markDirty)
    {
        _markDirty = markDirty;
        InitializeComponent();
        LogLevel.ItemsSource = LogLevels;
    }

    // All valid Serilog levels; sole source of the LogLevel ComboBox items.
    private static readonly string[] LogLevels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    internal void LoadFrom(ConfigDocument doc)
    {
        CertWarnDays.Text = doc.Monitoring.CertWarnDays.ToString();
        DiskWarnPct.Text = doc.Monitoring.DiskWarnPct.ToString();
        PortCheckInterval.Text = doc.Monitoring.PortCheckIntervalMinutes.ToString();
        GraphCheckInterval.Text = doc.Monitoring.GraphCheckIntervalMinutes.ToString();
        UpdateCheckEnabled.IsChecked = doc.Monitoring.UpdateCheckEnabled;

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

    private void AnyField_Changed(object sender, TextChangedEventArgs e) => _markDirty();
    private void AnyCheckBox_Changed(object sender, RoutedEventArgs e) => _markDirty();
    private void LogLevel_Changed(object sender, SelectionChangedEventArgs e) => _markDirty();
}
