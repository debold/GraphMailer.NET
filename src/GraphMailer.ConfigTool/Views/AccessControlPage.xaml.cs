using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Services;
using GraphMailer.Service.Services.Advisor;
namespace GraphMailer.ConfigTool.Views;

public partial class AccessControlPage : UserControl
{
    private readonly Action _markDirty;
    private readonly Func<ConfigDocument?>? _liveConfig;
    private readonly Func<bool> _isServiceRunning;
    private readonly DispatcherTimer _syncStatusTimer;
    private readonly ObservableCollection<UserRow> _users = [];
    private readonly ObservableCollection<PatternRow> _senders = [];
    private readonly ObservableCollection<PatternRow> _blockedSenders = [];
    private readonly ObservableCollection<PatternRow> _recipients = [];
    private readonly ObservableCollection<PatternRow> _blockedRecipients = [];
    private readonly ObservableCollection<RouteRow> _routes = [];

    internal AccessControlPage(
        Action markDirty,
        Func<ConfigDocument?>? liveConfig = null,
        Func<bool>? isServiceRunning = null)
    {
        _markDirty = markDirty;
        _liveConfig = liveConfig;
        _isServiceRunning = isServiceRunning ?? (() => true);
        InitializeComponent();
        UsersGrid.ItemsSource = _users;
        SendersGrid.ItemsSource = _senders;
        BlockedSendersGrid.ItemsSource = _blockedSenders;
        RecipientsGrid.ItemsSource = _recipients;
        BlockedRecipientsGrid.ItemsSource = _blockedRecipients;
        RoutesGrid.ItemsSource = _routes;

        // Directory sync status comes from a file the service maintains —
        // poll it only while the page is visible (pattern of Status/Metrics pages)
        _syncStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _syncStatusTimer.Tick += (_, _) => UpdateSyncStatus();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { UpdateSyncStatus(); _syncStatusTimer.Start(); }
            else _syncStatusTimer.Stop();
        };
    }

    private Action? _openRecommendations;

    /// <summary>Badges the card that owns each open recommendation targeting this page; the badge
    /// links to the Recommendations page via <paramref name="openRecommendations"/>.</summary>
    internal void ApplyRecommendations(IReadOnlyList<Recommendation> open, Action openRecommendations)
    {
        _openRecommendations = openRecommendations;
        RecommendationBadgeStyle.ApplyLabel(SenderValidationRecBadge, SenderValidationRecBadgeText,
            [.. open.Where(r => r.Id == RecommendationIds.SenderValidation)]);
    }

    private void RecommendationBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _openRecommendations?.Invoke();

    // ── Sender validation sync status ─────────────────────────────────────

    private void UpdateSyncStatus()
    {
        if (SvSyncStatus is null) return;   // during InitializeComponent

        var status = SenderDirectoryStatus.TryLoad(SenderDirectoryStatus.StatusFilePath);
        var pending = File.Exists(SenderDirectoryStatus.SyncRequestFilePath);
        var serviceRunning = _isServiceRunning();

        // "Sync now" needs both: validation enabled AND a running service to pick it up
        SvSyncNow.IsEnabled = SvEnabled.IsChecked == true && serviceRunning;

        string text;
        if (status?.LastSyncUtc is null)
            text = "No directory sync yet (service must be running with validation enabled).";
        else if (status.LastSyncSuccess)
            text = $"Last sync {status.LastSyncUtc.Value.ToLocalTime():HH:mm:ss}: " +
                   $"{status.UserCount} users, {status.GroupCount} groups, " +
                   $"{status.AddressCount} sender addresses, {status.DomainCount} mail domains" +
                   (status.NextSyncUtc is { } next ? $" · next sync {next.ToLocalTime():HH:mm:ss}" : "");
        else
            text = $"Sync at {status.LastSyncUtc.Value.ToLocalTime():HH:mm:ss} failed: {status.LastError}";

        if (!serviceRunning)
            text += "  · service not running";
        if (pending)
            text += "  — sync requested…";

        SvSyncStatus.Text = text;

        // A part of the sync was skipped for lack of a permission — almost always an option that
        // was switched on without re-running the Entra setup. Say so where the option lives.
        var warning = status?.LastWarning;
        SvSyncWarning.Text = warning ?? "";
        SvSyncWarning.Visibility = string.IsNullOrWhiteSpace(warning)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SvSyncNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SenderDirectoryStatus.SyncRequestFilePath)!);
            File.WriteAllText(SenderDirectoryStatus.SyncRequestFilePath, DateTime.UtcNow.ToString("O"));
            UpdateSyncStatus();
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("AccessControlPage", ex, "Sender directory sync request failed");
            SvSyncStatus.Text = $"Could not request sync: {ex.Message}";
        }
    }

    private void SvShowDirectory_Click(object sender, RoutedEventArgs e)
        => new SenderDirectoryWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    /// <summary>True when at least one user has CaptureNextPassword active.</summary>
    internal bool HasPendingCaptures => _users.Any(u => u.CaptureNextPassword);

    /// <summary>True while at least one user's password is flagged as undecryptable.</summary>
    internal bool HasUndecryptablePassword => _users.Any(u => u.PasswordUndecryptable);

    /// <summary>Clears all undecryptable markers (e.g. after a save re-encrypts secrets).</summary>
    internal void ClearUndecryptableMarkers()
    {
        foreach (var u in _users) u.PasswordUndecryptable = false;
    }

    /// <summary>
    /// Fills the page from a config document.
    ///
    /// Guarded, because every assignment below raises the control's change event, and those
    /// handlers re-evaluate the routing card. Mid-load that state is half-written: ticking
    /// "sender routing enabled" fires before the relay mailbox field has been filled, the card
    /// concludes there is no relay mailbox, and clears the "accept mailbox-less senders" flag the
    /// line above had just restored — so a saved, enabled option came back switched off. The
    /// evaluation therefore runs once, at the end, when every field is in place.
    /// </summary>
    internal void LoadFrom(ConfigDocument doc)
    {
        _loading = true;
        try
        {
            LoadFieldsFrom(doc);
        }
        finally
        {
            _loading = false;
        }

        UpdateRoutingAvailability(fromUser: false);
    }

    private bool _loading;

    private void LoadFieldsFrom(ConfigDocument doc)
    {
        _users.Clear();
        foreach (var u in doc.Access.Users)
            _users.Add(new UserRow { Enabled = u.Enabled, Username = u.Username, DisplayName = u.DisplayName, NewPassword = u.Password, CaptureNextPassword = u.CaptureNextPassword, FromRestrictions = [.. u.FromRestrictions] });

        // Flag users whose stored password could not be decrypted on load. The failure
        // paths use the same ordering as doc.Access.Users, so the index maps directly.
        foreach (var path in doc.DecryptionFailures)
            if (DecryptionFailureMap.UserPasswordIndex(path) is { } i && i < _users.Count)
                _users[i].PasswordUndecryptable = true;

        _senders.Clear();
        foreach (var s in doc.Access.AllowedSenders)
            _senders.Add(new PatternRow { Pattern = s });

        _blockedSenders.Clear();
        foreach (var s in doc.Access.BlockedSenders)
            _blockedSenders.Add(new PatternRow { Pattern = s });

        _recipients.Clear();
        foreach (var r in doc.Access.AllowedRecipients)
            _recipients.Add(new PatternRow { Pattern = r });

        _blockedRecipients.Clear();
        foreach (var r in doc.Access.BlockedRecipients)
            _blockedRecipients.Add(new PatternRow { Pattern = r });

        SvEnabled.IsChecked = doc.SenderValidation.SvEnabled;
        SvRefreshInterval.Text = doc.SenderValidation.SvRefreshIntervalMinutes.ToString();
        SvFailClosed.IsChecked = doc.SenderValidation.SvFailClosed;
        SvAcceptMailboxless.IsChecked = doc.SenderValidation.SvAcceptMailboxless;

        SrEnabled.IsChecked = doc.SenderRouting.SrEnabled;
        SrRelayMailbox.Text = doc.SenderRouting.SrRelayMailbox;

        _routes.Clear();
        foreach (var route in doc.SenderRouting.SrRoutes)
            _routes.Add(new RouteRow { Sender = route.Sender, Mailbox = route.Mailbox });
    }

    /// <summary>
    /// Usernames as currently shown in the grid, including ones not saved yet. Read directly
    /// from the live rows so the Malware Scan page can offer them in its bypass dropdown without
    /// waiting for a save — and without either page having to collect into the document first.
    /// </summary>
    internal IReadOnlyList<string> ConfiguredUsernames =>
        [.. _users.Select(r => r.Username?.Trim() ?? string.Empty)
                  .Where(u => u.Length > 0)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)];

    internal void CollectTo(ConfigDocument doc)
    {
        doc.Access.Users = _users
            .Select(r => new ConfigDocument.UserEntry
            {
                Enabled = r.Enabled,
                Username = r.Username,
                DisplayName = r.DisplayName,
                Password = r.NewPassword,
                CaptureNextPassword = r.CaptureNextPassword,
                FromRestrictions = [.. r.FromRestrictions],
            }).ToList();

        doc.Access.AllowedSenders = _senders.Select(r => r.Pattern).ToList();
        doc.Access.BlockedSenders = _blockedSenders.Select(r => r.Pattern).ToList();
        doc.Access.AllowedRecipients = _recipients.Select(r => r.Pattern).ToList();
        doc.Access.BlockedRecipients = _blockedRecipients.Select(r => r.Pattern).ToList();

        doc.SenderValidation.SvEnabled = SvEnabled.IsChecked == true;
        if (int.TryParse(SvRefreshInterval.Text, out var refreshMinutes))
            doc.SenderValidation.SvRefreshIntervalMinutes = refreshMinutes;
        doc.SenderValidation.SvFailClosed = SvFailClosed.IsChecked == true;
        doc.SenderValidation.SvAcceptMailboxless = SvAcceptMailboxless.IsChecked == true;

        doc.SenderRouting.SrEnabled = SrEnabled.IsChecked == true;
        doc.SenderRouting.SrRelayMailbox = SrRelayMailbox.Text.Trim();
        doc.SenderRouting.SrRoutes = _routes
            .Select(r => new ConfigDocument.SenderRouteEntry { Sender = r.Sender, Mailbox = r.Mailbox })
            .ToList();
    }

    private void SvField_Changed(object sender, RoutedEventArgs e)
    {
        _markDirty();
        UpdateSyncStatus();             // toggling validation enables/disables "Sync now" immediately
        UpdateRoutingAvailability(fromUser: true);   // …and decides whether the routing options bite
    }
    private void SvText_Changed(object sender, TextChangedEventArgs e) => _markDirty();

    // ── Sender routing ───────────────────────────────────────────────────────

    private void SrField_Changed(object sender, RoutedEventArgs e)
    {
        _markDirty();
        UpdateRoutingAvailability(fromUser: true);
    }

    private void SrText_Changed(object sender, TextChangedEventArgs e)
    {
        _markDirty();
        UpdateRoutingAvailability(fromUser: true);
    }

    /// <summary>
    /// Keeps the routing card honest about what it can actually do.
    ///
    /// Routing without a relay mailbox silently does nothing, and the two recognition options are
    /// worse than useless on their own: they let a group or public folder through MAIL FROM, and
    /// without a relay to deliver it the message is then bounced by Graph with a 404 — an NDR
    /// where the operator would otherwise have got a clean 550. So both are gated on a usable
    /// relay mailbox and cleared when that goes away.
    /// </summary>
    /// <param name="fromUser">
    /// False while loading a document — clearing a stale flag then must not raise the
    /// "unsaved changes" badge on a page the user has not touched.
    /// </param>
    private void UpdateRoutingAvailability(bool fromUser)
    {
        if (SrRelayMailboxError is null) return;   // during InitializeComponent
        if (_loading) return;                      // half-filled page — LoadFrom calls this at the end

        var address = SrRelayMailbox.Text.Trim();
        var routingOn = SrEnabled.IsChecked == true;

        string? error = null;
        if (routingOn)
        {
            if (address.Length == 0)
                error = "Required — without a relay mailbox nothing can be sent as a group or public folder.";
            else if (!EmailValidation.IsValidRecipient(address))
                error = "Enter a valid mailbox address, e.g. relay@contoso.com.";
        }

        SrRelayMailboxError.Text = error ?? "";
        SrRelayMailboxError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;

        var canRelay = routingOn && error is null;
        SvAcceptMailboxless.IsEnabled = canRelay;

        if (!canRelay)
        {
            var cleared = SvAcceptMailboxless.IsChecked == true;
            SvAcceptMailboxless.IsChecked = false;
            if (cleared && fromUser) _markDirty();
        }

        // It only takes effect through sender validation; without it every sender is accepted
        // anyway and the option does nothing.
        var validationOff = canRelay && SvEnabled.IsChecked != true;
        SrValidationHint.Text = validationOff
            ? "Microsoft 365 sender validation is off, so this option has no effect — every sender is accepted regardless. Routing itself works either way."
            : "";
        SrValidationHint.Visibility = validationOff ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddRoute_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SenderRouteDialog("Add Route", "", "", ValidateRoute)
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            _routes.Add(new RouteRow { Sender = dlg.Sender, Mailbox = dlg.Mailbox });
            _markDirty();
        }
    }

    private void EditRoute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not RouteRow row) return;

        var dlg = new SenderRouteDialog(
            "Edit Route", row.Sender, row.Mailbox,
            (s, m) => ValidateRoute(s, m, ignore: row))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            row.Sender = dlg.Sender;
            row.Mailbox = dlg.Mailbox;
            _markDirty();
        }
    }

    private void RemoveRoute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not RouteRow row) return;
        _routes.Remove(row);
        _markDirty();
    }

    private string? ValidateRoute(string senderPattern, string mailbox) => ValidateRoute(senderPattern, mailbox, null);

    private string? ValidateRoute(string senderPattern, string mailbox, RouteRow? ignore)
    {
        // The sender side accepts the same exact-address / @domain patterns as the allow lists.
        var patternError = AddressPatternValidator.Validate(
            senderPattern,
            [.. _routes.Where(r => r != ignore).Select(r => r.Sender)],
            null,
            isAllowList: true);
        if (patternError is not null) return patternError;

        if (mailbox.Length == 0)
            return "Enter the mailbox the message should be sent through.";
        if (!EmailValidation.IsValidRecipient(mailbox))
            return "The mailbox must be a valid address, e.g. relay@contoso.com.";

        return null;
    }

    private void SrGenerateScript_Click(object sender, RoutedEventArgs e)
    {
        var relay = SrRelayMailbox.Text.Trim();
        if (relay.Length == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Enter the relay mailbox first — the script grants SendAs to that mailbox.",
                "Relay mailbox missing",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        new SendAsScriptDialog(relay, BuildInfo.FileVersion, _liveConfig)
        { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    /// <summary>
    /// Called when the config file changes on disk (e.g. service captured a password).
    /// Only updates Password + CaptureNextPassword for users that had CaptureNextPassword=true
    /// in memory. All other in-memory changes are left untouched.
    /// Returns true if at least one user was updated.
    /// </summary>
    internal bool MergeCapturedPasswords(ConfigDocument freshDoc)
    {
        bool anyMerged = false;
        foreach (var row in _users)
        {
            if (!row.CaptureNextPassword) continue;

            var fresh = freshDoc.Access.Users
                .FirstOrDefault(u => u.Username.Equals(row.Username, StringComparison.OrdinalIgnoreCase));

            if (fresh is null) continue;

            // Capture completed: flag cleared + password present in file
            if (!fresh.CaptureNextPassword && !string.IsNullOrEmpty(fresh.Password))
            {
                row.NewPassword = fresh.Password;   // already ENC[...] from file
                row.CaptureNextPassword = false;
                anyMerged = true;
            }
        }
        return anyMerged;
    }

    // ── Users ─────────────────────────────────────────────────────────────

    private void AddUser_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new UserDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            _users.Add(new UserRow
            {
                Enabled = dlg.ResultEnabled,
                Username = dlg.ResultUsername,
                DisplayName = dlg.ResultDisplayName,
                NewPassword = dlg.ResultPassword,
                CaptureNextPassword = dlg.ResultCaptureNextPassword,
                FromRestrictions = dlg.ResultFromRestrictions,
            });
            _markDirty();
        }
    }

    private void EditUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not UserRow row) return;

        // If capture is still pending, do a sync read before opening the dialog
        // so the user sees the captured password rather than the stale capture-mode state.
        if (row.CaptureNextPassword && _liveConfig is not null)
        {
            var fresh = _liveConfig();
            if (fresh is not null) MergeCapturedPasswords(fresh);
        }

        var dlg = new UserDialog(row) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            row.Enabled = dlg.ResultEnabled;
            row.Username = dlg.ResultUsername;
            row.DisplayName = dlg.ResultDisplayName;
            row.NewPassword = dlg.ResultPassword;
            row.CaptureNextPassword = dlg.ResultCaptureNextPassword;
            row.FromRestrictions = dlg.ResultFromRestrictions;
            // The user reviewed/changed this entry → clear the undecryptable marker.
            row.PasswordUndecryptable = false;
            _markDirty();
        }
    }

    private void RemoveUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is UserRow row)
        { _users.Remove(row); _markDirty(); }
    }

    private void UserEnabled_Changed(object sender, RoutedEventArgs e) => _markDirty();

    // ── Senders ───────────────────────────────────────────────────────────

    private void AddSender_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PatternDialog(
            title: "Add Sender",
            description: "Exact address: user@domain.com\nAll addresses at a domain: @domain.com",
            initialValue: "",
            validate: v => ValidatePattern(v, _senders, _blockedSenders, isAllowList: true))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            _senders.Add(new PatternRow { Pattern = dlg.Result });
            _markDirty();
        }
    }

    private void EditSender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PatternRow row) return;

        var dlg = new PatternDialog(
            title: "Edit Sender",
            description: "Exact address: user@domain.com\nAll addresses at a domain: @domain.com",
            initialValue: row.Pattern,
            validate: v => ValidatePattern(v, _senders.Where(x => x != row), _blockedSenders, isAllowList: true))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            row.Pattern = dlg.Result;
            _markDirty();
        }
    }

    private void RemoveSender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PatternRow row)
        { _senders.Remove(row); _markDirty(); }
    }

    // ── Recipients ────────────────────────────────────────────────────────

    private void AddRecipient_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PatternDialog(
            title: "Add Recipient",
            description: "Exact address: user@domain.com\nAll addresses at a domain: @domain.com",
            initialValue: "",
            validate: v => ValidatePattern(v, _recipients, _blockedRecipients, isAllowList: true))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            _recipients.Add(new PatternRow { Pattern = dlg.Result });
            _markDirty();
        }
    }

    private void EditRecipient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PatternRow row) return;

        var dlg = new PatternDialog(
            title: "Edit Recipient",
            description: "Exact address: user@domain.com\nAll addresses at a domain: @domain.com",
            initialValue: row.Pattern,
            validate: v => ValidatePattern(v, _recipients.Where(x => x != row), _blockedRecipients, isAllowList: true))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            row.Pattern = dlg.Result;
            _markDirty();
        }
    }

    private void RemoveRecipient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PatternRow row)
        { _recipients.Remove(row); _markDirty(); }
    }

    // ── Blocked Senders ───────────────────────────────────────────────────

    private void AddBlockedSender_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PatternDialog(
            title: "Block Sender",
            description: "Exact address: user@domain.com\nAll addresses at a domain: @domain.com",
            initialValue: "",
            validate: v => ValidatePattern(v, _blockedSenders, _senders, isAllowList: false))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            _blockedSenders.Add(new PatternRow { Pattern = dlg.Result });
            _markDirty();
        }
    }

    private void EditBlockedSender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PatternRow row) return;

        var dlg = new PatternDialog(
            title: "Edit Blocked Sender",
            description: "Exact address: user@domain.com\nAll addresses at a domain: @domain.com",
            initialValue: row.Pattern,
            validate: v => ValidatePattern(v, _blockedSenders.Where(x => x != row), _senders, isAllowList: false))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            row.Pattern = dlg.Result;
            _markDirty();
        }
    }

    private void RemoveBlockedSender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PatternRow row)
        { _blockedSenders.Remove(row); _markDirty(); }
    }

    // ── Blocked Recipients ────────────────────────────────────────────────

    private void AddBlockedRecipient_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PatternDialog(
            title: "Block Recipient",
            description: "Exact address: user@domain.com\nAll addresses at a domain: @domain.com",
            initialValue: "",
            validate: v => ValidatePattern(v, _blockedRecipients, _recipients, isAllowList: false))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            _blockedRecipients.Add(new PatternRow { Pattern = dlg.Result });
            _markDirty();
        }
    }

    private void EditBlockedRecipient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PatternRow row) return;

        var dlg = new PatternDialog(
            title: "Edit Blocked Recipient",
            description: "Exact address: user@domain.com\nAll addresses at a domain: @domain.com",
            initialValue: row.Pattern,
            validate: v => ValidatePattern(v, _blockedRecipients.Where(x => x != row), _recipients, isAllowList: false))
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() == true)
        {
            row.Pattern = dlg.Result;
            _markDirty();
        }
    }

    private void RemoveBlockedRecipient_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PatternRow row)
        { _blockedRecipients.Remove(row); _markDirty(); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string? ValidatePattern(
        string pattern,
        IEnumerable<PatternRow> sameList,
        IEnumerable<PatternRow>? oppositeList = null,
        bool isAllowList = true)
        => AddressPatternValidator.Validate(
            pattern,
            sameList.Select(r => r.Pattern).ToList(),
            oppositeList?.Select(r => r.Pattern).ToList(),
            isAllowList);
}

