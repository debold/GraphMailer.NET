using System.Collections.ObjectModel;
using System.Net.Mail;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.ConfigTool.Views;

public partial class NotificationsPage : UserControl
{
    private readonly Action _markDirty;
    private readonly Func<bool>? _isBackupEmailEnabled;
    private readonly ObservableCollection<PatternRow> _adminRecipients = [];

    // XAML parsing fires Checked/Unchecked handlers for checkboxes with IsChecked set in
    // markup — while later-declared named elements do not exist yet. Suppress the
    // cross-field validation until construction is complete; LoadFrom runs it explicitly.
    private bool _uiReady;

    public NotificationsPage(Action markDirty, Func<bool>? isBackupEmailEnabled = null)
    {
        _markDirty = markDirty;
        _isBackupEmailEnabled = isBackupEmailEnabled;
        InitializeComponent();
        RecipientsGrid.ItemsSource = _adminRecipients;
        _uiReady = true;

        // Re-evaluate when the page becomes visible: the backup-email toggle (a
        // dependency of the sender) lives on another page and may have changed.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) UpdateSenderError(); };
    }

    /// <summary>Live sender state for cross-page validation (used by the Backup page).</summary>
    internal bool HasSenderAddress => !string.IsNullOrWhiteSpace(NotifFrom.Text);

    internal void LoadFrom(ConfigDocument doc)
    {
        _adminRecipients.Clear();
        foreach (var addr in doc.Notification.RecipientAddresses)
            _adminRecipients.Add(new PatternRow { Pattern = addr });
        NotifFrom.Text = doc.Notification.NotifFrom ?? "";
        ValidateEmailField(NotifFrom);
        SubjectPrefix.Text = doc.Notification.SubjectPrefix;
        NotifIpBlocked.IsChecked = doc.Notification.NotifIpBlocked;
        NotifDeliveryFailed.IsChecked = doc.Notification.NotifDeliveryFailed;
        NotifCertExpiring.IsChecked = doc.Notification.NotifCertExpiring;
        NotifCertExpired.IsChecked = doc.Notification.NotifCertExpired;
        NotifDiskSpace.IsChecked = doc.Notification.NotifDiskSpace;
        NotifGraphDown.IsChecked = doc.Notification.NotifGraphDown;
        NotifPortDown.IsChecked = doc.Notification.NotifPortDown;
        NotifServiceStartStop.IsChecked = doc.Notification.NotifServiceStartStop;
        NotifBackup.IsChecked = doc.Notification.NotifBackup;
        NdrEnabled.IsChecked = doc.Ndr.NdrEnabled;
        NdrNotifySender.IsChecked = doc.Ndr.NdrNotifySender;
        NdrNotifyAdmin.IsChecked = doc.Ndr.NdrNotifyAdmin;

        ReportEnabled.IsChecked = doc.Notification.ReportEnabled;
        SelectCombo(ReportFrequencyBox, doc.Notification.ReportFrequency);
        SelectCombo(ReportDayOfWeekBox, doc.Notification.ReportDayOfWeek);
        ReportDayOfMonthBox.Text = doc.Notification.ReportDayOfMonth.ToString();
        var (hh, mm) = ParseTime(doc.Notification.ReportTimeOfDay);
        ReportHourBox.Text = hh.ToString("00");
        ReportMinuteBox.Text = mm.ToString("00");
        UpdateReportEnabled();
        UpdateSenderError();
    }

    /// <summary>
    /// Live view of <see cref="SenderAddressRule"/>: recipients/NDR/report from this
    /// page, the emailed-backups toggle queried live from the Backup page. The same
    /// rule additionally gates Save over the fully collected configuration.
    /// </summary>
    private void UpdateSenderError()
    {
        if (!_uiReady) return;   // XAML init: handlers can fire before all elements exist

        var error = SenderAddressRule.Validate(
            NotifFrom.Text,
            hasRecipients: _adminRecipients.Count > 0,
            ndrEnabled: NdrEnabled.IsChecked == true,
            reportEnabled: ReportEnabled.IsChecked == true,
            backupEmailEnabled: _isBackupEmailEnabled?.Invoke() == true);

        SenderError.Text = error ?? "";
        SenderError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void CollectTo(ConfigDocument doc)
    {
        doc.Notification.RecipientAddresses = _adminRecipients.Select(r => r.Pattern).ToList();
        doc.Notification.NotifFrom = string.IsNullOrWhiteSpace(NotifFrom.Text) ? null : NotifFrom.Text.Trim();
        doc.Notification.SubjectPrefix = SubjectPrefix.Text;
        doc.Notification.NotifIpBlocked = NotifIpBlocked.IsChecked == true;
        doc.Notification.NotifDeliveryFailed = NotifDeliveryFailed.IsChecked == true;
        doc.Notification.NotifCertExpiring = NotifCertExpiring.IsChecked == true;
        doc.Notification.NotifCertExpired = NotifCertExpired.IsChecked == true;
        doc.Notification.NotifDiskSpace = NotifDiskSpace.IsChecked == true;
        doc.Notification.NotifGraphDown = NotifGraphDown.IsChecked == true;
        doc.Notification.NotifPortDown = NotifPortDown.IsChecked == true;
        doc.Notification.NotifServiceStartStop = NotifServiceStartStop.IsChecked == true;
        doc.Notification.NotifBackup = NotifBackup.IsChecked == true;
        doc.Ndr.NdrEnabled = NdrEnabled.IsChecked == true;
        doc.Ndr.NdrNotifySender = NdrNotifySender.IsChecked == true;
        doc.Ndr.NdrNotifyAdmin = NdrNotifyAdmin.IsChecked == true;

        doc.Notification.ReportEnabled = ReportEnabled.IsChecked == true;
        doc.Notification.ReportFrequency = ComboText(ReportFrequencyBox);
        doc.Notification.ReportDayOfWeek = ComboText(ReportDayOfWeekBox);
        doc.Notification.ReportDayOfMonth = ClampInt(ReportDayOfMonthBox.Text, 1, 28, 1);
        doc.Notification.ReportTimeOfDay = $"{ClampInt(ReportHourBox.Text, 0, 23, 7):00}:{ClampInt(ReportMinuteBox.Text, 0, 59, 0):00}";
    }

    private void AnyField_Changed(object sender, TextChangedEventArgs e) => _markDirty();

    private void AnyCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSenderError();   // NDR toggles affect whether the sender is required
        _markDirty();
    }

