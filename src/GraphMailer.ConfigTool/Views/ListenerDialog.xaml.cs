using System.Windows;
using System.Windows.Controls;

namespace GraphMailer.ConfigTool.Views;

public partial class ListenerDialog : Window
{
    public bool ResultEnabled { get; private set; }
    public string ResultDescription { get; private set; } = "";
    public int ResultPort { get; private set; }
    public string ResultTlsMode { get; private set; } = "None";
    public string ResultAuthMode { get; private set; } = "None";

    private readonly Func<int, string?>? _validatePort;

    public ListenerDialog(ListenerRow? existing = null, Func<int, string?>? validatePort = null)
    {
        _validatePort = validatePort;
        InitializeComponent();

        if (existing is not null)
        {
            Title = "Edit Listener";
            EnabledBox.IsChecked = existing.Enabled;
            DescriptionBox.Text = existing.Description;
            PortBox.Text = existing.Port.ToString();
            SelectComboItem(TlsModeBox, existing.TlsMode);
            SelectComboItem(AuthModeBox, existing.AuthMode);
        }
        else
        {
            Title = "Add Listener";
            EnabledBox.IsChecked = true;
            TlsModeBox.SelectedIndex = 0;
            AuthModeBox.SelectedIndex = 0;
        }

        Validate(null, null!);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DescriptionBox.Focus();
        DescriptionBox.SelectAll();
    }

    private void Validate(object? sender, TextChangedEventArgs e)
    {
        if (OkButton is null) return; // called before InitializeComponent

        var desc = DescriptionBox.Text.Trim();
        var portText = PortBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(desc))
        {
            SetError("Description must not be empty.", !string.IsNullOrWhiteSpace(DescriptionBox.Text));
            return;
        }

        if (!int.TryParse(portText, out var port) || port < 1 || port > 65535)
        {
            SetError("Port must be a number between 1 and 65535.", portText.Length > 0);
            return;
        }

        var portError = _validatePort?.Invoke(port);
        if (portError is not null)
        {
            SetError(portError, true);
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        OkButton.IsEnabled = true;
    }

    private void SetError(string msg, bool visible)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultEnabled = EnabledBox.IsChecked == true;
        ResultDescription = DescriptionBox.Text.Trim();
        ResultPort = int.Parse(PortBox.Text.Trim());
        ResultTlsMode = (TlsModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
        ResultAuthMode = (AuthModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void SelectComboItem(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Content?.ToString() == value)
            { box.SelectedItem = item; return; }
        }
        box.SelectedIndex = 0;
    }
}
