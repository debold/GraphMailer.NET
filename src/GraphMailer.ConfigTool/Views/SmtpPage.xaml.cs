using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure.Certificates;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.ConfigTool.Views;

public partial class SmtpPage : UserControl
{
    private readonly Action _markDirty;
    private readonly ObservableCollection<ListenerRow> _listeners = [];

    public SmtpPage(Action markDirty)
    {
        _markDirty = markDirty;
        InitializeComponent();
        ListenersGrid.ItemsSource = _listeners;
        LoadCertificates(TlsCertList);
    }

    internal void LoadFrom(ConfigDocument doc)
    {
        _listeners.Clear();
        // When the user config has no listeners, fall back to the SAME shared defaults the
        // service seeds on a fresh install (DefaultConfiguration), so the tool shows exactly what
        // the service runs instead of carrying its own divergent hard-coded defaults.
        var servers = doc.Servers.Count > 0 ? doc.Servers : DefaultConfiguration.Servers();
        foreach (var s in servers)
            _listeners.Add(new ListenerRow
            {
                Enabled = s.Enabled,
                Description = s.Name,
                Port = s.Port,
                TlsMode = ModeToTlsDisplay(s.Mode, s.AuthMode == "Required"),
                AuthMode = s.AuthMode,
            });

        // Bytes → MB (0 means "no limit", show as 0)
        MaxSizeMb.Text = (doc.Smtp.MaxSizeBytes / 1_048_576).ToString();
        SmtpBanner.Text = string.IsNullOrWhiteSpace(doc.Smtp.Banner) ? "GraphMailer" : doc.Smtp.Banner;

        // Re-select saved TLS certificate by CN
        TlsCertList.SelectedItem = null;
        if (!string.IsNullOrWhiteSpace(doc.Certificate.SubjectName))
        {
            foreach (var item in TlsCertList.Items.OfType<CertItem>())
            {
                if (string.Equals(item.Subject, doc.Certificate.SubjectName, StringComparison.OrdinalIgnoreCase))
                { TlsCertList.SelectedItem = item; break; }
            }
        }

        TlsFailClosed.IsChecked = doc.Certificate.FailClosed;
    }

    internal void CollectTo(ConfigDocument doc)
    {
        doc.Servers = _listeners
            .Select(r => new ConfigDocument.ServerEntry
            {
                Enabled = r.Enabled,
                Name = r.Description,
                Port = r.Port,
                Mode = TlsDisplayToMode(r.TlsMode),
                AuthMode = r.AuthMode,
            }).ToList();

        doc.Smtp.MaxSizeBytes = long.TryParse(MaxSizeMb.Text, out var mb) && mb >= 0
            ? mb * 1_048_576
            : 26_214_400;
        doc.Smtp.Banner = string.IsNullOrWhiteSpace(SmtpBanner.Text) ? "GraphMailer" : SmtpBanner.Text.Trim();

        if (TlsCertList.SelectedItem is CertItem cert)
        {
            doc.Certificate.SubjectName = cert.Subject;
            doc.Certificate.Issuer = cert.Issuer;
        }
        else
        {
            doc.Certificate.SubjectName = null;
            doc.Certificate.Issuer = null;
        }

        doc.Certificate.FailClosed = TlsFailClosed.IsChecked == true;
    }

    private static string ModeToTlsDisplay(string mode, bool authRequired)
        => mode.Equals("StartTls", StringComparison.OrdinalIgnoreCase)
               ? (authRequired ? "STARTTLS (required)" : "STARTTLS (optional)")
        : mode.Equals("Tls", StringComparison.OrdinalIgnoreCase)
               ? "Implicit TLS"
        : "None";

    private static string TlsDisplayToMode(string display) => display switch
    {
        "STARTTLS (optional)" => "StartTls",
        "STARTTLS (required)" => "StartTls",
        "Implicit TLS" => "Tls",
        _ => "Plain",
    };

