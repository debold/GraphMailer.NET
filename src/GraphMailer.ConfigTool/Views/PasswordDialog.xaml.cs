using System.Windows;

namespace GraphMailer.ConfigTool.Views;

public partial class PasswordDialog : Window
{
    public string Password { get; private set; } = "";

    public PasswordDialog(string username)
    {
        InitializeComponent();
        UserLabel.Text = $"New password for '{username}'";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PwBox.Password != PwConfirm.Password)
        {
            ErrText.Text = "Passwords do not match.";
            ErrText.Visibility = Visibility.Visible;
            return;
        }
        if (PwBox.Password.Length < 8)
        {
            ErrText.Text = "Password must be at least 8 characters.";
            ErrText.Visibility = Visibility.Visible;
            return;
        }
        Password = PwBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