    private void AnyCombo_Changed(object sender, SelectionChangedEventArgs e) => _markDirty();

    // ── Periodic report enable / frequency state ──────────────────────────

    private void ReportEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateReportEnabled();
        UpdateSenderError();
        _markDirty();
    }

    private void ReportFrequency_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateReportDayEnabled();
        _markDirty();
    }

    private void UpdateReportEnabled()
    {
        var on = ReportEnabled.IsChecked == true;
        ReportFields.IsEnabled = on;
        ReportFields.Opacity = on ? 1.0 : 0.5;
        UpdateReportDayEnabled();
    }

    private void UpdateReportDayEnabled()
    {
        var monthly = ComboText(ReportFrequencyBox).Equals("Monthly", StringComparison.OrdinalIgnoreCase);
        ReportDayOfWeekBox.IsEnabled = !monthly;
        ReportDayOfWeekBox.Opacity = monthly ? 0.5 : 1.0;
        ReportDayOfWeekLabel.Opacity = monthly ? 0.5 : 1.0;
        ReportDayOfMonthBox.IsEnabled = monthly;
        ReportDayOfMonthBox.Opacity = monthly ? 1.0 : 0.5;
        ReportDayOfMonthLabel.Opacity = monthly ? 1.0 : 0.5;
    }

    // ── Combo / time helpers ──────────────────────────────────────────────

    private static string ComboText(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private static void SelectCombo(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static (int Hour, int Minute) ParseTime(string? hhmm)
        => TimeOnly.TryParse(hhmm, out var t) ? (t.Hour, t.Minute) : (7, 0);

    private static int ClampInt(string text, int min, int max, int fallback)
        => int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;

    private void EmailField_Changed(object sender, TextChangedEventArgs e)
    {
        _markDirty();
        ValidateEmailField((TextBox)sender);
        UpdateSenderError();
    }

    private static void ValidateEmailField(TextBox tb)
    {
        var text = tb.Text.Trim();
        var valid = string.IsNullOrEmpty(text) || MailAddress.TryCreate(text, out _);
        if (valid)
            tb.ClearValue(TextBox.BorderBrushProperty);
        else
            tb.BorderBrush = Brushes.Red;
    }

    // ── Admin Recipients ──────────────────────────────────────────────────

    private void AddRecipient_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PatternDialog(
            title: "Add Admin Recipient",
            description: "Enter a valid email address.",
            initialValue: "",
            validate: v =>
            {
                var t = v.Trim();
                if (!EmailValidation.IsValidRecipient(t))
                    return "Enter a valid email address (e.g. admin@company.com).";
                if (_adminRecipients.Any(r => r.Pattern.Equals(t, StringComparison.OrdinalIgnoreCase)))
                    return "This address is already in the list.";
                return null;
            })
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            _adminRecipients.Add(new PatternRow { Pattern = dlg.Result });
            UpdateSenderError();   // recipients present ⇒ a sender becomes required
            _markDirty();
        }
    }

    private void EditRecipient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PatternRow row) return;

        var dlg = new PatternDialog(
            title: "Edit Admin Recipient",
            description: "Enter a valid email address.",
            initialValue: row.Pattern,
            validate: v =>
            {
                var t = v.Trim();
                if (!EmailValidation.IsValidRecipient(t))
                    return "Enter a valid email address (e.g. admin@company.com).";
                if (_adminRecipients.Where(r => r != row).Any(r => r.Pattern.Equals(t, StringComparison.OrdinalIgnoreCase)))
                    return "This address is already in the list.";
                return null;
            })
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            row.Pattern = dlg.Result;
            _markDirty();
        }
    }

    private void RemoveRecipient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PatternRow row)
        { _adminRecipients.Remove(row); UpdateSenderError(); _markDirty(); }
    }
}
