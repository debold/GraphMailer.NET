using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.ConfigTool.Views;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Backup;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Encryption;
using GraphMailer.Service.Services.UpdateCheck;

namespace GraphMailer.ConfigTool;

public partial class MainWindow : Window
{
    private Border? _activeNavItem;
    // Help page (relative to the bundled help\ folder) for the currently shown screen;
    // opened by the toolbar help icon / F1. Set by NavigateTo.
    private string _currentHelpPage = "index.html";
    private bool _isDirty;
    private bool _suppressDirty;
    private bool _pagesInitialized;
    private bool _restartRequired;
    private bool _serviceWasRunning;
    private uint _lastServicePid;
    // Restart badge state: visible while the restart-relevant config on disk differs
    // from the config the running service loaded at its last (re)start.
    private string _serviceSnapshot = "";
    private string _diskSnapshot = "";
    // Forces the restart badge regardless of the snapshot diff — set after a restore
    // (which replaces the whole config, so the running service is always out of date).
    private bool _restartForced;
    private readonly DispatcherTimer _statusTimer;
    private FileSystemWatcher? _configWatcher;
    private readonly DispatcherTimer _configReloadDebounce;

    private readonly ConfigService _configService;
    private readonly IConfigBackupService _backupService;
    private ConfigDocument _currentDoc;

    // Pages — config pages are created eagerly in constructor; others lazily on first navigation
    private StatusPage? _statusPage;
    private SmtpPage _smtpPage = null!;
    private AccessControlPage _accessPage = null!;
    private IpFilteringPage _ipFilteringPage = null!;
    private GraphApiPage _graphApiPage = null!;
    private MailQueuePage _queuePage = null!;
    private MonitoringPage _monitoringPage = null!;
    private NotificationsPage _notificationsPage = null!;
    private BackupPage _backupPage = null!;
    private MetricsPage? _metricsPage;
    private MessagesPage? _messagesPage;
    private LogPage? _logPage;

