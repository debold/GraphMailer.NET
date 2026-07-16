using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Read-only browser for the mail directories (queue / failed / sent).
/// Reads the *.meta.json files directly from disk; refreshes every 5 seconds
/// while visible. Retry details live in the collapsible details panel
/// (same pattern as the log viewer). Not part of the config load/save cycle.
/// </summary>
public partial class MessagesPage : UserControl
{
    private static readonly string[] Folders = ["Queue", "Failed", "Sent"];

    // Live view of the relevant Mail Queue settings (MailDir, ArchiveSentEmails)
    private readonly Func<(string? MailDir, bool ArchiveSent)> _queueSettings;
    private readonly DispatcherTimer _timer;
    private bool _loadInProgress;
    private bool _detailsVisible;

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

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadData();

    private async void LoadData()
    {
        // Prevent overlapping refreshes (a slow disk can outlast the 5 s interval)
        if (_loadInProgress) return;
        _loadInProgress = true;
        try
        {
            var (mailDir, archiveSent) = _queueSettings();
            var folderIndex = FolderSelect.SelectedIndex;
            var folderName = folderIndex switch { 1 => "failed", 2 => "sent", _ => "queue" };
            var baseDir = string.IsNullOrEmpty(mailDir) ? AppPaths.MailDir : mailDir;
            var directory = Path.Combine(baseDir, folderName);

            var rows = await Task.Run(() => MailFolderReader.ReadFolder(directory));

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

            bool isSent = folderIndex == 2;
            if (isSent && rows.Count == 0 && !archiveSent)
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

    // ── Details panel (same pattern as the log viewer) ────────────────────────

    private void BtnToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        _detailsVisible = !_detailsVisible;
        SplitterRow.Height = _detailsVisible ? new GridLength(5) : new GridLength(0);
        DetailsRow.Height = _detailsVisible ? new GridLength(190) : new GridLength(0);
        BtnToggleDetails.Content = _detailsVisible ? "▲ Details" : "▼ Details";
    }

    private void MessagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesGrid.SelectedItem is not MessageRow row)
        {
            DetailText.Text = "Select a message to see details.";
            DetailSubject.Visibility = Visibility.Collapsed;
            return;
        }

        // Auto-expand details panel on first selection
        if (!_detailsVisible)
            BtnToggleDetails_Click(this, null!);

        DetailSubject.Text = row.Subject;
        DetailSubject.Visibility = string.IsNullOrEmpty(row.Subject)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var sb = new StringBuilder();
        Append(sb, "Received", row.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        Append(sb, "From", row.From);
        Append(sb, "To", row.To);
        Append(sb, "Subject", row.Subject);
        Append(sb, "Status", row.Status);
        Append(sb, "Sent", row.SentAt?.ToString("yyyy-MM-dd HH:mm:ss"));
        Append(sb, "Client IP", row.ClientIp);
        Append(sb, "Attempts", row.Attempts);
        Append(sb, "Last attempt", row.LastAttemptAt?.ToString("yyyy-MM-dd HH:mm:ss"));
        Append(sb, "Last error", row.LastError);
        Append(sb, "Next retry", row.NextRetryAt?.ToString("yyyy-MM-dd HH:mm:ss"));
        Append(sb, "Message-ID", row.SmtpMessageId);
        Append(sb, "Queue ID", row.MessageId);
        DetailText.Text = sb.ToString().TrimEnd();
    }

    private static void Append(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            sb.Append(label.PadRight(14)).AppendLine(value);
    }
}
