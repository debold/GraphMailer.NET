using System.Net;
using System.Windows;

namespace GraphMailer.ConfigTool.Views;

public partial class IpEntryDialog : Window
{
    private readonly Func<string, string?> _validate;

    public string ResultEntry { get; private set; } = "";
    public string ResultComment { get; private set; } = "";

    /// <param name="title">Window title.</param>
    /// <param name="description">Helper text shown above the IP field.</param>
    /// <param name="initialEntry">Pre-filled IP/CIDR for edit mode.</param>
    /// <param name="initialComment">Pre-filled comment for edit mode.</param>
    /// <param name="extraValidate">Optional extra validator (e.g. duplicate check). Return null if valid.</param>
    public IpEntryDialog(string title, string description,
        string initialEntry = "", string initialComment = "",
        Func<string, string?>? extraValidate = null)
    {
        _validate = v =>
        {
            var err = ValidateIp(v);
            if (err is not null) return err;
            return extraValidate?.Invoke(v);
        };

        InitializeComponent();

        Title = title;
        DescriptionText.Text = description;
        EntryBox.Text = initialEntry;
        CommentBox.Text = initialComment;

        Validate(initialEntry);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EntryBox.Focus();
        EntryBox.SelectAll();
    }

    private void EntryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Validate(EntryBox.Text);

    private void Validate(string text)
    {
        var error = _validate(text.Trim());
        if (error is null)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = true;
        }
        else
        {
            ErrorText.Text = error;
            ErrorText.Visibility = string.IsNullOrWhiteSpace(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            OkButton.IsEnabled = false;
        }
    }

    private static string? ValidateIp(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "IP address or CIDR must not be empty.";

        var s = value.Trim();

        // CIDR notation: e.g. 192.168.0.0/24 or 2001:db8::/32
        if (s.Contains('/'))
        {
            var parts = s.Split('/', 2);
            if (!IsStrictIp(parts[0], out var ip))
                return $"'{parts[0]}' is not a valid IP address.";
            if (!int.TryParse(parts[1], out var prefix))
                return "CIDR prefix must be a number.";
            int maxPrefix = ip!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            if (prefix < 0 || prefix > maxPrefix)
                return $"CIDR prefix must be between 0 and {maxPrefix}.";
            return null;
        }

        // Plain IP address
        if (!IsStrictIp(s, out _))
            return $"'{s}' is not a valid IP address or CIDR block.\nExamples: 192.168.1.1 · 10.0.0.0/8 · 2001:db8::1";

        return null;
    }

    /// <summary>
    /// Strict IPv4/IPv6 parse. Rejects BSD shorthand like "1" or "192.168"
    /// that IPAddress.TryParse accepts as compressed octet notation.
    /// </summary>
    private static bool IsStrictIp(string s, out IPAddress? ip)
    {
        ip = null;
        if (!IPAddress.TryParse(s, out var parsed)) return false;

        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // Enforce exactly four decimal octets (a.b.c.d)
            var octets = s.Split('.');
            if (octets.Length != 4) return false;
            foreach (var octet in octets)
                if (!int.TryParse(octet, out var n) || n < 0 || n > 255) return false;
        }

        ip = parsed;
        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultEntry = EntryBox.Text.Trim();
        ResultComment = CommentBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
