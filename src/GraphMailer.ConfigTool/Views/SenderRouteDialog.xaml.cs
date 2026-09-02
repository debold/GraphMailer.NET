using System.Windows;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Two-field modal for one sender route: which sender is relayed, and through which mailbox.
/// Usage: new SenderRouteDialog(title, sender, mailbox, validate) { Owner = ... }
/// After ShowDialog() == true, read Sender and Mailbox.
/// </summary>
public partial class SenderRouteDialog : Window
{
    private readonly Func<string, string, string?> _validate;

    /// <summary>The trimmed sender pattern after the user clicked OK.</summary>
    public string Sender { get; private set; } = "";

    /// <summary>The trimmed relay mailbox after the user clicked OK.</summary>
    public string Mailbox { get; private set; } = "";

    /// <param name="title">Window title shown in the title bar.</param>
    /// <param name="sender">Pre-filled sender pattern for edit mode; use "" for add mode.</param>
    /// <param name="mailbox">Pre-filled mailbox for edit mode; use "" for add mode.</param>
    /// <param name="validate">Returns null when the pair is valid, or an error message.</param>
    public SenderRouteDialog(string title, string sender, string mailbox, Func<string, string, string?> validate)
    {
        _validate = validate;
        InitializeComponent();

        Title = title;
        SenderBox.Text = sender;
        MailboxBox.Text = mailbox;
        SenderBox.CaretIndex = sender.Length;

        Validate();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SenderBox.Focus();
        SenderBox.SelectAll();
    }

    private void Field_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Validate();

    private void Validate()
    {
        var error = _validate(SenderBox.Text.Trim(), MailboxBox.Text.Trim());
        if (error is null)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = true;
            return;
        }

        ErrorText.Text = error;
        // Don't scold the user before they have typed anything into either field.
        ErrorText.Visibility = string.IsNullOrWhiteSpace(SenderBox.Text) && string.IsNullOrWhiteSpace(MailboxBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OkButton.IsEnabled = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Sender = SenderBox.Text.Trim();
        Mailbox = MailboxBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
