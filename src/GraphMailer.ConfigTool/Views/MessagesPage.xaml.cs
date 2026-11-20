using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// The Messages page: recent messages on the left, the selected one's details and timeline on
/// the right. The list combines the message history the service records in <c>metrics.db</c>
/// (every step since 1.6.0, kept for the metrics retention) with the mail folders (queue, failed,
/// sent), so messages from before the history existed still show up, with a reduced timeline.
/// Refreshes every 5 seconds while visible. Read-only; not part of the config load/save cycle.
/// </summary>
public partial class MessagesPage : UserControl
{
    // "All" first and preselected: the merged view is the useful default, the rest are drill-downs.
    private static readonly (string Label, MessageListFilter Filter)[] Filters =
    [
        ("All", MessageListFilter.All),
        ("Queue", MessageListFilter.Queue),
        ("Delayed", MessageListFilter.Delayed),
        ("Failed", MessageListFilter.Failed),
        ("Rejected", MessageListFilter.Rejected),
        ("Sent", MessageListFilter.Sent),
    ];

    // Live view of the relevant Mail Queue settings (MailDir, ArchiveSentEmails)
    private readonly Func<(string? MailDir, bool ArchiveSent)> _queueSettings;
    private readonly Func<string> _dataDir;
    // Live Monitoring setting: the service records message history only while metrics are on.
    private readonly Func<bool> _metricsEnabled;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _copyConfirm;
    private bool _loadInProgress;
    private int _limit = MailFolderReader.PageSize;
    private string _copyText = string.Empty;

    internal MessagesPage(
        Func<(string? MailDir, bool ArchiveSent)> queueSettings, Func<string> dataDir, Func<bool> metricsEnabled)
    {
        _queueSettings = queueSettings;
        _dataDir = dataDir;
        _metricsEnabled = metricsEnabled;

        // Built before InitializeComponent(): the search box raises TextChanged while the XAML
        // is being parsed, and the handler must not find a null timer.
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _limit = MailFolderReader.PageSize;   // a new search starts at the first page
            LoadData();
        };

        _copyConfirm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyConfirm.Tick += (_, _) => { _copyConfirm.Stop(); CopyConfirm.Visibility = Visibility.Collapsed; };

        InitializeComponent();

        FolderSelect.ItemsSource = Filters.Select(f => f.Label).ToList();
        FolderSelect.SelectedIndex = 0;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => LoadData();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { LoadData(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private string DbPath => Path.Combine(_dataDir(), "metrics.db");

    private void Folder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;   // ignore the initial selection during construction
        _limit = MailFolderReader.PageSize;   // a different filter starts at the first page
        LoadData();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        // Restart the window: only the last keystroke of a burst re-reads the data
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();      // TextChanged re-runs the (now empty) search
        SearchBox.Focus();
    }

    /// <summary>
    /// Pages in another block. The raised limit survives the 5 s auto-refresh and is reset only by
    /// a filter change or a new search term.
    /// </summary>
    private void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        _limit += MailFolderReader.PageSize;
        LoadData();
    }

    private async void LoadData()
    {
        // Prevent overlapping refreshes (a slow disk can outlast the 5 s interval)
        if (_loadInProgress) return;
        _loadInProgress = true;
        try
        {
            var (mailDir, archiveSent) = _queueSettings();
            var filter = Filters[Math.Max(0, FolderSelect.SelectedIndex)].Filter;
            var baseDir = string.IsNullOrEmpty(mailDir) ? AppPaths.MailDir : mailDir;
            var search = SearchBox.Text.Trim();
            var limit = _limit;
            var dbPath = DbPath;
            var metricsEnabled = _metricsEnabled();

            var (rows, total, hasMore, readError, dbExists) = await Task.Run(() =>
            {
                var traced = MessageTraceReader.ReadList(dbPath, filter, search, limit, out var traceError);
                // Messages from before 1.6.0 have no history, only their receipt/delivery metrics.
                // Without archived mail those rows are the only trace of them left.
                var legacy = MessageTraceReader.ReadMetricsList(dbPath, filter, search, limit, out var legacyError);
                // Folders are read whole anyway (see MailFolderReader); the merge pages the result.
                var folders = MailFolderReader.ReadFolders(int.MaxValue, null,
                    Path.Combine(baseDir, "queue"),
                    Path.Combine(baseDir, "failed"),
                    Path.Combine(baseDir, "sent")).Rows;
                var recorded = traced.Rows.Concat(legacy.Rows).ToList();
                var merged = MessageTimeline.Merge(recorded, traced.Total + legacy.Total, folders, filter, search, limit,
                    out var mergedTotal, out var mergedHasMore);
                return (merged, mergedTotal, mergedHasMore || traced.HasMore || legacy.HasMore,
                        traceError ?? legacyError, File.Exists(dbPath));
            });

            // Replacing the ItemsSource must not throw away user-resized column
            // widths or the current selection (the details panel depends on it).
            var widths = MessagesGrid.Columns.Select(c => c.Width).ToList();
            var selectedId = (MessagesGrid.SelectedItem as MessageRow)?.MessageId;

            MessagesGrid.ItemsSource = rows;

            for (int i = 0; i < widths.Count; i++)
                MessagesGrid.Columns[i].Width = widths[i];

            if (selectedId is not null)
            {
                var restored = rows.FirstOrDefault(r => r.MessageId == selectedId);
                if (restored is not null)
                    MessagesGrid.SelectedItem = restored;
            }

            CountText.Text = ListCountLabel.Build(rows.Count, total, "messages", search.Length > 0, hasMore);

            LoadMoreButton.Content = $"Load {MailFolderReader.PageSize:N0} more";
            LoadMoreButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;

            var hint = MessageListHint.Build(rows.Count, search.Length > 0, filter, archiveSent,
                metricsEnabled, dbExists, readError);
            EmptyHint.Text = hint ?? string.Empty;
            EmptyHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _loadInProgress = false;
        }
    }

