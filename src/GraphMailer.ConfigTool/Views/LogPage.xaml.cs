using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.ConfigTool.Views;

public partial class LogPage : UserControl
{
    // Serilog default file output format (no custom template set in Program.cs):
    // 2025-12-25 10:30:00.123 +01:00 [INF] [Component] Message text
    private static readonly Regex LogLineRegex = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+\s+[+-]\d{2}:\d{2})\s+\[(?<lvl>[A-Z]{3})\]\s+(?<msg>.*)$",
        RegexOptions.Compiled);

    private readonly DispatcherTimer _timer;
    private bool _loadInProgress;

    private List<LogEntry> _allEntries = [];

    public LogPage()
    {
        InitializeComponent();

        // Populate component filter with static placeholder until first load
        ComponentFilter.Items.Add(new ComboBoxItem { Content = "All" });
        ComponentFilter.SelectedIndex = 0;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        // Auto-refresh off = frozen view: the periodic tick must not reload,
        // otherwise entries shift around while the user is reading or searching.
        _timer.Tick += (_, _) => { if (AutoScrollCheck.IsChecked == true) LoadData(); };

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { LoadData(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    private async void LoadData()
    {
        if (_loadInProgress) return;
        _loadInProgress = true;
        try { await LoadDataAsync(); }
        finally { _loadInProgress = false; }
    }

    private async Task LoadDataAsync()
    {
        var entries = await Task.Run(ReadLogEntries);

        // Preserve selected component filter across refreshes
        var prevComponent = (ComponentFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "All";

        // Rebuild component dropdown from unique values in loaded data
        var components = entries
            .Select(e => e.Component)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        ComponentFilter.SelectionChanged -= Filter_Changed;
        ComponentFilter.Items.Clear();
        ComponentFilter.Items.Add(new ComboBoxItem { Content = "All" });
        foreach (var c in components)
            ComponentFilter.Items.Add(new ComboBoxItem { Content = c });

        // Restore or default to "All"
        var match = ComponentFilter.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Content == prevComponent);
        ComponentFilter.SelectedItem = match ?? ComponentFilter.Items[0];
        ComponentFilter.SelectionChanged += Filter_Changed;

        _allEntries = entries;
        ApplyFilter();
    }

    private static List<LogEntry> ReadLogEntries()
    {
        var logsDir = AppPaths.LogsDir;
        if (!Directory.Exists(logsDir)) return [];

        // Read today's and yesterday's rolling log file (most recent 2000 lines total)
        // Ascending order so the combined list is chronological → Reverse() yields newest first
        var files = Directory.GetFiles(logsDir, "graphmailer-*.log")
            .OrderBy(f => f)
            .TakeLast(2)
            .ToList();

        if (files.Count == 0) return [];

        var rawLines = new List<string>();
        foreach (var file in files)
        {
            try
            {
                // Open with shared read so we can read while the service has the file open
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                rawLines.AddRange(reader.ReadToEnd()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch { /* skip unreadable file */ }
        }

        // Keep newest 2000 lines to avoid flooding the grid
        var lines = rawLines.Count > 2000
            ? rawLines.GetRange(rawLines.Count - 2000, 2000)
            : rawLines;

        var entries = new List<LogEntry>(lines.Count);
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            var entry = ParseLine(trimmed);
            if (entry is not null)
            {
                entries.Add(entry);
            }
            else if (entries.Count > 0 && !string.IsNullOrWhiteSpace(trimmed))
            {
                // Continuation line (stack trace etc.) — append to the previous entry
                var prev = entries[^1];
                entries[^1] = prev with { RawLine = prev.RawLine + "\n" + trimmed };
            }
        }

        // Newest first
        entries.Reverse();
        return entries;
    }

    private static LogEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var m = LogLineRegex.Match(line);
        if (!m.Success)
            return null; // continuation / stack trace — will be appended to previous entry

        var tsStr = m.Groups["ts"].Value.Trim();
        var level = ExpandLevel(m.Groups["lvl"].Value);
        var msg = m.Groups["msg"].Value;

        // Extract [Component] prefix from message
        string component = "";
        var compMatch = Regex.Match(msg, @"^\[([^\]]+)\]\s*(.*)$");
        if (compMatch.Success)
        {
            component = compMatch.Groups[1].Value;
            msg = compMatch.Groups[2].Value;
        }

        // Parse timestamp and convert to local time for display
        string timeLocal = tsStr;
        if (DateTimeOffset.TryParse(tsStr, out var dto))
            timeLocal = dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        return new LogEntry(timeLocal, level, component, msg, line);
    }

    private static string ExpandLevel(string abbr) => abbr switch
    {
        "VRB" => "Verbose",
        "DBG" => "Debug",
        "INF" => "Information",
        "WRN" => "Warning",
        "ERR" => "Error",
        "FTL" => "Fatal",
        _ => abbr,
    };

    private static string ShortLevel(string full) => full switch
    {
        "Verbose" => "VRB",
        "Debug" => "DBG",
        "Information" => "INF",
        "Warning" => "WRN",
        "Error" => "ERR",
        "Fatal" => "FTL",
        _ => full,
    };

    // ── Filter ───────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        // Guard: called during InitializeComponent() before all XAML elements exist
        if (LogGrid is null) return;
        var level = (LevelFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "All";
        var component = (ComponentFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "All";
        var search = SearchBox.Text.Trim();

        var filtered = _allEntries.AsEnumerable();

        // Minimum-severity filter: "Debug+" shows Debug and everything above it
        if (level != "All")
        {
            var minRank = LevelRank(level.TrimEnd('+'));
            filtered = filtered.Where(e => LevelRank(e.Level) >= minRank);
        }

        if (component != "All")
            filtered = filtered.Where(e => e.Component == component);

        if (!string.IsNullOrEmpty(search))
            filtered = filtered.Where(e =>
                e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Component.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.RawLine.Contains(search, StringComparison.OrdinalIgnoreCase));

        var result = filtered.ToList();

        // Replacing the ItemsSource drops the selection and re-applies the column
        // widths declared in XAML — restore both so the details panel and any
        // user-dragged column width survive an auto-refresh.
        var selected = LogGrid.SelectedItem as LogEntry;
        var widths = LogGrid.Columns.Select(c => c.Width).ToList();

        LogGrid.ItemsSource = result;

        for (int i = 0; i < widths.Count; i++)
            LogGrid.Columns[i].Width = widths[i];

        if (selected is not null)
        {
            var restored = result.FirstOrDefault(e =>
                e.TimeLocal == selected.TimeLocal && e.RawLine == selected.RawLine);
            if (restored is not null)
                LogGrid.SelectedItem = restored;
        }

        EntryCountText.Text = $"({result.Count} entries)";

        if ((AutoScrollCheck.IsChecked == true) && result.Count > 0)
            LogGrid.ScrollIntoView(LogGrid.Items[0]);
    }

    private static int LevelRank(string level) => level switch
    {
        "Verbose" => 0,
        "Debug" => 1,
        "Information" => 2,
        "Warning" => 3,
        "Error" => 4,
        "Fatal" => 5,
        _ => 0,
    };

    // ── Event handlers ───────────────────────────────────────────────────────

    private void Filter_Changed(object sender, EventArgs e) => ApplyFilter();

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();      // TextChanged re-applies the (now empty) filter
        SearchBox.Focus();
    }

    private void AutoScroll_Changed(object sender, RoutedEventArgs e)
    {
        // Re-enabling auto-refresh catches up immediately instead of waiting for the next tick
        if (IsLoaded && AutoScrollCheck.IsChecked == true)
            LoadData();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadData();

    /// <summary>
    /// Closes the details panel. Dropping the selection is what hides it — keeping the
    /// row selected while the panel is gone would re-open it on the next refresh.
    /// </summary>
    private void LogDetailsClose_Click(object sender, RoutedEventArgs e) => LogGrid.UnselectAll();

    private void LogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogGrid.SelectedItem is not LogEntry row)
        {
            LogDetails.Visibility = Visibility.Collapsed;
            return;
        }

        LogDetails.Visibility = Visibility.Visible;
        DetailTimestamp.Text = Show(row.TimeLocal);
        DetailLevel.Text = Show(row.Level);
        DetailComponent.Text = Show(row.Component);

        // Body: message + any continuation lines (stack traces)
        var extra = row.RawLine;
        // Strip the first log line from RawLine to get only continuation lines
        var firstNewline = extra.IndexOf('\n');
        var continuation = firstNewline >= 0 ? extra[(firstNewline + 1)..].Trim() : "";
        DetailText.Text = string.IsNullOrEmpty(continuation)
            ? Show(row.Message)
            : row.Message + "\n\n" + continuation;
    }

    /// <summary>Empty fields become an em dash, so every row keeps its place in the raster.</summary>
    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "—" : value;

    /// <summary>Context-menu copy for the message body (stack traces are worth pasting).</summary>
    private void DetailCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: TextBlock target } }) return;
        if (string.IsNullOrEmpty(target.Text) || target.Text == "—") return;

        try { Clipboard.SetText(target.Text); }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process — never take down the page for it
            ConfigToolLog.ErrorOnChange("LogPage", ex, "Could not copy the log message to the clipboard");
        }
    }

    // ── Model ────────────────────────────────────────────────────────────────

    private record LogEntry(string TimeLocal, string Level, string Component, string Message, string RawLine)
    {
        public string LevelShort => ShortLevel(Level);
        public string LevelBg => Level switch
        {
            "Fatal" => "#FFC42B1C",
            "Error" => "#FFFDE7E9",
            "Warning" => "#FFFFF4CE",
            "Information" => "#FFDFF6DD",
            "Debug" => "#FFF0F0F0",
            _ => "#FFF0F0F0",
        };

        public string LevelFg => Level switch
        {
            "Fatal" => "#FFFFFFFF",
            "Error" => "#FFC42B1C",
            "Warning" => "#FF7A5700",
            "Information" => "#FF0F7B0F",
            "Debug" => "#FF616161",
            _ => "#FF616161",
        };
    }
}
