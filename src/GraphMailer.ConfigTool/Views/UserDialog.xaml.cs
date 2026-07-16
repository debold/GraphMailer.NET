using System.Windows;
using System.Windows.Controls;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Modal dialog for adding or editing an SMTP user.
/// After ShowDialog() == true, read the Result properties.
/// </summary>
public partial class UserDialog : Window
{
    private readonly bool _isEdit;
    private bool _revealing;

    // ── Result ────────────────────────────────────────────────────────────
    public bool ResultEnabled { get; private set; }
    public string ResultUsername { get; private set; } = "";
    public string ResultDisplayName { get; private set; } = "";
    /// <summary>The validated password entered by the user. Empty string when capture mode is active.</summary>
    public string ResultPassword { get; private set; } = "";
    /// <summary>When true, the service will capture the password on the next SMTP AUTH.</summary>
    public bool ResultCaptureNextPassword { get; private set; }
    /// <summary>Parsed, validated list of MAIL FROM restrictions. Empty = no restriction.</summary>
    public List<string> ResultFromRestrictions { get; private set; } = [];

    // ── Construction ──────────────────────────────────────────────────────

    /// <param name="existing">Pass an existing UserRow to pre-fill (edit mode); null for add mode.</param>
    public UserDialog(UserRow? existing = null)
    {
        _isEdit = existing is not null;
        InitializeComponent();

        Title = _isEdit ? "Edit User" : "Add User";

        if (existing is not null)
        {
            EnabledBox.IsChecked = existing.Enabled;
            UsernameBox.Text = existing.Username;
            DisplayNameBox.Text = existing.DisplayName;
            if (existing.CaptureNextPassword)
            {
                CaptureBox.IsChecked = true;
                // PasswordPanel hidden via Capture_Changed
            }
            else
            {
                PasswordBox.Password = existing.NewPassword ?? "";
                ConfirmBox.Password = existing.NewPassword ?? "";
            }
            FromRestrictionsBox.Text = existing.FromRestrictions is { Count: > 0 }
                ? string.Join(", ", existing.FromRestrictions)
                : "";
        }
        else
        {
            EnabledBox.IsChecked = true;
        }

        Validate();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isEdit)
        {
            // In edit mode position cursor at end of username
            UsernameBox.CaretIndex = UsernameBox.Text.Length;
        }
        UsernameBox.Focus();
    }

    // ── Capture toggle ────────────────────────────────────────────────────

    private void Capture_Changed(object sender, RoutedEventArgs e)
    {
        bool capture = CaptureBox.IsChecked == true;
        PasswordPanel.Visibility = capture ? Visibility.Collapsed : Visibility.Visible;
        CaptureHint.Visibility = capture ? Visibility.Visible : Visibility.Collapsed;
        RunValidation();
    }

    // ── Reveal toggle ─────────────────────────────────────────────────────

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        _revealing = !_revealing;
        RevealButton.Content = _revealing ? "🔒" : "👁";

        if (_revealing)
        {
            PasswordReveal.Text = PasswordBox.Password;
            ConfirmReveal.Text = ConfirmBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            ConfirmBox.Visibility = Visibility.Collapsed;
            PasswordReveal.Visibility = Visibility.Visible;
            ConfirmReveal.Visibility = Visibility.Visible;
            PasswordReveal.Focus();
            PasswordReveal.CaretIndex = PasswordReveal.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordReveal.Text;
            ConfirmBox.Password = ConfirmReveal.Text;
            PasswordReveal.Visibility = Visibility.Collapsed;
            ConfirmReveal.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            ConfirmBox.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
    }

    // ── Validation ────────────────────────────────────────────────────────

    private void Validate(object? sender = null,
        System.Windows.Controls.TextChangedEventArgs? e = null)
        => RunValidation();

    private void PasswordBox_Changed(object sender, RoutedEventArgs e)
        => RunValidation();

    private void PasswordReveal_Changed(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        // Keep the hidden boxes in sync while revealing
        if (_revealing)
        {
            if (sender == PasswordReveal) PasswordBox.Password = PasswordReveal.Text;
            else ConfirmBox.Password = ConfirmReveal.Text;
        }
        RunValidation();
    }

    private void RunValidation()
    {
        var username = UsernameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(username))
        { ShowError("Username must not be empty."); return; }

        if (CaptureBox.IsChecked != true)
        {
            var pw = _revealing ? PasswordReveal.Text : PasswordBox.Password;
            var confirm = _revealing ? ConfirmReveal.Text : ConfirmBox.Password;
            if (pw.Length == 0)
            { ShowError("Password is required."); return; }
            if (pw.Length < 8)
            { ShowError("Password must be at least 8 characters."); return; }
            if (pw != confirm)
            { ShowError("Passwords do not match."); return; }
        }

        // Validate FromRestrictions — same rules as address/domain patterns
        var rawRestrictions = FromRestrictionsBox.Text.Trim();
        if (!string.IsNullOrEmpty(rawRestrictions))
        {
            var parts = rawRestrictions.Split(',', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (!GraphMailer.ConfigTool.Helpers.AddressPatternValidator.IsValidPattern(part))
                {
                    ShowError($"Invalid MAIL FROM pattern: '{part}'.\nAccepted: user@domain.com or @domain.com");
                    return;
                }
            }
        }

        ClearError();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        OkButton.IsEnabled = false;
    }

    private void ClearError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        OkButton.IsEnabled = true;
    }

    // ── Commit ────────────────────────────────────────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultEnabled = EnabledBox.IsChecked == true;
        ResultUsername = UsernameBox.Text.Trim();
        ResultDisplayName = DisplayNameBox.Text.Trim();
        ResultCaptureNextPassword = CaptureBox.IsChecked == true;
        ResultPassword = ResultCaptureNextPassword
            ? ""
            : (_revealing ? PasswordReveal.Text : PasswordBox.Password);
        ResultFromRestrictions = FromRestrictionsBox.Text
            .Split(',', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
