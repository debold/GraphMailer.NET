using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Backup;
using GraphMailer.Service.Infrastructure.Config;
using Microsoft.Win32;

namespace GraphMailer.ConfigTool.Views;

public partial class BackupPage : UserControl
{
    private const int MinKeep = 1;
    private const int MaxKeep = 365;
    private const int MinPasswordLength = 8;

    private readonly Action _markDirty;
    private readonly IConfigBackupService _backup;
    private readonly Action _onConfigRestored;
    private readonly Func<bool>? _notificationSenderConfigured;
    private readonly ObservableCollection<PatternRow> _recipients = [];

    private bool _passwordError;
    private bool _emailError;

    internal BackupPage(
        Action markDirty, IConfigBackupService backup, Action onConfigRestored,
        Func<bool>? notificationSenderConfigured = null)
    {
        _markDirty = markDirty;
        _backup = backup;
        _onConfigRestored = onConfigRestored;
        _notificationSenderConfigured = notificationSenderConfigured;
        InitializeComponent();
        RecipientsGrid.ItemsSource = _recipients;

        // Re-evaluate when the page becomes visible: the sender address (a dependency
        // of emailed backups) lives on the Notifications page and may have changed.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) ValidateEmail(); };
    }

    /// <summary>Live toggle state for cross-page validation (used by the Notifications page).</summary>
    internal bool IsEmailBackupEnabled => EmailEnabledToggle.IsChecked == true;

    /// <summary>True when the page has a blocking validation error (password rule or email backups without recipients).</summary>
    internal bool HasValidationErrors => _passwordError || _emailError;

    // ── Load / Collect ────────────────────────────────────────────────────

    internal void LoadFrom(ConfigDocument doc)
    {
        var b = doc.Backup;
        EnabledToggle.IsChecked = b.BackupEnabled;
        SelectCombo(FrequencyBox, b.Frequency);
        SelectCombo(DayOfWeekBox, b.DayOfWeek);

        var (hh, mm) = ParseTime(b.TimeOfDay);
        HourBox.Text = hh.ToString("00");
        MinuteBox.Text = mm.ToString("00");

        MaxBackupsBox.Text = b.MaxBackups.ToString();
        DirectoryBox.Text = string.IsNullOrWhiteSpace(b.Directory) ? AppPaths.BackupsDir : b.Directory!;

        PasswordBoxCtl.Password = b.Password ?? "";
        PasswordConfirmCtl.Password = b.Password ?? "";

        EmailEnabledToggle.IsChecked = b.EmailEnabled;
        _recipients.Clear();
        foreach (var addr in b.EmailRecipients)
            _recipients.Add(new PatternRow { Pattern = addr });

        UpdateScheduleEnabled();
        ValidatePassword();
        ValidateEmail();
    }

    internal void CollectTo(ConfigDocument doc)
    {
        var b = doc.Backup;
        b.BackupEnabled = EnabledToggle.IsChecked == true;
        b.Frequency = ComboText(FrequencyBox);
        b.DayOfWeek = ComboText(DayOfWeekBox);
        b.TimeOfDay = $"{ClampInt(HourBox.Text, 0, 23, 3):00}:{ClampInt(MinuteBox.Text, 0, 59, 0):00}";
        b.MaxBackups = ClampInt(MaxBackupsBox.Text, MinKeep, MaxKeep, 14);

        var dir = DirectoryBox.Text.Trim();
        b.Directory = string.IsNullOrEmpty(dir) || PathsEqual(dir, AppPaths.BackupsDir) ? null : dir;

        b.Password = string.IsNullOrEmpty(PasswordBoxCtl.Password) ? null : PasswordBoxCtl.Password;
        b.EmailEnabled = EmailEnabledToggle.IsChecked == true;
        b.EmailRecipients = _recipients.Select(r => r.Pattern).ToList();
    }