// ── Data models ──────────────────────────────────────────────────────────────

public class UserRow : INotifyPropertyChanged
{
    private bool _enabled;
    private string _username = "";
    private string _displayName = "";
    private bool _captureNextPassword;
    private bool _passwordUndecryptable;
    public string? NewPassword { get; set; }
    public List<string> FromRestrictions { get; set; } = [];

    /// <summary>
    /// True when this user's stored password could not be decrypted on load (shown blank).
    /// Drives an inline warning marker in the users grid.
    /// </summary>
    public bool PasswordUndecryptable
    {
        get => _passwordUndecryptable;
        set { _passwordUndecryptable = value; OnPropChanged(); OnPropChanged(nameof(PasswordWarningVisibility)); }
    }

    public Visibility PasswordWarningVisibility
        => _passwordUndecryptable ? Visibility.Visible : Visibility.Collapsed;

    public bool Enabled { get => _enabled; set { _enabled = value; OnPropChanged(); } }
    public string Username { get => _username; set { _username = value; OnPropChanged(); } }
    public string DisplayName { get => _displayName; set { _displayName = value; OnPropChanged(); } }
    public bool CaptureNextPassword
    {
        get => _captureNextPassword;
        set { _captureNextPassword = value; OnPropChanged(); OnPropChanged(nameof(CaptureLabel)); OnPropChanged(nameof(CaptureTooltip)); }
    }
    public string CaptureLabel => _captureNextPassword ? "Capturing…" : "Capture";
    public string CaptureTooltip => _captureNextPassword
        ? "Capture mode active \u2013 the next SMTP AUTH will set this user's password. Click to cancel."
        : "Activate capture mode: the next SMTP AUTH by this user will capture and store the password.";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropChanged([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public class PatternRow : INotifyPropertyChanged
{
    private string _pattern = "";

    public string Pattern
    {
        get => _pattern;
        set { _pattern = value; OnPropChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropChanged([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>One sender-routing override: which sender goes out through which mailbox.</summary>
public class RouteRow : INotifyPropertyChanged
{
    private string _sender = "";
    private string _mailbox = "";

    public string Sender
    {
        get => _sender;
        set { _sender = value; OnPropChanged(); }
    }

    public string Mailbox
    {
        get => _mailbox;
        set { _mailbox = value; OnPropChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropChanged([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
