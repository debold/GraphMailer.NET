using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Encryption;
using static GraphMailer.ConfigTool.Helpers.ServiceControl;

namespace GraphMailer.ConfigTool.Views;

public partial class StatusPage : UserControl
{
    private static string DbPath => Path.Combine(AppPaths.DataDir, "metrics.db");
    private static string QueueDir => Path.Combine(AppPaths.MailDir, "queue");

    private readonly DispatcherTimer _timer;
    private bool _loadInProgress;

    public StatusPage()
    {
        InitializeComponent();
        LoadData();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => LoadData();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { LoadData(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private async void LoadData()
    {
        // Prevent overlapping refreshes (slow port checks can outlast the 5s interval)
        if (_loadInProgress) return;
        _loadInProgress = true;
        try
        {
            await LoadDataAsync();
        }
        finally
        {
            _loadInProgress = false;
        }
    }

    private async Task LoadDataAsync()
    {
        // ── KPIs from SQLite (fast — keep on background thread) ──────────
        var today = DateTime.UtcNow.Date.ToString("O");
        var now = DateTime.UtcNow;

        var (delivered, failed, queued, kpiErr) = await Task.Run(() =>
        {
            long d = 0, f = 0;
            int q = 0;
            bool err = false;
            if (File.Exists(DbPath))
            {
                try
                {
                    using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
                    conn.Open();
                    d = QueryScalar(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='sent'   AND occurred_at >= $d", today);
                    f = QueryScalar(conn, "SELECT COUNT(*) FROM email_events WHERE event_type='failed' AND occurred_at >= $d", today);
                }
                catch { err = true; }
            }
            try { q = Directory.Exists(QueueDir) ? Directory.GetFiles(QueueDir, "*.meta.json").Length : 0; }
            catch { }
            return (d, f, q, err);
        });

        if (kpiErr)
        {
            KpiDelivered.Text = "ERR"; KpiDeliveredSub.Text = "cannot read db";
            KpiFailed.Text = "ERR"; KpiFailedSub.Text = "cannot read db";
        }
        else if (!File.Exists(DbPath))
        {
            KpiDelivered.Text = "—"; KpiDeliveredSub.Text = "no data yet";
            KpiFailed.Text = "—"; KpiFailedSub.Text = "no data yet";
        }
        else
        {
            KpiDelivered.Text = delivered.ToString(); KpiDeliveredSub.Text = "sent today (UTC)";
            KpiFailed.Text = failed.ToString(); KpiFailedSub.Text = "failed today (UTC)";
        }

        KpiQueued.Text = queued.ToString(); KpiQueuedSub.Text = "messages in queue";

        var (uptimeText, uptimeSub) = await Task.Run(GetServiceUptime);
        KpiUptime.Text = uptimeText;
        KpiUptimeSub.Text = uptimeSub;

        // ── Health rows (slow: sc.exe + TCP connect) — background thread ─
        var healthRows = await Task.Run(() => BuildHealthRows().ToList());
        HealthGrid.ItemsSource = healthRows;

        UpdateSecretsBanner(healthRows.FirstOrDefault(r => r.Component == SecretsComponent));
        UpdateServiceStatus();
    }

    private static long QueryScalar(SqliteConnection conn, string sql, string param)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$d", param);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : 0;
    }

    // ── Service Control ──────────────────────────────────────────────────
    // sc.exe invocation and queryex parsing live in Helpers.ServiceControl
    // (imported via "using static"), shared with the MainWindow status poller.
    private const string SvcName = ServiceName;

    private void UpdateServiceStatus()
    {
        var (state, exists) = QueryServiceState();
        bool running = state == "RUNNING";

        SvcStatusText.Text = state switch
        {
            "RUNNING" => "Running",
            "STOPPED" => "Stopped",
            "START_PENDING" => "Starting\u2026",
            "STOP_PENDING" => "Stopping\u2026",
            "Not Installed" => "Not Installed",
            "Pending Deletion" => "Pending Deletion",
            _ => state,
        };

        (SvcStatusBadge.Background, SvcStatusText.Foreground) = state switch
        {
            "RUNNING" => ((Brush)new SolidColorBrush(Color.FromRgb(0xDF, 0xF6, 0xDD)), new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F))),
            "STOPPED" => (new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE9)), new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C))),
            "Not Installed" => (new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61))),
            "Pending Deletion" => (new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)), new SolidColorBrush(Color.FromRgb(0x7A, 0x57, 0x00))),
            _ => (new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)), new SolidColorBrush(Color.FromRgb(0x7A, 0x57, 0x00))),
        };

        BtnStart.IsEnabled = !running && exists;
        BtnStop.IsEnabled = running;
        BtnRestart.IsEnabled = running;
        BtnInstall.IsEnabled = !exists;
        BtnUninstall.IsEnabled = exists;
    }

    private static string PollForState(string successState, string failState, int seconds)
    {
        for (int i = 0; i < seconds * 2; i++)
        {
            Thread.Sleep(500);
            var (s, _) = QueryServiceState();
            if (s == successState || s == failState) return s;
        }
        return QueryServiceState().state;
    }

    private static (string text, string sub) GetServiceUptime()
    {
        try
        {
            // sc.exe queryex gives us the PID when the service is RUNNING
            var (state, _, pid) = Query();
            if (state != "RUNNING") return ("—", "service stopped");
            if (pid == 0) return ("—", "PID not found");

            using var proc = Process.GetProcessById((int)pid);
            var elapsed = DateTime.Now - proc.StartTime;

            string text = elapsed.TotalDays >= 1
                ? $"{(int)elapsed.TotalDays}d {elapsed.Hours}h"
                : elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                    : $"{elapsed.Minutes}m {elapsed.Seconds}s";

            string sub = $"since {proc.StartTime:dd.MM.yyyy HH:mm}";
            return (text, sub);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ("—", "process not accessible");
        }
        catch
        {
            return ("—", "service stopped");
        }
    }

    private static (string state, bool exists) QueryServiceState()
    {
        var (state, exists, _) = Query();
        return (state, exists);
    }

    private void SetSvcButtons(bool enabled) =>
        BtnStart.IsEnabled = BtnStop.IsEnabled = BtnRestart.IsEnabled =
        BtnInstall.IsEnabled = BtnUninstall.IsEnabled = enabled;

    private void ShowFeedback(string msg, bool isError = false)
    {
        SvcFeedback.Text = msg;
        SvcFeedback.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C))
            : new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61));
        SvcFeedback.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetSvcButtons(false);
            ShowFeedback("Starting service\u2026");
            var (scOut, scExit) = await Task.Run(() => RunSc($"start \"{SvcName}\""));
            if (scExit != 0 && scExit != 1053)
            {
                ShowFeedback($"Start failed (exit {scExit}):\n{scOut.Trim()}", true);
                UpdateServiceStatus();
                return;
            }
            ShowFeedback("Waiting for service\u2026");
            var state = await Task.Run(() => PollForState("RUNNING", "STOPPED", 15));
            ShowFeedback(state == "RUNNING" ? "Service started." : "Service did not start. " + StartFailureHint(), state != "RUNNING");
            UpdateServiceStatus();
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("StatusPage", ex, "Service start failed");
            ShowFeedback($"Unexpected error: {ex.Message}", true);
            UpdateServiceStatus();
        }
    }

    /// <summary>
    /// A framework-dependent service exe cannot start without the .NET runtime \u2014
    /// point straight at the cause instead of the generic "check the log".
    /// </summary>
    private static string StartFailureHint()
        => DotNetRuntimeCheck.IsServiceRuntimeInstalled()
            ? "Check the log file for errors."
            : DotNetRuntimeCheck.MissingRuntimeHint;

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetSvcButtons(false);
            ShowFeedback("Stopping service\u2026");
            var (scOut, exit) = await Task.Run(() => RunSc($"stop \"{SvcName}\""));
            ShowFeedback(exit == 0 ? "Service stopped." : $"Stop failed (exit {exit}):\n{scOut.Trim()}", exit != 0);
            UpdateServiceStatus();
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("StatusPage", ex, "Service stop failed");
            ShowFeedback($"Unexpected error: {ex.Message}", true);
            UpdateServiceStatus();
        }
    }

    private async void BtnRestart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetSvcButtons(false);
            ShowFeedback("Restarting service\u2026");
            await Task.Run(() =>
            {
                RunSc($"stop \"{SvcName}\"");
                for (int i = 0; i < 20; i++)
                {
                    var (out2, _) = RunSc($"query \"{SvcName}\"");
                    if (out2.Contains("STOPPED")) break;
                    Thread.Sleep(500);
                }
                RunSc($"start \"{SvcName}\"");
            });
            ShowFeedback("Service restarted.");
            UpdateServiceStatus();
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("StatusPage", ex, "Service restart failed");
            ShowFeedback($"Unexpected error: {ex.Message}", true);
            UpdateServiceStatus();
        }
    }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // GraphMailer.exe must sit next to this executable (same build output folder)
            var svcExe = Path.Combine(AppContext.BaseDirectory, "GraphMailer.exe");

            if (!File.Exists(svcExe))
            {
                ShowFeedback($"GraphMailer.exe not found in:\n{AppContext.BaseDirectory}\n\nBuild the service project first.", true);
                return;
            }
            if (!DotNetRuntimeCheck.IsServiceRuntimeInstalled())
            {
                ShowFeedback("Cannot install the service: " + DotNetRuntimeCheck.MissingRuntimeHint, true);
                return;
            }
            SetSvcButtons(false);
            ShowFeedback("Installing service\u2026");
            var (phase, code, scMsg) = await Task.Run<(string phase, int code, string scMsg)>(() =>
            {
                var (createOut, createExit) = RunSc($"create \"{SvcName}\" binPath= \"{svcExe}\" start= auto DisplayName= \"GraphMailer SMTP Relay\"");
                if (createExit != 0) return ("install_failed", createExit, createOut);
                RunSc($"description \"{SvcName}\" \"SMTP relay service that delivers emails via Microsoft 365 Graph API\"");
                var (startOut, startExit) = RunSc($"start \"{SvcName}\"");
                if (startExit != 0 && startExit != 1053) return ("start_failed", startExit, startOut);
                return ("starting", 0, "");
            });
            if (phase == "install_failed") { ShowFeedback($"Install failed (exit {code}):\n{scMsg.Trim()}", true); UpdateServiceStatus(); return; }
            if (phase == "start_failed") { ShowFeedback($"Installed, but start failed (exit {code}):\n{scMsg.Trim()}", true); UpdateServiceStatus(); return; }
            ShowFeedback("Waiting for service\u2026");
            var state = await Task.Run(() => PollForState("RUNNING", "STOPPED", 15));
            ShowFeedback(state == "RUNNING" ? "Service installed and started." : "Installed, but service did not start. " + StartFailureHint(), state != "RUNNING");
            UpdateServiceStatus();
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("StatusPage", ex, "Service install failed");
            ShowFeedback($"Unexpected error: {ex.Message}", true);
            UpdateServiceStatus();
        }
    }

    private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MessageBox.Show(
                "This will stop and remove the GraphMailer Windows Service. Continue?",
                "Uninstall Service",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            SetSvcButtons(false);
            _timer.Stop();
            ShowFeedback("Uninstalling service\u2026");
            var (scOut, exit) = await Task.Run(() =>
            {
                RunSc($"stop \"{SvcName}\"");
                Thread.Sleep(1500);
                return RunSc($"delete \"{SvcName}\"");
            });
            if (exit != 0)
            {
                ShowFeedback($"Uninstall failed (exit {exit}):\n{scOut.Trim()}", true);
                _timer.Start();
                UpdateServiceStatus();
                return;
            }
            // sc delete succeeded — stop polling sc query for 3 s so the pending-deletion
            // handle can close and the service entry disappears from SCM.
            ShowFeedback("Waiting for service entry to be removed\u2026");
            await Task.Delay(3000);
            _timer.Start();
            var (finalState, _) = QueryServiceState();
            if (finalState == "Not Installed")
                ShowFeedback("Service removed.");
            else
                ShowFeedback("Service is marked for deletion. It will be fully removed after the next system restart.", true);
            UpdateServiceStatus();
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("StatusPage", ex, "Service uninstall failed");
            ShowFeedback($"Unexpected error: {ex.Message}", true);
            _timer.Start();
            UpdateServiceStatus();
        }
    }

    private static IEnumerable<HealthRow> BuildHealthRows()
    {
        var now = DateTime.UtcNow;
        var checkTime = now.ToLocalTime().ToString("HH:mm:ss");
        // Query service state once and share it with checks that need it
        var (svcState, _) = QueryServiceState();
        return
        [
            CheckSmtpServiceHealth(svcState, checkTime),
            CheckSecretsHealth(checkTime),
            CheckCertHealth(checkTime),
            CheckPortsHealth(svcState, checkTime),
            CheckDiskHealth(checkTime),
            CheckQueueHealth(checkTime),
            CheckGraphApiHealth(now, checkTime),
        ];
    }

    private static HealthRow CheckSmtpServiceHealth(string svcState, string checkTime)
    {
        return svcState switch
        {
            "RUNNING" => new HealthRow("SMTP Service", "OK", "Running", checkTime),
            "Not Installed" => new HealthRow("SMTP Service", "Error", "Not installed", checkTime),
            _ => new HealthRow("SMTP Service", "Warning", svcState, checkTime),
        };
    }

    private const string SecretsComponent = "Config Secrets";

    // Config protector shares the service's Data Protection key ring; build it once.
    private static IDataProtector? _configProtector;
    private static bool _configProtectorTried;

    private static IDataProtector? GetConfigProtector()
    {
        if (_configProtectorTried) return _configProtector;
        _configProtectorTried = true;
        try
        {
            _configProtector = DataProtectionExtensions.BuildConfigProtector();
        }
        catch
        {
            _configProtector = null; // registry/key ring not accessible
        }
        return _configProtector;
    }

    /// <summary>
    /// Verifies that every <c>ENC[...]</c> value in graphmailer.json can be decrypted with the
    /// current Data Protection key ring. Runs independently of the service, so an operator can
    /// spot a key-ring/config mismatch (e.g. after a restore) before the service is even started.
    /// </summary>
    private static HealthRow CheckSecretsHealth(string checkTime)
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFilePath))
                return new HealthRow(SecretsComponent, "Unknown", "No configuration file", checkTime);

            var protector = GetConfigProtector();
            if (protector is null)
                return new HealthRow(SecretsComponent, "Unknown", "Data Protection key ring not accessible", checkTime);

            var json = File.ReadAllText(AppPaths.ConfigFilePath);

            SecretIntegrityChecker.SecretScanResult scan;
            try
            {
                scan = SecretIntegrityChecker.Scan(json, protector);
            }
            catch (JsonException)
            {
                return new HealthRow(SecretsComponent, "Unknown", "Configuration is not valid JSON", checkTime);
            }

            if (scan.TotalEncrypted == 0)
                return new HealthRow(SecretsComponent, "Unknown", "No encrypted secrets configured", checkTime);

            if (scan.Undecryptable.Count == 0)
                return new HealthRow(SecretsComponent, "OK",
                    $"{scan.TotalEncrypted} encrypted secret(s) decryptable", checkTime);

            return new HealthRow(SecretsComponent, "Error",
                "Cannot decrypt: " + string.Join(", ", scan.Undecryptable), checkTime);
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("StatusPage", ex, "Secrets health check failed");
            return new HealthRow(SecretsComponent, "Unknown", ex.Message, checkTime);
        }
    }

    private bool _secretsBannerDismissed;

    private void UpdateSecretsBanner(HealthRow? secretsRow)
    {
        bool hasError = secretsRow?.Status == "Error";

        // Reset the dismissal once the problem clears, so a later recurrence shows again.
        if (!hasError)
        {
            _secretsBannerDismissed = false;
            SecretsBanner.Visibility = Visibility.Collapsed;
            return;
        }

        if (_secretsBannerDismissed)
        {
            SecretsBanner.Visibility = Visibility.Collapsed;
            return;
        }

        SecretsBannerText.Text =
            $"⚠ {secretsRow!.Detail}. Re-enter the affected value(s) in the configuration " +
            "to re-encrypt them with the current key.";
        SecretsBanner.Visibility = Visibility.Visible;
    }

    private void SecretsBannerDismiss_Click(object sender, MouseButtonEventArgs e)
    {
        _secretsBannerDismissed = true;
        SecretsBanner.Visibility = Visibility.Collapsed;
    }

    private static HealthRow CheckCertHealth(string checkTime)
    {
        try
        {
            string? subjectName = null;
            if (File.Exists(AppPaths.ConfigFilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigFilePath));
                if (doc.RootElement.TryGetProperty("Certificate", out var certSec) &&
                    certSec.TryGetProperty("SubjectName", out var sn))
                    subjectName = sn.GetString();
            }

            if (string.IsNullOrEmpty(subjectName))
                return new HealthRow("TLS Certificate", "Unknown", "SubjectName not configured", checkTime);

            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var cert = store.Certificates
                .Cast<X509Certificate2>()
                .Where(c => c.Subject.Contains(subjectName, StringComparison.OrdinalIgnoreCase)
                         || c.GetNameInfo(X509NameType.DnsName, false)
                              .Contains(subjectName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();

            if (cert is null)
                return new HealthRow("TLS Certificate", "Error", $"No cert found for '{subjectName}'", checkTime);

            var daysLeft = (cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
            var detail = $"{subjectName} — expires {cert.NotAfter:yyyy-MM-dd} ({(int)daysLeft}d)";
            return daysLeft < 0 ? new HealthRow("TLS Certificate", "Error", "EXPIRED: " + detail, checkTime)
                 : daysLeft < 14 ? new HealthRow("TLS Certificate", "Warning", detail, checkTime)
                                 : new HealthRow("TLS Certificate", "OK", detail, checkTime);
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("StatusPage", ex, "TLS certificate health check failed");
            return new HealthRow("TLS Certificate", "Unknown", ex.Message, checkTime);
        }
    }

    private static HealthRow CheckPortsHealth(string svcState, string checkTime)
    {
        try
        {
            // Read SMTP server ports from graphmailer.json
            var ports = new List<(int port, string name)>();
            if (File.Exists(AppPaths.ConfigFilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigFilePath));
                if (doc.RootElement.TryGetProperty("Servers", out var servers) &&
                    servers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in servers.EnumerateArray())
                    {
                        var port = entry.TryGetProperty("Port", out var p) ? p.GetInt32() : 2525;
                        var name = entry.TryGetProperty("Name", out var n) ? n.GetString() ?? "SMTP" : "SMTP";
                        ports.Add((port, name));
                    }
                }
            }

            if (ports.Count == 0)
            {
                // Fall back to appsettings.json (bundled defaults next to the service exe)
                var appSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(appSettings))
                {
                    using var doc2 = JsonDocument.Parse(File.ReadAllText(appSettings));
                    if (doc2.RootElement.TryGetProperty("Servers", out var servers2) &&
                        servers2.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in servers2.EnumerateArray())
                        {
                            var port = entry.TryGetProperty("Port", out var p) ? p.GetInt32() : 2525;
                            var name = entry.TryGetProperty("Name", out var n) ? n.GetString() ?? "SMTP" : "SMTP";
                            ports.Add((port, name));
                        }
                    }
                }
            }

            if (ports.Count == 0)
                return new HealthRow("SMTP Ports", "Unknown", "No SMTP servers configured", checkTime);

            var results = new List<string>();
            bool anyFailed = false;
            bool anyConflict = false;

            // Resolve port → owning process name via netstat + Process.GetProcessById.
            // IPGlobalProperties.GetActiveTcpListeners() only gives endpoints, not PIDs,
            // so we parse "netstat -ano" (no extra packages, no P/Invoke).
            var portOwner = GetListeningPortOwners();

            bool serviceRunning = svcState == "RUNNING";

            foreach (var (port, name) in ports)
            {
                if (portOwner.TryGetValue(port, out var ownerName))
                {
                    bool ownedByUs = ownerName.Equals("GraphMailer", StringComparison.OrdinalIgnoreCase);
                    if (ownedByUs)
                        results.Add($"{name}:{port} OK");
                    else
                    {
                        results.Add($"{name}:{port} occupied by '{ownerName}'");
                        anyConflict = true;
                    }
                }
                else
                {
                    results.Add($"{name}:{port} not listening");
                    anyFailed = true;
                }
            }

            var detail = string.Join(", ", results);
            if (anyConflict)
                return new HealthRow("SMTP Ports", "Warning", detail, checkTime);
            // If service is not running, port being down is expected — show Warning not Error
            if (anyFailed && !serviceRunning)
                return new HealthRow("SMTP Ports", "Warning", detail + " (service stopped)", checkTime);
            return anyFailed
                ? new HealthRow("SMTP Ports", "Error", detail, checkTime)
                : new HealthRow("SMTP Ports", "OK", detail, checkTime);
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("StatusPage", ex, "SMTP ports health check failed");
            return new HealthRow("SMTP Ports", "Unknown", ex.Message, checkTime);
        }
    }

    /// <summary>
    /// Returns a dictionary mapping TCP LISTEN port → owning process name.
    /// Uses P/Invoke <c>GetExtendedTcpTable</c> (iphlpapi.dll) — locale-independent,
    /// no text parsing, no child process, no external package.
    /// Falls back to an empty dictionary on any error.
    /// </summary>
    private static Dictionary<int, string> GetListeningPortOwners()
    {
        var result = new Dictionary<int, string>();
        try
        {
            // TCP_TABLE_OWNER_PID_LISTENER = 3  →  only LISTEN rows + PID
            const int AF_INET = 2;
            const int tableClass = 3;

            int bufLen = 0;
            NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref bufLen, false, AF_INET, tableClass, 0);

            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(bufLen);
            try
            {
                if (NativeMethods.GetExtendedTcpTable(buf, ref bufLen, false, AF_INET, tableClass, 0) != 0)
                    return result;

                int rowCount = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                int offset = 4; // skip dwNumEntries

                for (int i = 0; i < rowCount; i++)
                {
                    // MIB_TCPROW_OWNER_PID: dwState(4) + dwLocalAddr(4) + dwLocalPort(4) + dwRemoteAddr(4) + dwRemotePort(4) + dwOwningPid(4)
                    int localPortNetOrder = System.Runtime.InteropServices.Marshal.ReadInt32(buf, offset + 8);
                    int pid = System.Runtime.InteropServices.Marshal.ReadInt32(buf, offset + 20);
                    offset += 24;

                    // Port is stored as a DWORD in network byte order in the low 16 bits
                    // e.g. port 25 (0x0019) is stored as bytes 0x19,0x00,0x00,0x00 → ReadInt32 = 0x1900
                    int port = System.Net.IPAddress.NetworkToHostOrder((short)(localPortNetOrder & 0xFFFF)) & 0xFFFF;
                    if (result.ContainsKey(port)) continue;

                    try { result[port] = Process.GetProcessById(pid).ProcessName; }
                    catch { result[port] = $"PID {pid}"; }
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
            }
        }
        catch { /* best-effort */ }
        return result;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
            int ipVersion, int tableClass, int reserved);
    }

    private static HealthRow CheckDiskHealth(string checkTime)
    {
        try
        {
            var dir = Directory.Exists(AppPaths.MailDir) ? AppPaths.MailDir : AppPaths.BaseDir;
            var root = Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return new HealthRow("Disk Space", "Warning", $"Drive {root} not ready", checkTime);

            var freePct = drive.AvailableFreeSpace * 100.0 / drive.TotalSize;
            var freeGb = drive.AvailableFreeSpace / 1_073_741_824.0;
            var detail = $"{freeGb:F1} GB free ({freePct:F1}%) on {root}";
            return freePct < 5 ? new HealthRow("Disk Space", "Error", detail, checkTime)
                 : freePct < 10 ? new HealthRow("Disk Space", "Warning", detail, checkTime)
                                : new HealthRow("Disk Space", "OK", detail, checkTime);
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("StatusPage", ex, "Disk space health check failed");
            return new HealthRow("Disk Space", "Unknown", ex.Message, checkTime);
        }
    }

    private static HealthRow CheckQueueHealth(string checkTime)
    {
        try
        {
            var queueDir = Path.Combine(AppPaths.MailDir, "queue");
            var failedDir = Path.Combine(AppPaths.MailDir, "failed");
            var queued = Directory.Exists(queueDir) ? Directory.GetFiles(queueDir, "*.meta.json").Length : 0;
            var failed = Directory.Exists(failedDir) ? Directory.GetFiles(failedDir, "*.meta.json").Length : 0;
            var detail = $"{queued} queued, {failed} failed";
            return failed > 0
                ? new HealthRow("Mail Queue", "Warning", detail, checkTime)
                : new HealthRow("Mail Queue", "OK", detail, checkTime);
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("StatusPage", ex, "Mail queue health check failed");
            return new HealthRow("Mail Queue", "Unknown", ex.Message, checkTime);
        }
    }

    private static HealthRow CheckGraphApiHealth(DateTime now, string checkTime)
    {
        try
        {
            if (!File.Exists(DbPath))
                return new HealthRow("Graph API", "Unknown", "No delivery data", checkTime);

            using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(occurred_at) FROM email_events WHERE event_type='sent'";
            var result = cmd.ExecuteScalar();

            if (result is null or DBNull)
                return new HealthRow("Graph API", "Unknown", "No deliveries recorded", checkTime);

            var lastSent = DateTime.Parse((string)result, null,
                System.Globalization.DateTimeStyles.RoundtripKind);
            var ago = now - lastSent;
            var agoStr = ago.TotalDays >= 1 ? $"{(int)ago.TotalDays}d ago"
                       : ago.TotalHours >= 1 ? $"{(int)ago.TotalHours}h ago"
                                             : $"{(int)ago.TotalMinutes}m ago";
            return new HealthRow("Graph API", "OK", $"Last delivery: {agoStr}", checkTime);
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("StatusPage", ex, "Graph API health check failed");
            return new HealthRow("Graph API", "Unknown", ex.Message, checkTime);
        }
    }

    private record HealthRow(string Component, string Status, string Detail, string LastCheck)
    {
        public string StatusBg => Status switch
        {
            "OK" => "#FFDFF6DD",
            "Warning" => "#FFFFF4CE",
            "Error" => "#FFFDE7E9",
            _ => "#FFF0F0F0",
        };
        public string StatusFg => Status switch
        {
            "OK" => "#FF0F7B0F",
            "Warning" => "#FF7A5700",
            "Error" => "#FFC42B1C",
            _ => "#FF616161",
        };
    }
}
