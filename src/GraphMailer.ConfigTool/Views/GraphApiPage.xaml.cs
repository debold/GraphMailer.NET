using System.Net.Mail;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.ConfigTool.Views;

public partial class GraphApiPage : UserControl
{
    private readonly Action _markDirty;
    private readonly Action<bool> _setOwnerSuppressDirty;
    private bool _revealingSecret;
    private bool _suppressDirty;

    private const string AppRegistrationName = "GraphMailer.NET";

    public GraphApiPage(Action markDirty, Action<bool> setOwnerSuppressDirty)
    {
        _markDirty = markDirty;
        _setOwnerSuppressDirty = setOwnerSuppressDirty;
        InitializeComponent();
        SmtpPage.LoadClientAuthCertificates(ManualCertList);
    }

    internal void LoadFrom(ConfigDocument doc)
    {
        var g = doc.GraphApi;
        TenantId.Text = g.TenantId ?? string.Empty;
        ClientId.Text = g.ClientId ?? string.Empty;

        bool useCert = !string.IsNullOrWhiteSpace(g.ClientCertificateThumbprint)
                    || !string.IsNullOrWhiteSpace(g.ClientCertificateSubjectName);
        AuthCert.IsChecked = useCert;
        AuthSecret.IsChecked = !useCert;

        // Secret panel
        ClientSecretBox.Password = g.ClientSecret ?? string.Empty;
        if (_revealingSecret)
        {
            ClientSecretReveal.Text = ClientSecretBox.Password;
        }

        // Certificate panel: select by thumbprint
        ManualCertList.SelectedItem = null;
        if (!string.IsNullOrWhiteSpace(g.ClientCertificateThumbprint))
        {
            foreach (var item in ManualCertList.Items.OfType<CertItem>())
            {
                if (string.Equals(item.Thumbprint, g.ClientCertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                { ManualCertList.SelectedItem = item; break; }
            }
        }

        // Show the setup-info panel when the config already contains auto-setup values
        bool hasAutoConfig = !string.IsNullOrWhiteSpace(g.TenantId)
                          && !string.IsNullOrWhiteSpace(g.ClientId)
                          && !string.IsNullOrWhiteSpace(g.ClientCertificateThumbprint);
        if (hasAutoConfig)
        {
            InfoAppName.Text = AppRegistrationName;
            InfoTenantId.Text = g.TenantId;
            InfoClientId.Text = g.ClientId;
            InfoCertSubject.Text = g.ClientCertificateSubjectName ?? string.Empty;
            InfoThumbprint.Text = FormatThumbprint(g.ClientCertificateThumbprint);

            using var certStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            certStore.Open(OpenFlags.ReadOnly);
            var match = certStore.Certificates
                .Find(X509FindType.FindByThumbprint, g.ClientCertificateThumbprint!, false)
                .OfType<X509Certificate2>().FirstOrDefault();
            if (match is not null)
            {
                var days = (int)(match.NotAfter.Date - DateTime.UtcNow.Date).TotalDays;
                InfoValidUntil.Text = $"{match.NotAfter:d}  (in {days} days)";
            }
            else
            {
                InfoValidUntil.Text = "(certificate not in local store)";
            }

            SetupInfoPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SetupInfoPanel.Visibility = Visibility.Collapsed;
        }

        // Flag the secret field when its ENC[...] value could not be decrypted on load.
        SetSecretUndecryptable(
            DecryptionFailureMap.HasGraphApiFailure(doc.DecryptionFailures));
    }

    private void SetSecretUndecryptable(bool on)
        => SecretWarning.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>True while the client secret is flagged as undecryptable.</summary>
    internal bool HasUndecryptableSecret => SecretWarning.Visibility == Visibility.Visible;

    /// <summary>Clears the undecryptable marker (e.g. after a save re-encrypts secrets).</summary>
    internal void ClearUndecryptableMarker() => SetSecretUndecryptable(false);

    internal void CollectTo(ConfigDocument doc)
    {
        var g = doc.GraphApi;
        g.TenantId = NullIfEmpty(TenantId.Text);
        g.ClientId = NullIfEmpty(ClientId.Text);

        if (AuthSecret.IsChecked == true)
        {
            g.ClientSecret = NullIfEmpty(ClientSecretBox.Password);
            g.ClientCertificateThumbprint = null;
            g.ClientCertificateSubjectName = null;
            g.ClientCertificateIssuer = null;
        }
        else
        {
            g.ClientSecret = null;
            if (ManualCertList.SelectedItem is CertItem cert)
            {
                g.ClientCertificateThumbprint = cert.Thumbprint;
                g.ClientCertificateSubjectName = cert.Subject;
                g.ClientCertificateIssuer = cert.Issuer;
            }
        }
    }

    private static string? NullIfEmpty(string? v)
        => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    // ── Automatic setup ───────────────────────────────────────────────────────

    private void RunSetup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EntraSetupProgressWindow(AppRegistrationName)
        {
            Owner = Window.GetWindow(this),
        };

        // Suppress dirty-marking on ALL pages for the entire dialog lifecycle.
        // When the dialog closes WPF fires focus-restore events (SelectionChanged,
        // TextChanged, Checked, …) on the owner window's controls — including
        // controls on other pages. These all flow through MainWindow.MarkDirty()
        // which respects MainWindow._suppressDirty, so we toggle that flag here.
        // BeginInvoke at ContextIdle (priority 3) releases the flag only after
        // all higher-priority queued events (Input=5, Render=7, Normal=9 …)
        // have already been dispatched while the flag was still true.
        _suppressDirty = true;
        _setOwnerSuppressDirty(true);
        dlg.ShowDialog();
        var capturedResult = dlg.SetupResult;
        Dispatcher.BeginInvoke(() =>
        {
            _suppressDirty = false;
            _setOwnerSuppressDirty(false);
            if (capturedResult is not null)
                ApplySetupResult(capturedResult);
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void ApplySetupResult(EntraSetupService.Result result)
    {
        bool changed =
            !string.Equals(TenantId.Text.Trim(), result.TenantId, StringComparison.Ordinal) ||
            !string.Equals(ClientId.Text.Trim(), result.ClientId, StringComparison.Ordinal) ||
            AuthCert.IsChecked != true ||
            ManualCertList.SelectedItem is not CertItem sel ||
            !string.Equals(sel.Thumbprint, result.CertThumbprint, StringComparison.OrdinalIgnoreCase);

        // Only write to input controls when something actually changed.
        // Writing values (even identical ones) can trigger TextChanged / SelectionChanged
        // events that arrive after _suppressDirty is restored and would set the dirty flag
        // incorrectly.
        if (changed)
        {
            _suppressDirty = true;
            try
            {
                TenantId.Text = result.TenantId;
                ClientId.Text = result.ClientId;
                AuthCert.IsChecked = true;

                SmtpPage.LoadClientAuthCertificates(ManualCertList);
                foreach (var item in ManualCertList.Items.OfType<CertItem>())
                {
                    if (string.Equals(item.Thumbprint, result.CertThumbprint, StringComparison.OrdinalIgnoreCase))
                    { ManualCertList.SelectedItem = item; break; }
                }
            }
            finally
            {
                _suppressDirty = false;
            }
            _markDirty();
        }

        // Always refresh the display panel and hide the setup-prompt box.
        // These are display-only TextBlocks / visibility toggles — no change events.
        ShowSetupInfo(AppRegistrationName, result.TenantId, result.ClientId,
                      result.CertSubject, result.CertThumbprint, result.CertNotAfter);
        SetupStatusBox.Visibility = Visibility.Collapsed;
    }

    // ── Test email ────────────────────────────────────────────────────────────

    private void TestField_Changed(object sender, TextChangedEventArgs e)
    {
        if (TestSendButton is null) return; // before InitializeComponent

        bool fromValid = IsValidEmail(TestFrom.Text);
        bool toValid = IsValidEmail(TestTo.Text);

        TestFromHint.Visibility = !string.IsNullOrWhiteSpace(TestFrom.Text) && !fromValid
            ? Visibility.Visible : Visibility.Collapsed;
        TestToHint.Visibility = !string.IsNullOrWhiteSpace(TestTo.Text) && !toValid
            ? Visibility.Visible : Visibility.Collapsed;

        TestSendButton.IsEnabled = fromValid && toValid;
    }

    private static bool IsValidEmail(string? text)
        => !string.IsNullOrWhiteSpace(text) && MailAddress.TryCreate(text.Trim(), out _);

    private async void TestSend_Click(object sender, RoutedEventArgs e)
    {
        var tenantId = NullIfEmpty(TenantId.Text);
        var clientId = NullIfEmpty(ClientId.Text);
        var from = NullIfEmpty(TestFrom.Text);
        var to = NullIfEmpty(TestTo.Text);

        if (tenantId is null || clientId is null)
        {
            ShowTestWarn("⚠  Please enter Tenant ID and Client ID in the Manual Configuration section.");
            return;
        }

        string? clientSecret = null;
        string? certThumbprint = null;

        if (AuthCert.IsChecked == true)
        {
            certThumbprint = ManualCertList.SelectedItem is CertItem c ? c.Thumbprint : null;
            if (certThumbprint is null)
            {
                ShowTestWarn("⚠  No certificate selected. Select a certificate in the Manual Configuration section.");
                return;
            }
        }
        else
        {
            clientSecret = NullIfEmpty(ClientSecretBox.Password);
            if (clientSecret is null)
            {
                ShowTestWarn("⚠  No client secret entered. Enter a client secret in the Manual Configuration section.");
                return;
            }
        }

        TestSendButton.IsEnabled = false;
        HideTestBoxes();
        ShowTestInfo("Authenticating with Microsoft Entra ID…");

        try
        {
            await GraphApiTestService.SendAsync(
                tenantId, clientId, clientSecret, certThumbprint,
                from!, to!,
                System.Threading.CancellationToken.None);

            ShowTestOk($"✔  Test email sent successfully to {to}.");
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("GraphApiPage", ex, "Graph API test send failed");
            ShowTestWarn($"⚠  Failed: {ex.Message}");
        }
        finally
        {
            TestSendButton.IsEnabled = IsValidEmail(TestFrom.Text) && IsValidEmail(TestTo.Text);
        }
    }

    private void HideTestBoxes()
    {
        TestInfoBox.Visibility = Visibility.Collapsed;
        TestOkBox.Visibility = Visibility.Collapsed;
        TestWarnBox.Visibility = Visibility.Collapsed;
    }

    private void ShowTestInfo(string msg)
    {
        HideTestBoxes();
        TestInfoText.Text = msg;
        TestInfoBox.Visibility = Visibility.Visible;
    }

    private void ShowTestOk(string msg)
    {
        HideTestBoxes();
        TestOkText.Text = msg;
        TestOkBox.Visibility = Visibility.Visible;
    }

    private void ShowTestWarn(string msg)
    {
        HideTestBoxes();
        TestWarnText.Text = msg;
        TestWarnBox.Visibility = Visibility.Visible;
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void ShowStatus(string message)
    {
        SetupStatusText.Text = message;
        SetupStatusBox.Visibility = Visibility.Visible;
    }

    private static string FormatThumbprint(string? thumb) =>
        thumb?.Length == 40
            ? $"{thumb[..8]} {thumb[8..16]} {thumb[16..24]} {thumb[24..32]} {thumb[32..]}"
            : thumb ?? string.Empty;

    private void ShowSetupInfo(string appName, string tenantId, string clientId,
                               string certSubject, string thumbprint, DateTime validUntil)
    {
        InfoAppName.Text = appName;
        InfoTenantId.Text = tenantId;
        InfoClientId.Text = clientId;
        InfoCertSubject.Text = certSubject;
        InfoThumbprint.Text = FormatThumbprint(thumbprint);

        var days = (int)(validUntil.Date - DateTime.UtcNow.Date).TotalDays;
        InfoValidUntil.Text = $"{validUntil:d}  (in {days} days)";

        SetupInfoPanel.Visibility = Visibility.Visible;
    }

    // ── Manual configuration ──────────────────────────────────────────────────

    private void ManualCertList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedThumbprintText.Text = ManualCertList.SelectedItem is CertItem c
            ? $"Thumbprint: {c.Thumbprint}"
            : string.Empty;
        if (!_suppressDirty) _markDirty();
    }

    private void RefreshManualCerts_Click(object sender, RoutedEventArgs e)
        => SmtpPage.LoadClientAuthCertificates(ManualCertList);

    private void ToggleManual_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        bool expanded = ManualPanel.Visibility == Visibility.Visible;
        ManualPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ManualToggleIcon.Text = expanded ? "▶" : "▼";
    }

    private void AnyField_Changed(object sender, TextChangedEventArgs e)
    { if (!_suppressDirty) _markDirty(); }

    private void AuthMethod_Changed(object sender, RoutedEventArgs e)
    {
        if (SecretPanel is null) return; // called before InitializeComponent
        bool useSecret = AuthSecret.IsChecked == true;
        SecretPanel.Visibility = useSecret ? Visibility.Visible : Visibility.Collapsed;
        CertPanel.Visibility = useSecret ? Visibility.Collapsed : Visibility.Visible;
        if (!_suppressDirty) _markDirty();
    }

    private void ClientSecret_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirty) return;   // programmatic load, not a user edit
        // The user typed a new secret → it is no longer the undecryptable one.
        // Clear before _markDirty so the owner's badge refresh sees the cleared state.
        if (ClientSecretBox.Password.Length > 0) SetSecretUndecryptable(false);
        _markDirty();
    }

    private void SecretReveal_Click(object sender, RoutedEventArgs e)
    {
        _revealingSecret = !_revealingSecret;
        if (_revealingSecret)
        {
            ClientSecretReveal.Text = ClientSecretBox.Password;
            ClientSecretBox.Visibility = Visibility.Collapsed;
            ClientSecretReveal.Visibility = Visibility.Visible;
            ClientSecretReveal.Focus();
            ClientSecretReveal.CaretIndex = ClientSecretReveal.Text.Length;
        }
        else
        {
            ClientSecretBox.Password = ClientSecretReveal.Text;
            ClientSecretReveal.Visibility = Visibility.Collapsed;
            ClientSecretBox.Visibility = Visibility.Visible;
            ClientSecretBox.Focus();
        }
    }

    private void ClientSecretReveal_Changed(object sender, TextChangedEventArgs e)
    {
        if (_revealingSecret)
            ClientSecretBox.Password = ClientSecretReveal.Text;
        if (_suppressDirty) return;
        if (ClientSecretReveal.Text.Length > 0) SetSecretUndecryptable(false);
        _markDirty();
    }
}