    public MainWindow()
    {
        InitializeComponent();
        ClampToWorkArea();

        // Initialise ConfigService using the same Data Protection key ring as the service
        var configPath = AppPaths.ConfigFilePath;
        var configProtector = DataProtectionExtensions.BuildConfigProtector();
        _configService = new ConfigService(configPath, configProtector);
        _backupService = new ConfigBackupService(configProtector, configPath);

        // Migrate the config schema before loading (backs up the original; no-op when current).
        var migration = ConfigMigrator.MigrateFile(configPath);
        if (migration.Incompatible)
            MessageBox.Show(
                $"This configuration was written by a newer version of GraphMailer (schema v{migration.From}; this tool supports v{ConfigSchema.Current}).\n\n" +
                "It is shown as-is, but settings added by the newer version are not editable here. Please update GraphMailer.",
                "Newer configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (migration.Migrated)
            MessageBox.Show(
                $"The configuration was migrated from schema v{migration.From} to v{migration.To}.\n\n" +
                $"A backup of the original was saved to:\n{migration.BackupPath}",
                "Configuration migrated", MessageBoxButton.OK, MessageBoxImage.Information);

        _currentDoc = LoadConfig();
        // Assume the running service uses the config currently on disk
        _diskSnapshot = _serviceSnapshot = MakeRestartSnapshot(_currentDoc);

        // Debounce timer: FSW fires multiple events per write; wait 800ms after last event
        _configReloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _configReloadDebounce.Tick += (_, _) =>
        {
            _configReloadDebounce.Stop();
            OnConfigFileChanged();
        };

        // Create all config pages eagerly so CollectTo always works,
        // even for pages the user never navigates to.
        // _suppressDirty prevents TextChanged / SelectionChanged events from
        // marking the form dirty during the initial LoadFrom calls.
        _suppressDirty = true;
        try
        {
            _smtpPage = new SmtpPage(MarkDirty); _smtpPage.LoadFrom(_currentDoc);
            _accessPage = new AccessControlPage(
                MarkDirty,
                () => { try { return _configService.Load(); } catch { return null; } },
                () => _serviceWasRunning);
            _accessPage.LoadFrom(_currentDoc);
            _ipFilteringPage = new IpFilteringPage(MarkDirty); _ipFilteringPage.LoadFrom(_currentDoc);
            _graphApiPage = new GraphApiPage(MarkDirty, b => _suppressDirty = b); _graphApiPage.LoadFrom(_currentDoc);
            _queuePage = new MailQueuePage(MarkDirty); _queuePage.LoadFrom(_currentDoc);
            _monitoringPage = new MonitoringPage(MarkDirty); _monitoringPage.LoadFrom(_currentDoc);
            // The sender address (Notifications page) and the emailed-backups toggle
            // (Backup page) validate against each other — wired via lazy callbacks so
            // each page always sees the other's LIVE state. Construction order matters:
            // the Backup page's callback runs during its LoadFrom below, when the
            // Notifications page already exists; the reverse callback null-checks.
            _notificationsPage = new NotificationsPage(MarkDirty, () => _backupPage?.IsEmailBackupEnabled == true);
            _notificationsPage.LoadFrom(_currentDoc);
            _backupPage = new BackupPage(MarkDirty, _backupService, OnConfigRestored, () => _notificationsPage.HasSenderAddress);
            _backupPage.LoadFrom(_currentDoc);
        }
        finally
        {
            _suppressDirty = false;
        }

        _pagesInitialized = true;
        UpdateNavBadges();

        // No config file yet → defaults are in memory but not on disk; prompt the user to save.
        if (!_configService.FileExists)
            MarkDirty();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => RefreshServiceStatus();
        _statusTimer.Start();
        RefreshServiceStatus();

        // Watch config file for external changes (e.g. service capturing a password)
        StartConfigWatcher(AppPaths.ConfigFilePath);

        // Start on Status page
        NavigateTo(NavStatus, "Status", "monitoring/status.html", () => _statusPage ??= new StatusPage());
    }

    // On screens smaller than the designed window size (low-resolution consoles, RDP
    // sessions) the fixed 1180x860 start size would extend past the screen edge, and a
    // MinWidth/MinHeight larger than the screen would keep the user from ever shrinking
    // it back into view. Clamp both to the work area (screen minus taskbar, in DIUs);
    // combined with WindowStartupLocation=CenterScreen the window is always fully
    // visible. All pages scroll, so a smaller-than-designed window stays usable.
    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
    }

