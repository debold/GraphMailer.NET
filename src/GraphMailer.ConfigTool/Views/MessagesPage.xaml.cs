using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Read-only browser for the mail directories (queue / failed / sent).
/// Reads the *.meta.json files directly from disk; refreshes every 5 seconds
/// while visible. Retry details live in the details panel below the list, which
/// follows the selection. Not part of the config load/save cycle.
/// </summary>
public partial class MessagesPage : UserControl
{
    // "All" first and preselected: the merged view is the useful default, the single
    // folders are the drill-down.
    private static readonly string[] Folders = ["All", "Queue", "Failed", "Sent"];

    private const int AllIndex = 0;
    private const int SentIndex = 3;

    // Live view of the relevant Mail Queue settings (MailDir, ArchiveSentEmails)
    private readonly Func<(string? MailDir, bool ArchiveSent)> _queueSettings;
    private readonly DispatcherTimer _timer;
    private bool _loadInProgress;

    internal MessagesPage(Func<(string? MailDir, bool ArchiveSent)> queueSettings)
    {
        _queueSettings = queueSettings;
        InitializeComponent();

        FolderSelect.ItemsSource = Folders;
        FolderSelect.SelectedIndex = 0;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => LoadData();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { LoadData(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private void Folder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;   // ignore the initial selection during construction
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
            var folderIndex = FolderSelect.SelectedIndex;
            var baseDir = string.IsNullOrEmpty(mailDir) ? AppPaths.MailDir : mailDir;
            var isAll = folderIndex == AllIndex;

            // The status only tells the folders apart in the merged view
            StatusColumn.Visibility = isAll ? Visibility.Visible : Visibility.Collapsed;

            var rows = await Task.Run(() => isAll
                ? MailFolderReader.ReadFolders(
                    Path.Combine(baseDir, "queue"),
                    Path.Combine(baseDir, "failed"),
                    Path.Combine(baseDir, "sent"))
                : MailFolderReader.ReadFolder(Path.Combine(
                    baseDir,
                    folderIndex switch { 2 => "failed", SentIndex => "sent", _ => "queue" })));

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

            CountText.Text = rows.Count == MailFolderReader.MaxEntries
                ? $"newest {MailFolderReader.MaxEntries} messages"
                : $"{rows.Count} message(s)";

            if (folderIndex == SentIndex && rows.Count == 0 && !archiveSent)
            {
                EmptyHint.Text = "Sent archiving is disabled — delivered messages are deleted immediately. " +
                                 "Enable “Archive sent emails” on the Mail Queue page to keep them here. " +
                                 "The Metrics page shows the delivery history regardless of this setting.";
                EmptyHint.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyHint.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _loadInProgress = false;
        }
    }

    // ── Details panel (same pattern as the Metrics page's Activity tab) ───────

    /// <summary>
    /// Closes the details panel. Dropping the selection is what hides it — keeping the
    /// row selected while the panel is gone would re-open it on the next refresh.
    /// </summary>
    private void MessageDetailsClose_Click(object sender, RoutedEventArgs e) => MessagesGrid.UnselectAll();

    private void MessagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesGrid.SelectedItem is not MessageRow row)
        {
            MessageDetails.Visibility = Visibility.Collapsed;
            return;
        }

        MessageDetails.Visibility = Visibility.Visible;

        DetailSubjectValue.Text = Show(row.Subject);
        DetailReceived.Text = row.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss");
        DetailStatus.Text = Show(row.Status);
        DetailFrom.Text = Show(row.From);
        DetailTo.Text = Show(row.To);
        DetailSent.Text = Show(row.SentAt?.ToString("yyyy-MM-dd HH:mm:ss"));
        DetailClientIp.Text = Show(row.ClientIp);
        DetailAttempts.Text = Show(row.Attempts);
        DetailLastAttempt.Text = Show(row.LastAttemptAt?.ToString("yyyy-MM-dd HH:mm:ss"));
        DetailNextRetry.Text = Show(row.NextRetryAt?.ToString("yyyy-MM-dd HH:mm:ss"));
        DetailMessageId.Text = Show(row.SmtpMessageId);
        DetailQueueId.Text = Show(row.MessageId);
        DetailLastError.Text = Show(row.LastError);
    }

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