    // ── Enable/disable + greying ──────────────────────────────────────────

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateScheduleEnabled();
        ValidatePassword();   // enabling the schedule makes a missing password an error
        _markDirty();
    }

    private void Frequency_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateDayEnabled();
        _markDirty();
    }

    private void UpdateScheduleEnabled()
    {
        var on = EnabledToggle.IsChecked == true;
        ScheduleFields.IsEnabled = on;
        ScheduleFields.Opacity = on ? 1.0 : 0.5;
        UpdateDayEnabled();
    }

    private void UpdateDayEnabled()
    {
        var weekly = ComboText(FrequencyBox).Equals("Weekly", StringComparison.OrdinalIgnoreCase);
        DayOfWeekBox.IsEnabled = weekly;
        DayOfWeekBox.Opacity = weekly ? 1.0 : 0.5;
        DayOfWeekLabel.Opacity = weekly ? 1.0 : 0.5;
    }

    // ── Keep-last stepper ─────────────────────────────────────────────────

    private void MaxUp_Click(object sender, RoutedEventArgs e) => StepMax(+1);
    private void MaxDown_Click(object sender, RoutedEventArgs e) => StepMax(-1);

    private void StepMax(int delta)
    {
        var value = ClampInt(MaxBackupsBox.Text, MinKeep, MaxKeep, 14);
        MaxBackupsBox.Text = Math.Clamp(value + delta, MinKeep, MaxKeep).ToString();
        _markDirty();
    }

    // ── Directory picker ──────────────────────────────────────────────────

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var current = DirectoryBox.Text.Trim();
        var dialog = new OpenFolderDialog
        {
            Title = "Select backup directory",
            InitialDirectory = Directory.Exists(current) ? current : AppPaths.BackupsDir,
        };
        if (dialog.ShowDialog() == true)
        {
            DirectoryBox.Text = dialog.FolderName;
            _markDirty();
        }
    }

    // ── Password ──────────────────────────────────────────────────────────

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        ValidatePassword();
        _markDirty();
    }

    private void ValidatePassword()
    {
        var error = ValidatePasswordRule(
            EnabledToggle.IsChecked == true, PasswordBoxCtl.Password, PasswordConfirmCtl.Password);

        _passwordError = error is not null;
        PasswordError.Text = error ?? "";
        PasswordError.Visibility = _passwordError ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Validation rule for the backup password (static for unit testing).
    /// An ENABLED schedule without a password is an error: the service pauses
    /// scheduled backups in that state with only a log warning — without this
    /// rule the operator could save a schedule that silently never runs.
    /// </summary>
    internal static string? ValidatePasswordRule(bool scheduleEnabled, string password, string confirm)
    {
        if (password.Length > 0 || confirm.Length > 0)
        {
            if (password.Length < MinPasswordLength)
                return $"Backup password must be at least {MinPasswordLength} characters.";
            if (password != confirm)
                return "Passwords do not match.";
            return null;
        }

        return scheduleEnabled
            ? "A password is required for scheduled backups — without one the service pauses the schedule and no backups are created."
            : null;
    }

    // ── Field events ──────────────────────────────────────────────────────

    private void AnyField_Changed(object sender, TextChangedEventArgs e) => _markDirty();
    private void AnyCheckbox_Changed(object sender, RoutedEventArgs e) => _markDirty();
    private void AnyCombo_Changed(object sender, SelectionChangedEventArgs e) => _markDirty();

    // ── Manual create ─────────────────────────────────────────────────────

    private void CreateNow_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBoxCtl.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowFeedback("Set a backup password first (it encrypts the backup file).", isError: true);
            return;
        }
        if (_passwordError)
        {
            ShowFeedback("Fix the backup password (length / confirmation) before creating a backup.", isError: true);
            return;
        }

        var directory = string.IsNullOrWhiteSpace(DirectoryBox.Text) ? AppPaths.BackupsDir : DirectoryBox.Text.Trim();
        try
        {
            var path = _backup.WriteBackup(password, directory);
            if (!File.Exists(path))
            {
                ShowFeedback($"Backup reported success but the file is missing: {path}", isError: true);
                return;
            }
            if (int.TryParse(MaxBackupsBox.Text, out var max) && max > 0)
                _backup.Rotate(directory, max);

            var size = new FileInfo(path).Length;
            ShowFeedback($"Backup created: {path} ({size:N0} bytes)", isError: false);
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("BackupPage", ex, "Backup failed");
            ShowFeedback($"Backup failed: {ex.Message}", isError: true);
        }
    }

    // ── Restore ───────────────────────────────────────────────────────────

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        // Default to the backups directory; create it so the dialog opens there.
        try { Directory.CreateDirectory(AppPaths.BackupsDir); } catch { /* picker falls back gracefully */ }

        var dialog = new OpenFileDialog
        {
            Title = "Select backup file",
            Filter = "GraphMailer backup (*.gmbak)|*.gmbak|All files (*.*)|*.*",
            InitialDirectory = AppPaths.BackupsDir,
        };
        if (dialog.ShowDialog() != true)
            return;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(dialog.FileName); }
        catch (Exception ex)
        {
            ConfigToolLog.Error("BackupPage", ex, "Cannot read backup file");
            ShowFeedback($"Cannot read file: {ex.Message}", isError: true);
            return;
        }

        var pwDialog = new PasswordPromptDialog(
            "Restore backup",
            $"Enter the backup password for:\n{Path.GetFileName(dialog.FileName)}")
        { Owner = Window.GetWindow(this) };
        if (pwDialog.ShowDialog() != true)
            return;

        BackupManifest manifest;
        try
        {
            manifest = _backup.ReadManifest(bytes, pwDialog.Password);
        }
        catch (BackupDecryptionException)
        {
            ShowFeedback("Wrong password, or the backup file is corrupt.", isError: true);
            return;
        }
        catch (BackupFormatException ex)
        {
            ConfigToolLog.Error("BackupPage", ex, "Invalid backup file");
            ShowFeedback($"Not a valid backup file: {ex.Message}", isError: true);
            return;
        }

        if (File.Exists(AppPaths.ConfigFilePath))
        {
            var created = manifest.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var confirm = MessageBox.Show(
                "This will OVERWRITE the current configuration.\n\n" +
                $"Backup created: {created}\nSource machine: {manifest.SourceMachine ?? "(unknown)"}\n\n" +
                "Continue?",
                "Restore configuration", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        try
        {
            _backup.Restore(bytes, pwDialog.Password);
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("BackupPage", ex, "Restore failed");
            ShowFeedback($"Restore failed: {ex.Message}", isError: true);
            return;
        }

        ShowFeedback("Configuration restored. Restart the service to apply it.", isError: false);
        _onConfigRestored();
    }

    private void ShowFeedback(string message, bool isError)
    {
        ActionFeedback.Text = message;
        ActionFeedback.Foreground = isError
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("OkBrush");
        ActionFeedback.Visibility = Visibility.Visible;
    }

    // ── Email recipients ──────────────────────────────────────────────────

    private void EmailEnabled_Changed(object sender, RoutedEventArgs e)
    {
        ValidateEmail();
        _markDirty();
    }

    /// <summary>
    /// Enabled email backups without a recipient — or without the notification sender
    /// address they are sent from — would be saved as a configuration that looks
    /// functional but silently never emails anything.
    /// </summary>
    private void ValidateEmail()
    {
        var error = ValidateEmailBackupRule(
            EmailEnabledToggle.IsChecked == true,
            _recipients.Count,
            // Queried live from the Notifications page; without the callback (tests)
            // the sender is assumed configured — the save gate still catches it.
            senderConfigured: _notificationSenderConfigured?.Invoke() ?? true);

        _emailError = error is not null;
        EmailError.Text = error ?? "";
        EmailError.Visibility = _emailError ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Validation rule for email backups (static for unit testing).</summary>
    internal static string? ValidateEmailBackupRule(bool emailEnabled, int recipientCount, bool senderConfigured)
    {
        if (!emailEnabled) return null;
        if (recipientCount == 0)
            return "Add at least one recipient — with an empty list, enabled email backups are silently skipped.";
        if (!senderConfigured)
            return "Emailed backups are sent from the sender email address on the Notifications page — none is configured yet.";
        return null;
    }

    private void AddRecipient_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PatternDialog(
            title: "Add Backup Recipient",
            description: "Enter a valid email address.",
            initialValue: "",
            validate: ValidateRecipient(null))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            _recipients.Add(new PatternRow { Pattern = dlg.Result });
            ValidateEmail();
            _markDirty();
        }
    }

    private void EditRecipient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PatternRow row) return;

        var dlg = new PatternDialog(
            title: "Edit Backup Recipient",
            description: "Enter a valid email address.",
            initialValue: row.Pattern,
            validate: ValidateRecipient(row))
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
        { _recipients.Remove(row); ValidateEmail(); _markDirty(); }
    }

    private Func<string, string?> ValidateRecipient(PatternRow? editing) => value =>
    {
        var t = value.Trim();
        if (!EmailValidation.IsValidRecipient(t))
            return "Enter a valid email address (e.g. ops@company.com).";
        if (_recipients.Where(r => r != editing).Any(r => r.Pattern.Equals(t, StringComparison.OrdinalIgnoreCase)))
            return "This address is already in the list.";
        return null;
    };

    // ── Helpers ───────────────────────────────────────────────────────────

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
    {
        if (TimeOnly.TryParse(hhmm, out var t)) return (t.Hour, t.Minute);
        return (3, 0);
    }

    private static int ClampInt(string text, int min, int max, int fallback)
        => int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;

    private static bool PathsEqual(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