    private ConfigDocument LoadConfig()
    {
        ConfigDocument doc;
        try { doc = _configService.Load(); }
        catch (ConfigLoadException ex)
        {
            ConfigToolLog.Error("MainWindow", ex, "Configuration file could not be read");
            MessageBox.Show(
                $"The configuration file could not be read:\n{ex.Message}\n\nStarting with defaults.",
                "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new ConfigDocument();
        }

        // A single undecryptable secret no longer aborts the load: the rest of the config
        // is loaded and the affected fields are left blank. Warn the operator which ones.
        if (doc.DecryptionFailures.Count > 0)
        {
            var fields = string.Join("\n", doc.DecryptionFailures.Select(f => $"  • {f}"));
            MessageBox.Show(
                "The configuration was loaded, but the following encrypted value(s) could not be " +
                $"decrypted with the current key and are shown blank:\n\n{fields}\n\n" +
                "Re-enter them and save to re-encrypt with the current key. " +
                "(This typically happens when the config was restored to a different machine.)",
                "Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return doc;
    }

    /// <summary>
    /// Shows a badge on the nav items whose page still contains an undecryptable secret,
    /// so the operator can find the affected page without opening each one. Driven by the
    /// pages' live marker state, so it clears as soon as a value is re-entered or saved.
    /// </summary>
    private void UpdateNavBadges()
    {
        if (!_pagesInitialized) return;
        NavGraphApiBadge.Visibility = _graphApiPage.HasUndecryptableSecret
            ? Visibility.Visible : Visibility.Collapsed;
        NavAccessBadge.Visibility = _accessPage.HasUndecryptablePassword
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Config file watcher ─────────────────────────────────────────────────────

    private void StartConfigWatcher(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        var file = Path.GetFileName(filePath);
        if (dir is null || file is null || !Directory.Exists(dir)) return;

        _configWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        // FSW callbacks arrive on thread-pool — restart debounce on UI thread
        _configWatcher.Changed += (_, _) =>
            Dispatcher.BeginInvoke(() =>
            {
                _configReloadDebounce.Stop();
                _configReloadDebounce.Start();
            });
    }

    private void OnConfigFileChanged()
    {
        // Only act when there are users with capture pending
        if (!_accessPage.HasPendingCaptures) return;

        ConfigDocument freshDoc;
        try { freshDoc = _configService.Load(); }
        catch { return; }  // file may be mid-write; next FSW event will retry

        bool merged = _accessPage.MergeCapturedPasswords(freshDoc);
        if (merged)
        {
            // Update the in-memory doc so a subsequent Save picks up the captured passwords
            _accessPage.CollectTo(_currentDoc);
            // Mark dirty so the user knows there are unsaved in-memory changes
            // (the captured password is in-memory but the rest of the form may differ)
            MarkDirty();
        }
    }

    // ── Service status ────────────────────────────────────────────────────

    private async void RefreshServiceStatus()
    {
        // sc.exe can block while the SCM is busy (e.g. a wedged service in
        // STOP_PENDING) — keep the spawn off the UI thread.
        var state = await Task.Run(QueryServiceState);
        ApplyServiceState(state);
    }

    private static (string Label, string Sub, Color Dot, uint Pid) QueryServiceState()
    {
        try
        {
            var (state, _, pid) = ServiceControl.Query();
            return state switch
            {
                "RUNNING"          => ("Service running",   "GraphMailer",                  Color.FromRgb(0x6C, 0xCB, 0x5F), pid),
                "STOPPED"          => ("Service stopped",   "Go to Status page to start",   Color.FromRgb(0xC4, 0x2B, 0x1C), 0u),
                "START_PENDING"    => ("Service starting…", "",                             Color.FromRgb(0xF9, 0xCB, 0x45), 0u),
                "STOP_PENDING"     => ("Service stopping…", "",                             Color.FromRgb(0xF9, 0xCB, 0x45), 0u),
                "Not Installed"    => ("Not installed",     "Go to Status page to install", Color.FromRgb(0x88, 0x88, 0x88), 0u),
                "Pending Deletion" => ("Pending deletion",  "Removed after next reboot",    Color.FromRgb(0x88, 0x88, 0x88), 0u),
                _                  => ("Unknown",           "sc.exe queryex failed",        Color.FromRgb(0x88, 0x88, 0x88), 0u),
            };
        }
        catch (Exception ex)
        {
            ConfigToolLog.ErrorOnChange("MainWindow", ex, "Service status query failed");
            return ("Error", ex.Message, Color.FromRgb(0x88, 0x88, 0x88), 0);
        }
    }

    private void ApplyServiceState((string Label, string Sub, Color Dot, uint Pid) state)
    {
        bool isNowRunning = state.Label == "Service running";
        bool isDefinitive = isNowRunning
            || state.Label is "Service stopped" or "Not installed" or "Pending deletion";

        // Transient results (pending states, sc.exe errors) must not touch the
        // restart bookkeeping: a single failed poll between two RUNNING polls
        // would otherwise look like a completed restart.
        if (isDefinitive)
        {
            if (_restartRequired && isNowRunning)
            {
                // Slow restart: STOPPED state was observed between polls.
                // Fast restart: STOPPED was missed but the PID changed while running.
                if (!_serviceWasRunning ||
                    (state.Pid != 0 && _lastServicePid != 0 && state.Pid != _lastServicePid))
                    OnServiceRestartDetected();
            }

            _serviceWasRunning = isNowRunning;
            _lastServicePid = isNowRunning && state.Pid != 0 ? state.Pid : 0;
        }

        ServiceStatusText.Text = state.Label;
        ServiceSinceText.Text = state.Sub;
        ServiceStatusDot.Fill = new SolidColorBrush(state.Dot);

        RefreshUpdateBadge();
    }

    /// <summary>
    /// Shows the green update pill in the sidebar header while data\update-status.json
    /// reports a newer release (written by the service's weekly update check).
    /// Piggybacks on the 5s status poller — the read is a tiny local file.
    /// </summary>
    private void RefreshUpdateBadge()
    {
        var status = UpdateCheckStatus.TryLoad(UpdateCheckStatus.StatusFilePath);
        if (status is { UpdateAvailable: true, LatestVersion: not null })
        {
            UpdateBadgeText.Text = $"↑ {status.LatestVersion}";
            UpdateBadge.ToolTip =
                $"Update available: {status.LatestVersion} (installed: {BuildInfo.FileVersion}).\n" +
                "Click for details on the Status page.";
            UpdateBadge.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
        }
    }

    // ── Navigation handlers ──────────────────────────────────────────────────

    private void NavStatus_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavStatus, "Status", "monitoring/status.html", () => _statusPage ??= new StatusPage());

    private void NavSmtp_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavSmtp, "Servers & TLS", "configuration/servers-tls.html", () => _smtpPage!);

    private void NavAccess_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavAccess, "Access Control", "configuration/access-control.html", () => _accessPage!);

    private void NavIpFiltering_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavIpFiltering, "IP Filtering", "configuration/ip-filtering.html", () => _ipFilteringPage!);