    // ── Details panel ─────────────────────────────────────────────────────────

    /// <summary>
    /// Closes the details panel. Dropping the selection is what hides it — keeping the
    /// row selected while the panel is gone would re-open it on the next refresh.
    /// </summary>
    private void MessageDetailsClose_Click(object sender, RoutedEventArgs e) => MessagesGrid.UnselectAll();

    private async void MessagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesGrid.SelectedItem is not MessageRow row)
        {
            MessageDetails.Visibility = Visibility.Collapsed;
            return;
        }

        MessageDetails.Visibility = Visibility.Visible;

        DetailSubjectValue.Text = Show(row.Subject);
        DetailSubjectValue.ToolTip = row.Subject;
        DetailStatus.Text = row.StatusLabel;
        DetailStatus.Foreground = Brush(row.StatusFg);
        DetailStatusPill.Background = Brush(row.StatusBg);
        DetailReceived.Text = row.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss");
        DetailFrom.Text = Show(row.From);
        DetailTo.Text = Show(row.To);
        DetailSent.Text = Show(row.SentAt?.ToString("yyyy-MM-dd HH:mm:ss"));
        DetailClientIp.Text = Show(row.ClientIp);
        DetailAttempts.Text = Show(row.Attempts);
        DetailNextRetry.Text = Show(row.NextRetryAt?.ToString("yyyy-MM-dd HH:mm:ss"));
        DetailMessageId.Text = Show(row.SmtpMessageId);
        DetailQueueId.Text = Show(row.MessageId);
        DetailFile.Text = row.EmlPath ?? "Not stored (only the recorded facts are kept)";

        // The history is read on every selection change (also by the 5 s refresh restoring the
        // selection), so a message still being retried shows its newest attempt.
        var dbPath = DbPath;
        var messageId = row.MessageId;
        var (events, receipt) = await Task.Run(() =>
        {
            var ev = MessageTraceReader.ReadEvents(dbPath, messageId);
            return (ev, ev.Count == 0 ? MessageTraceReader.ReadReceipt(dbPath, messageId) : null);
        });

        // The user may have moved on while the read was running.
        if (!ReferenceEquals(MessagesGrid.SelectedItem, row)) return;

        var summary = MessageTimeline.Summarize(row, events);
        DetailSummary.Text = summary;
        _copyText = MessageTimeline.BuildCopyText(row, summary);
        HistoryHint.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TimelineList.ItemsSource = MessageTimeline.Build(row, events, receipt);
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_copyText)) return;
        try
        {
            Clipboard.SetText(_copyText);
            CopyConfirm.Visibility = Visibility.Visible;
            _copyConfirm.Stop();
            _copyConfirm.Start();
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process — never take down the page for it
            ConfigToolLog.ErrorOnChange("MessagesPage", ex, "Could not copy the message summary to the clipboard");
        }
    }

    private static SolidColorBrush Brush(string argb) => new((Color)ColorConverter.ConvertFromString(argb));

    /// <summary>Empty fields become an em dash, so every row keeps its place in the raster.</summary>
    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "—" : value;

    /// <summary>Context-menu copy, shared by the values worth pasting elsewhere.</summary>
    private void DetailCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: TextBlock target } }) return;
        if (string.IsNullOrEmpty(target.Text) || target.Text == "—") return;

        try { Clipboard.SetText(target.Text); }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process — never take down the page for it
            ConfigToolLog.ErrorOnChange("MessagesPage", ex, "Could not copy the message detail to the clipboard");
        }
    }
}
