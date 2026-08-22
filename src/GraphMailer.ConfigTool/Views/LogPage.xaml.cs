using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.ConfigTool.Views;

public partial class LogPage : UserControl
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _searchDebounce;
    private bool _loadInProgress;

    private LogReadResult _result = new([], [], 0, false, false);
    private bool _filtered;
    private int _logLimit = LogFileReader.PageSize;

    /// <summary>
    /// Puts an address from a log entry on the IP whitelist (<c>false</c>) or blacklist
    /// (<c>true</c>). Supplied by the main window, which owns the IP filtering page — this page
    /// reads log files and has no business touching configuration itself.
    /// </summary>
    private readonly Action<string, bool>? _addIpToFilter;

    public LogPage(Action<string, bool>? addIpToFilter = null)
    {
        _addIpToFilter = addIpToFilter;
        // Built before InitializeComponent(): the search box raises TextChanged while the XAML
        // is being parsed, and the handler must not find a null timer.
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _logLimit = LogFileReader.PageSize;   // a new search starts at the first page
            LoadData();
        };

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
        // The level filter carries SelectedIndex="0" *and* SelectionChanged in XAML, so it raises
        // Filter_Changed while InitializeComponent() is still running — at which point the controls
        // declared after it (ComponentFilter, SearchBox, the grid) are null. IsVisible additionally
        // keeps the constructor's own SelectedIndex assignment from reading seven log files before
        // the page has ever been shown.
        if (!IsInitialized || !IsVisible) return;

        if (_loadInProgress) return;
        _loadInProgress = true;
        try { await LoadDataAsync(); }
        finally { _loadInProgress = false; }
    }

    private async Task LoadDataAsync()
    {
        // The filter is applied while reading rather than afterwards: it is the only way a search
        // can reach entries outside the loaded page, which is the whole point of having one.
        var level = (LevelFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "All";
        var component = (ComponentFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "All";
        var search = SearchBox.Text.Trim();
        var predicate = BuildPredicate(level, component, search);
        var limit = _logLimit;

        var result = await Task.Run(() => LogFileReader.Read(AppPaths.LogsDir, limit, predicate));

        // Preserve selected component filter across refreshes
        var prevComponent = component;

        // The dropdown lists every component *seen*, not just the matching ones — building it from
        // the filtered result would leave "All" and the current pick as the only choices.
        ComponentFilter.SelectionChanged -= Filter_Changed;
        ComponentFilter.Items.Clear();
        ComponentFilter.Items.Add(new ComboBoxItem { Content = "All" });
        foreach (var c in result.Components)
            ComponentFilter.Items.Add(new ComboBoxItem { Content = c });

        // A component filtered down to nothing must stay selectable, or the user cannot undo it
        if (prevComponent != "All" && !result.Components.Contains(prevComponent))
            ComponentFilter.Items.Add(new ComboBoxItem { Content = prevComponent });

        // Restore or default to "All"
        var match = ComponentFilter.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Content == prevComponent);
        ComponentFilter.SelectedItem = match ?? ComponentFilter.Items[0];
        ComponentFilter.SelectionChanged += Filter_Changed;

        _result = result;
        _filtered = predicate is not null;
        ShowEntries();
    }

    /// <summary>
    /// Combines the three filter controls into one predicate, or <c>null</c> when none of them
    /// narrows anything — a null predicate lets the reader stop at the page limit instead of
    /// walking the whole log.
    /// </summary>
    private static Func<LogEntry, bool>? BuildPredicate(string level, string component, string search)
    {
        var minRank = level == "All" ? -1 : LogFileReader.LevelRank(level.TrimEnd('+'));
        var byComponent = component != "All";
        var bySearch = search.Length > 0;

        if (minRank < 0 && !byComponent && !bySearch) return null;

        return e =>
            (minRank < 0 || LogFileReader.LevelRank(e.Level) >= minRank)
            && (!byComponent || e.Component == component)
            && (!bySearch || e.Matches(search));
    }

    /// <summary>
    /// Pushes the loaded entries into the grid. Replacing the ItemsSource drops the selection and
    /// re-applies the column widths declared in XAML — restore both so an auto-refresh does not
    /// disturb the user (same pattern as the Messages page and the Metrics Activity tab).
    /// </summary>
    private void ShowEntries()
    {
        // Guard: the search debounce can fire before the grid exists
        if (LogGrid is null) return;

        var result = _result.Entries;

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

        // A filtered read reports what it looked at; an unfiltered one stops at the page limit
        // and genuinely does not know how much log lies behind it, so it names no total.
        EntryCountText.Text = ListCountLabel.Build(
            result.Count,
            _filtered ? _result.Scanned : null,
            "entries",
            _filtered,
            _result.HasMore,
            _result.ScanCapped ? $"stopped after {LogFileReader.MaxScan:N0} entries" : null);

        LoadMoreButton.Content = $"Load {LogFileReader.PageSize:N0} more";
        LoadMoreButton.Visibility = _result.HasMore ? Visibility.Visible : Visibility.Collapsed;

        if ((AutoScrollCheck.IsChecked == true) && result.Count > 0)
            LogGrid.ScrollIntoView(LogGrid.Items[0]);
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    /// <summary>Level and component are dropdowns — a change there reloads straight away.</summary>
    private void Filter_Changed(object sender, EventArgs e)
    {
        _logLimit = LogFileReader.PageSize;   // a different filter starts at the first page
        LoadData();
    }

    /// <summary>The search reads files, so it must not fire on every keystroke.</summary>
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>
    /// Pages in another block. The raised limit survives the auto-refresh and is reset only by a
    /// filter change.
    /// </summary>
    private void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        _logLimit += LogFileReader.PageSize;
        LoadData();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();      // TextChanged re-runs the (now empty) search
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

    // ── IP context menu ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the menu for the entry under the cursor: copying the entry, and the addresses it
    /// mentions, so a rejection or a blocked-IP warning can go straight onto a filter list instead
    /// of being copied across two pages by hand. Rebuilt on every open because everything but the
    /// copy item depends entirely on that one row.
    /// </summary>
    private void LogGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = LogGrid.ContextMenu;
        if (menu is null) return;

        menu.Items.Clear();

        var entry = EntryUnderCursor(e.OriginalSource as DependencyObject);
        if (entry is null)
        {
            e.Handled = true;   // header or empty space — no menu at all
            return;
        }

        // Right-click acts on what it points at, which WPF does not select on its own. Without
        // this the menu could offer one row's address while another row stays highlighted.
        LogGrid.SelectedItem = entry;

        // The whole entry, not the message alone: RawLine carries the timestamp, the level and any
        // stack trace, which is what makes a pasted line useful in a ticket or a mail.
        var copy = new MenuItem { Header = "Copy entry" };
        copy.Click += (_, _) => CopyToClipboard(entry.RawLine);
        menu.Items.Add(copy);

        var addresses = _addIpToFilter is null ? [] : LogIpExtractor.Extract(entry.Message);
        if (addresses.Count == 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "No IP address in this entry", IsEnabled = false });
            return;
        }

        foreach (var ip in addresses)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(FilterItem($"Add {ip} to whitelist…", ip, blacklist: false));
            menu.Items.Add(FilterItem($"Add {ip} to blacklist…", ip, blacklist: true));
        }
    }

    private MenuItem FilterItem(string header, string ipAddress, bool blacklist)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => _addIpToFilter!(ipAddress, blacklist);
        return item;
    }

    /// <summary>
    /// Walks up from whatever was right-clicked to the row that holds it. The logical-tree
    /// fallback covers sources that are not visuals (a <c>Run</c> inside a cell's text), where
    /// <see cref="VisualTreeHelper.GetParent"/> throws rather than returning null.
    /// </summary>
    private static LogEntry? EntryUnderCursor(DependencyObject? source)
    {
        while (source is not null and not DataGridRow)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return (source as DataGridRow)?.Item as LogEntry;
    }

    /// <summary>Context-menu copy for the message body (stack traces are worth pasting).</summary>
    private void DetailCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: TextBlock target } }) return;
        if (string.IsNullOrEmpty(target.Text) || target.Text == "—") return;

        CopyToClipboard(target.Text);
    }

    /// <summary>
    /// Copies without letting a clipboard failure reach the user as a crash: the clipboard is a
    /// shared OS resource and another process can hold it locked for a moment.
    /// </summary>
    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try { Clipboard.SetText(text); }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("LogPage", ex, "Could not copy to the clipboard");
        }
    }

    // The LogEntry model and the file parsing live in Services/LogFileReader.cs, so the reader
    // can be tested without a WPF page.
}