    private void AddListener_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ListenerDialog(
            validatePort: p => _listeners.Any(r => r.Port == p) ? $"Port {p} is already used by another listener." : null)
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            _listeners.Add(new ListenerRow
            {
                Enabled = dlg.ResultEnabled,
                Description = dlg.ResultDescription,
                Port = dlg.ResultPort,
                TlsMode = dlg.ResultTlsMode,
                AuthMode = dlg.ResultAuthMode
            });
            _markDirty();
        }
    }

    private void EditListener_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ListenerRow row) return;
        var dlg = new ListenerDialog(
            existing: row,
            validatePort: p => _listeners.Any(r => r != row && r.Port == p) ? $"Port {p} is already used by another listener." : null)
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            row.Enabled = dlg.ResultEnabled; row.Description = dlg.ResultDescription;
            row.Port = dlg.ResultPort; row.TlsMode = dlg.ResultTlsMode; row.AuthMode = dlg.ResultAuthMode;
            _markDirty();
        }
    }

    private void RemoveListener_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ListenerRow row)
        { _listeners.Remove(row); _markDirty(); }
    }

    private void ListenerEnabled_Changed(object sender, RoutedEventArgs e) => _markDirty();

    private void TlsFailClosed_Changed(object sender, RoutedEventArgs e) => _markDirty();

    private void TlsCertList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TlsCertList.SelectedItem is CertItem cert)
        {
            TlsCertCn.Text = cert.Subject;
            TlsCertIssuer.Text = cert.Issuer;
            TlsCertExpiry.Text = cert.ExpiryText;
            _markDirty();
        }
    }

    private void RefreshTlsCerts_Click(object sender, RoutedEventArgs e)
        => LoadCertificates(TlsCertList);

    private async void CreateSmtpCert_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        btn.Content = "Creating\u2026";
        try
        {
            var (subject, aclGranted) = await Task.Run(SelfSignedSmtpCertificate.CreateAndInstall);

            LoadCertificates(TlsCertList);
            foreach (var item in TlsCertList.Items.OfType<CertItem>())
            {
                if (string.Equals(item.Subject, subject, StringComparison.OrdinalIgnoreCase))
                { TlsCertList.SelectedItem = item; break; }
            }

            var msg = aclGranted
                ? $"Certificate \u2018{subject}\u2019 has been created and installed.\n\n" +
                  "\u2022 LocalMachine\\My \u2014 used by the SMTP service\n" +
                  "\u2022 LocalMachine\\Root \u2014 trusted by all applications on this machine\n\n" +
                  "NETWORK SERVICE was granted read access to the private key.\n" +
                  "If the service runs under a different account, open certlm.msc, right-click the certificate \u2192 All Tasks \u2192 Manage Private Keys and grant that account Read access."
                : $"Certificate \u2018{subject}\u2019 has been created and installed.\n\n" +
                  "\u2022 LocalMachine\\My \u2014 used by the SMTP service\n" +
                  "\u2022 LocalMachine\\Root \u2014 trusted by all applications on this machine\n\n" +
                  "Important: to let the service use this certificate, open certlm.msc, right-click the certificate \u2192 All Tasks \u2192 Manage Private Keys and grant the service account (e.g. NETWORK SERVICE) Read access.";

            MessageBox.Show(msg, "Certificate created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("SmtpPage", ex, "Self-signed certificate creation failed");
            MessageBox.Show($"Failed to create certificate:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = "+ Create self-signed certificate";
        }
    }

    private void AnyField_Changed(object sender, TextChangedEventArgs e)
        => _markDirty();

    internal static void LoadCertificates(ListBox listBox)
        => LoadCertificatesByEku(listBox, "1.3.6.1.5.5.7.3.1"); // Server Authentication

    internal static void LoadClientAuthCertificates(ListBox listBox)
        => LoadCertificatesByEku(listBox, "1.3.6.1.5.5.7.3.2"); // Client Authentication

    private static void LoadCertificatesByEku(ListBox listBox, string ekuOid)
    {
        var items = new List<CertItem>();
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates)
            {
                // Must have a private key – without it SslStream cannot perform the TLS handshake
                if (!cert.HasPrivateKey) continue;

                var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
                bool matches = eku?.EnhancedKeyUsages
                    .Cast<Oid>()
                    .Any(o => o.Value == ekuOid) ?? false;
                if (!matches) continue;

                // Verify the private key is actually accessible from the current process.
                // A certificate with an inaccessible key will cause 0x80090327 at runtime.
                if (!IsPrivateKeyAccessible(cert)) continue;

                items.Add(new CertItem(cert));
            }
        }
        catch { /* insufficient permissions — show empty list */ }

        listBox.ItemsSource = items.OrderByDescending(c => c.NotAfter).ToList();
    }

    /// <summary>
    /// Returns true when the certificate's private key can be opened from the current process.
    /// Certificates in LocalMachine\My may have ACLs that deny access to non-SYSTEM accounts.
    /// </summary>
    private static bool IsPrivateKeyAccessible(X509Certificate2 cert)
    {
        try
        {
            // Try the most common key types; success means the key store ACL allows access
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa != null) { rsa.ExportParameters(false); return true; }

            using var ecdsa = cert.GetECDsaPrivateKey();
            if (ecdsa != null) { ecdsa.ExportParameters(false); return true; }

            return false; // No recognisable key type
        }
        catch (CryptographicException)
        {
            return false; // Key exists but access is denied
        }
    }
}

// ── Data models ──────────────────────────────────────────────────────────────

public class ListenerRow : INotifyPropertyChanged
{
    private bool _enabled;
    private string _description = "";
    private int _port;
    private string _tlsMode = "None";
    private string _authMode = "None";

    public bool Enabled { get => _enabled; set { _enabled = value; OnPropChanged(); } }
    public string Description { get => _description; set { _description = value; OnPropChanged(); } }
    public int Port { get => _port; set { _port = value; OnPropChanged(); } }
    public string TlsMode { get => _tlsMode; set { _tlsMode = value; OnPropChanged(); } }
    public string AuthMode { get => _authMode; set { _authMode = value; OnPropChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropChanged([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public class CertItem
{
    public CertItem(X509Certificate2 cert)
    {
        Subject = cert.GetNameInfo(X509NameType.SimpleName, false);
        Issuer = cert.GetNameInfo(X509NameType.SimpleName, true);
        Thumbprint = cert.Thumbprint;
        NotAfter = cert.NotAfter;
        ExpiryText = NotAfter < DateTime.Now
            ? $"EXPIRED {NotAfter:yyyy-MM-dd}"
            : $"Expires {NotAfter:yyyy-MM-dd}";
        IsExpired = NotAfter < DateTime.Now;
    }

    public string Subject { get; }
    public string Issuer { get; }
    public string Thumbprint { get; }
    public DateTime NotAfter { get; }
    public string ExpiryText { get; }
    public bool IsExpired { get; }

    public string ExpiryFg => IsExpired ? "#FFC42B1C" : "#FF616161";
}