    private void NavGraphApi_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavGraphApi, "Graph API", "configuration/graph-api.html", () => _graphApiPage!);

    private void NavQueue_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavQueue, "Mail Queue", "configuration/mail-queue.html", () => _queuePage!);

    private void NavMonitoring_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavMonitoring, "Monitoring", "configuration/monitoring.html", () => _monitoringPage!);

    private void NavNotifications_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavNotifications, "Notifications", "configuration/notifications.html", () => _notificationsPage!);

    private void NavBackup_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavBackup, "Backup & Restore", "configuration/backup-restore.html", () => _backupPage!);

    private void NavMetrics_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavMetrics, "Metrics", "monitoring/metrics.html", () => _metricsPage ??= new MetricsPage());

    private void NavMessages_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavMessages, "Messages", "monitoring/messages.html", () => _messagesPage ??= new MessagesPage(
            () => (_currentDoc.MailQueue.MailDir, _currentDoc.MailQueue.ArchiveSentEmails)));

    private void NavLogs_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        => NavigateTo(NavLogs, "Logs", "monitoring/logs.html", () => _logPage ??= new LogPage());

    // ── Toolbar ──────────────────────────────────────────────────────────────

    // Toolbar help icon / F1 → open the bundled help page for the current screen.
    private void Help_Executed(object sender, ExecutedRoutedEventArgs e) => OpenHelp(_currentHelpPage);

    /// <summary>
    /// Opens a help page from the help\ tree bundled next to the EXE (installed layout:
    /// INSTALLFOLDER\help\…). Falls back to the help index, then to a friendly notice when
    /// the help is absent (e.g. a dev build that was not produced by build-installer.ps1).
    /// </summary>
    private static void OpenHelp(string relativePage)
    {
        try
        {
            var helpDir = Path.Combine(AppContext.BaseDirectory, "help");
            var target = Path.Combine(helpDir, relativePage.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(target))
            {
                var index = Path.Combine(helpDir, "index.html");
                target = File.Exists(index) ? index : null!;
            }

            if (target is null)
            {
                MessageBox.Show(
                    "The help files were not found next to the application. The HTML help ships with " +
                    "the installed version of GraphMailer (Start menu → \"GraphMailer Help\").",
                    "Help unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("MainWindow", ex, "Could not open the help");
            MessageBox.Show($"Could not open the help:\n{ex.Message}",
                "Help", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (NumericField.SubtreeHasErrors(this))
        {
            MessageBox.Show(
                "One or more fields contain invalid values (highlighted in red).\nPlease correct them before saving.",
                "Cannot Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_backupPage.HasValidationErrors)
        {
            MessageBox.Show(
                "The Backup & Restore page has invalid settings (shown in red on that page).\nFix them before saving.",
                "Cannot Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Collect changes from all open pages into _currentDoc
        _smtpPage.CollectTo(_currentDoc);
        _accessPage.CollectTo(_currentDoc);
        _ipFilteringPage.CollectTo(_currentDoc);
        _graphApiPage.CollectTo(_currentDoc);
        _queuePage.CollectTo(_currentDoc);
        _monitoringPage.CollectTo(_currentDoc);
        _notificationsPage.CollectTo(_currentDoc);
        _backupPage.CollectTo(_currentDoc);

        // Cross-page rule: several features (notifications, NDRs, reports, emailed
        // backups) can only send with a configured sender mailbox — Graph app-only
        // auth has no fallback account. Checked here because the Backup page's email
        // toggle lives outside the Notifications page.
        if (Helpers.SenderAddressRule.Validate(_currentDoc) is { } senderIssue)
        {
            MessageBox.Show(
                senderIssue + "\n\nSet the sender email address on the Notifications page (or disable the dependent features).",
                "Cannot Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _configService.Save(_currentDoc);
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("MainWindow", ex, "Failed to save configuration");
            var logPath = TryWriteErrorLog("save-error", ex.ToString());
            MessageBox.Show(
                "Failed to save configuration:\n\n" + DescribeException(ex) +
                (logPath is not null ? $"\n\nFull details (with stack trace) written to:\n{logPath}" : ""),
                "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Saving re-encrypts every secret with the current key, so nothing is undecryptable
        // anymore — clear the inline markers and the nav badges.
        _graphApiPage.ClearUndecryptableMarker();
        _accessPage.ClearUndecryptableMarkers();
        UpdateNavBadges();

        // Badge reflects disk vs. running-service config; reverting a change
        // and saving again clears it without a restart.
        _diskSnapshot = MakeRestartSnapshot(_currentDoc);
        UpdateRestartBadge();

        ClearDirty();
        SaveButton.Content = "\u2714 Saved";
        SaveButton.Background = (SolidColorBrush)FindResource("OkBrush");
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { SaveButton.Content = "\U0001f4be Save"; SaveButton.ClearValue(BackgroundProperty); t.Stop(); };
        t.Start();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDirty) return;
        if (MessageBox.Show("Discard all unsaved changes?", "Discard",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        ReloadPagesFromDisk();
    }

    /// <summary>
    /// Re-reads graphmailer.json from disk and reloads every config page. Used by Discard and
    /// after a restore (which rewrites the file). The restart badge then reflects disk-vs-running.
    /// </summary>
    private void ReloadPagesFromDisk()
    {
        _currentDoc = LoadConfig();
        _diskSnapshot = MakeRestartSnapshot(_currentDoc);
        UpdateRestartBadge();

        _suppressDirty = true;
        try
        {
            _smtpPage.LoadFrom(_currentDoc);
            _accessPage.LoadFrom(_currentDoc);
            _ipFilteringPage.LoadFrom(_currentDoc);
            _graphApiPage.LoadFrom(_currentDoc);
            _queuePage.LoadFrom(_currentDoc);
            _monitoringPage.LoadFrom(_currentDoc);
            _notificationsPage.LoadFrom(_currentDoc);
            _backupPage.LoadFrom(_currentDoc);
        }
        catch { _suppressDirty = false; throw; }

        UpdateNavBadges();
        // Keep _suppressDirty = true past WPF's Render pass so deferred DataGrid
        // CheckBox Checked events don't re-show the "Unsaved Changes" badge.
        Dispatcher.InvokeAsync(() => { _suppressDirty = false; ClearDirty(); }, DispatcherPriority.Loaded);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void NavigateTo(Border navItem, string title, string helpPage, Func<UserControl> pageFactory)
    {
        // Deactivate previous — monitoring-group icons (Tag="monitor") get their
        // green tint back, everything else the default nav gray.
        if (_activeNavItem is not null)
        {
            _activeNavItem.Background = Brushes.Transparent;
            if (_activeNavItem.Child is StackPanel sp)
                foreach (TextBlock tb in sp.Children.OfType<TextBlock>())
                    tb.Foreground = tb.Tag is "monitor"
                        ? (SolidColorBrush)FindResource("NavMonitorIconBrush")
                        : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        }

        // Activate new
        navItem.Background = (SolidColorBrush)FindResource("NavActiveBrush");
        if (navItem.Child is StackPanel panel)
            foreach (TextBlock tb in panel.Children.OfType<TextBlock>())
                tb.Foreground = Brushes.White;

        _activeNavItem = navItem;
        PageTitleText.Text = title;
        _currentHelpPage = helpPage;
        _suppressDirty = true;
        try { PageHost.Content = pageFactory(); }
        catch { _suppressDirty = false; throw; }
        // Reset only after WPF has completed the layout/binding pass so that
        // deferred DataGrid CheckBox Checked events (from TwoWay bindings) don't
        // trigger a false "unsaved changes" on first navigation.
        Dispatcher.InvokeAsync(() => _suppressDirty = false, DispatcherPriority.Loaded);
    }

    public void MarkDirty()
    {
        if (_suppressDirty) return;
        _isDirty = true;
        UnsavedBadge.Visibility = Visibility.Visible;
        // An edit may have cleared an undecryptable marker (re-entered secret/password).
        UpdateNavBadges();
    }

    private void ClearDirty()
    {
        _isDirty = false;
        UnsavedBadge.Visibility = Visibility.Collapsed;
    }

    private void UpdateRestartBadge()
    {
        _restartRequired = _restartForced || _diskSnapshot != _serviceSnapshot;
        RestartBadge.Visibility = _restartRequired ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnServiceRestartDetected()
    {
        // The service has (re)started and now runs the config saved on disk.
        _restartForced = false;
        _serviceSnapshot = _diskSnapshot;
        UpdateRestartBadge();
    }

    /// <summary>
    /// Called after a backup is restored: reloads the UI from the new config and forces
    /// the restart badge — a restore replaces the entire configuration, so the running
    /// service is always out of date until it is restarted.
    /// </summary>
    private void OnConfigRestored()
    {
        ReloadPagesFromDisk();
        _restartForced = true;
        UpdateRestartBadge();
    }

    /// <summary>
    /// Returns a fingerprint of all settings that require a service restart when changed.
    /// The log level is deliberately absent: graphmailer.json is loaded with
    /// reloadOnChange and Serilog.Settings.Configuration hot-swaps the minimum level.
    /// </summary>
    /// <summary>
    /// Flattens an exception chain (each inner exception on its own line, with type)
    /// so a "Refer to the inner exception" wrapper actually shows the root cause.
    /// </summary>
    private static string DescribeException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
            sb.AppendLine($"{e.GetType().Name}: {e.Message}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Writes the full exception detail (with stack trace) to a timestamped file under
    /// the logs directory so the operator can share it. Best-effort; returns the path or null.
    /// </summary>
    private static string? TryWriteErrorLog(string prefix, string detail)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var path = Path.Combine(AppPaths.LogsDir,
                $"configtool-{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, $"{DateTime.Now:O}\n\n{detail}\n");
            return path;
        }
        catch
        {
            return null;   // logs dir not writable — the dialog still shows the chain
        }
    }

    private static string MakeRestartSnapshot(ConfigDocument doc) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            doc.Smtp.Banner,
            doc.Smtp.MaxSizeBytes,
            doc.Certificate.StoreLocation,
            doc.Certificate.StoreName,
            doc.Certificate.SubjectName,
            doc.Certificate.Issuer,
            doc.Certificate.FailClosed,
            doc.MailQueue.MailDir,
            doc.MailQueue.PollingIntervalSeconds,
            Servers = doc.Servers
                .Select(e => new { e.Enabled, e.Port, e.Mode, e.AuthMode, e.Name })
                .OrderBy(e => e.Port).ThenBy(e => e.Name)
                .ToArray(),
        });

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isDirty)
        {
            var result = MessageBox.Show(
                "There are unsaved changes. Save before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                Save_Click(this, new RoutedEventArgs());
                // If save failed _isDirty will still be true; let the window close anyway.
            }
            else if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
        _configWatcher?.Dispose();
    }
}
