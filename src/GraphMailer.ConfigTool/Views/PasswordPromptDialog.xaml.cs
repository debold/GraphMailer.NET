using System.Windows;

namespace GraphMailer.ConfigTool.Views;

/// <summary>Single-field password prompt (e.g. to unlock a backup file for restore).</summary>
public partial class PasswordPromptDialog : Window
{
    public string Password { get; private set; } = "";

    public PasswordPromptDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        Loaded += (_, _) => PwBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PwBox.Password.Length == 0)
        {
            ErrText.Text = "Please enter the password.";
            ErrText.Visibility = Visibility.Visible;
            return;
        }
        Password = PwBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
