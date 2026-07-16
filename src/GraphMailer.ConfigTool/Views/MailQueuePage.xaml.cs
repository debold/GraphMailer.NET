using System.IO;
using System.Windows;
using System.Windows.Controls;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Services;
using Microsoft.Win32;

namespace GraphMailer.ConfigTool.Views;

public partial class MailQueuePage : UserControl
{
    private readonly Action _markDirty;

    public MailQueuePage(Action markDirty)
    {
        _markDirty = markDirty;
        InitializeComponent();
        QueueDir.Tag = AppPaths.MailDir;
    }

    internal void LoadFrom(ConfigDocument doc)
    {
        PollingInterval.Text = doc.MailQueue.PollingIntervalSeconds.ToString();
        TransientCount.Text = doc.MailQueue.TransientRetryCount.ToString();
        TransientInterval.Text = doc.MailQueue.TransientRetryIntervalSeconds.ToString();
        SteadyInterval.Text = doc.MailQueue.RetryIntervalSeconds.ToString();
        ExpirationHours.Text = doc.MailQueue.MessageExpirationHours.ToString();
        ArchivingEnabled.IsChecked = doc.MailQueue.ArchiveSentEmails;
        RetentionDays.Text = doc.MailQueue.SentEmailRetentionDays.ToString();
        FailedRetentionDays.Text = doc.MailQueue.FailedEmailRetentionDays.ToString();
        // QueueDir stores the configurable mail base directory; SentDir/FailedDir are derived.
        QueueDir.Text = doc.MailQueue.MailDir;
        UpdateDerivedDirs();
        UpdateRetrySchedule();
    }

    internal void CollectTo(ConfigDocument doc)
    {
        doc.MailQueue.PollingIntervalSeconds = int.TryParse(PollingInterval.Text, out var pi) ? pi : 5;
        doc.MailQueue.TransientRetryCount = int.TryParse(TransientCount.Text, out var tc) ? tc : 6;
        doc.MailQueue.TransientRetryIntervalSeconds = int.TryParse(TransientInterval.Text, out var ti) ? ti : 300;
        doc.MailQueue.RetryIntervalSeconds = int.TryParse(SteadyInterval.Text, out var si) ? si : 900;
        doc.MailQueue.MessageExpirationHours = int.TryParse(ExpirationHours.Text, out var eh) ? eh : 24;
        doc.MailQueue.ArchiveSentEmails = ArchivingEnabled.IsChecked == true;
        doc.MailQueue.SentEmailRetentionDays = int.TryParse(RetentionDays.Text, out var rt) ? rt : 7;
        doc.MailQueue.FailedEmailRetentionDays = int.TryParse(FailedRetentionDays.Text, out var frt) ? frt : 60;
        doc.MailQueue.MailDir = QueueDir.Text.Trim();
    }

    private void AnyField_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateRetrySchedule();
        _markDirty();
    }

    private void AnyCheckbox_Changed(object sender, RoutedEventArgs e) => _markDirty();

    /// <summary>Recomputes the human-readable retry-delay preview from the current inputs.</summary>
    private void UpdateRetrySchedule()
    {
        if (RetryScheduleText is null) return; // called before InitializeComponent completes

        var opts = new GraphMailer.Service.Configuration.MailQueueOptions
        {
            TransientRetryCount = int.TryParse(TransientCount.Text, out var tc) ? tc : 6,
            TransientRetryIntervalSeconds = int.TryParse(TransientInterval.Text, out var ti) ? ti : 300,
            RetryIntervalSeconds = int.TryParse(SteadyInterval.Text, out var si) ? si : 900,
            MessageExpirationHours = int.TryParse(ExpirationHours.Text, out var eh) ? eh : 24,
        };
        RetryScheduleText.Text = RetrySchedule.Describe(opts);
    }

    private void QueueDir_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateDerivedDirs();
        _markDirty();
    }

    private void UpdateDerivedDirs()
    {
        var mailDir = string.IsNullOrWhiteSpace(QueueDir.Text)
            ? AppPaths.MailDir
            : QueueDir.Text.Trim();
        QueueSubDir.Text = Path.Combine(mailDir, "queue");
        SentDir.Text = Path.Combine(mailDir, "sent");
        FailedDir.Text = Path.Combine(mailDir, "failed");
    }

    // ── Folder pickers ───────────────────────────────────────────────────────────

    private void BrowseQueueDir_Click(object sender, RoutedEventArgs e)
        => BrowseFolder("Select mail base directory", QueueDir, AppPaths.MailDir);

    private void ClearQueueDir_Click(object sender, RoutedEventArgs e)
        => Clear(QueueDir);

    private void BrowseFolder(string title, TextBox target, string initialDirectory)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        var current = target.Text.Trim();
        dlg.InitialDirectory = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
            ? current
            : FindExistingAncestor(initialDirectory);
        if (dlg.ShowDialog() == true)
        { target.Text = dlg.FolderName; _markDirty(); }
    }

    /// <summary>Returns the deepest ancestor of <paramref name="path"/> that actually exists on disk.</summary>
    private static string FindExistingAncestor(string path)
    {
        var dir = path;
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir);
        return string.IsNullOrEmpty(dir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : dir;
    }

    private void Clear(TextBox target)
    { target.Text = ""; _markDirty(); }
}
