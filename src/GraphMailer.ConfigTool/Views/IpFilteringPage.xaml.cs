using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Services;

namespace GraphMailer.ConfigTool.Views;

public partial class IpFilteringPage : UserControl
{
    private readonly Action _markDirty;
    private readonly ObservableCollection<IpEntryRow> _whitelist = [];
    private readonly ObservableCollection<IpEntryRow> _blacklist = [];
    private readonly ObservableCollection<BlockedIpRow> _blocked = [];

    public IpFilteringPage(Action markDirty)
    {
        _markDirty = markDirty;
        InitializeComponent();
        WhitelistGrid.ItemsSource = _whitelist;
        BlacklistGrid.ItemsSource = _blacklist;
        BlockedGrid.ItemsSource = _blocked;

        // Runtime data, not configuration: re-read every time the page is opened, so the list is
        // current without the user having to press Refresh first.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) LoadBlocked(); };
    }

    internal void LoadFrom(ConfigDocument doc)
    {
        _whitelist.Clear();
        foreach (var e in doc.Access.IpWhitelist)
            _whitelist.Add(new IpEntryRow { Entry = e, Comment = doc.Access.IpWhitelistComments.GetValueOrDefault(e, "") });

        _blacklist.Clear();
        foreach (var e in doc.Access.IpBlacklist)
            _blacklist.Add(new IpEntryRow { Entry = e, Comment = doc.Access.IpBlacklistComments.GetValueOrDefault(e, "") });

        MaxFailures.Text = doc.IpBlocking.FailureThreshold.ToString();
        WindowMinutes.Text = (doc.IpBlocking.TimeframeSeconds / 60).ToString();
        BlockMinutes.Text = (doc.IpBlocking.BlockDurationSeconds / 60).ToString();
    }

    internal void CollectTo(ConfigDocument doc)
    {
        doc.Access.IpWhitelist         = _whitelist.Select(r => r.Entry).ToList();
        doc.Access.IpWhitelistComments = _whitelist
            .Where(r => !string.IsNullOrEmpty(r.Comment))
            .ToDictionary(r => r.Entry, r => r.Comment);
        doc.Access.IpBlacklist         = _blacklist.Select(r => r.Entry).ToList();
        doc.Access.IpBlacklistComments = _blacklist
            .Where(r => !string.IsNullOrEmpty(r.Comment))
            .ToDictionary(r => r.Entry, r => r.Comment);

        doc.IpBlocking.FailureThreshold = int.TryParse(MaxFailures.Text, out var mf) ? mf : 10;
        doc.IpBlocking.TimeframeSeconds = (int.TryParse(WindowMinutes.Text, out var wm) ? wm : 10) * 60;
        doc.IpBlocking.BlockDurationSeconds = (int.TryParse(BlockMinutes.Text, out var bm) ? bm : 10) * 60;
    }

    // ── Adding from elsewhere ─────────────────────────────────────────────

    /// <summary>
    /// Adds an address the operator picked out of a log entry. Routed through this page rather
    /// than handled at the call site so the entry passes the same dialog, the same validation and
    /// the same duplicate check as one typed here — and so the operator lands on the list they
    /// just changed, where the pending entry is visible and still unsaved.
    /// </summary>
    /// <returns>The list the address went into, or null when the dialog was cancelled.</returns>
    internal string? AddFromLog(string ipAddress, bool blacklist)
    {
        var list = blacklist ? _blacklist : _whitelist;
        var name = blacklist ? "blacklist" : "whitelist";

        var existing = list.FirstOrDefault(r => r.Entry == ipAddress);
        if (existing is not null)
        {
            // Already covered — select it instead of opening a dialog that could only be
            // dismissed with a duplicate error.
            var grid = blacklist ? BlacklistGrid : WhitelistGrid;
            grid.SelectedItem = existing;
            grid.ScrollIntoView(existing);
            return null;
        }

        var dlg = new IpEntryDialog(
            title: blacklist ? "Add to Blacklist" : "Add to Whitelist",
            description: blacklist
                ? "Blacklisted IPs/CIDRs are rejected at MAIL FROM. Taken from a log entry — widen it to a "
                  + "CIDR range if you want to cover the whole network. Applies once you save; a session "
                  + "already open is not disconnected."
                : "When the whitelist is not empty, only listed IPs/CIDRs may send mail — all others are "
                  + "rejected at MAIL FROM. Taken from a log entry. Applies once you save.",
            initialEntry: ipAddress,
            extraValidate: v => list.Any(r => r.Entry == v) ? $"'{v}' is already in the {name}." : null)
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() != true) return null;

        var row = new IpEntryRow { Entry = dlg.ResultEntry, Comment = dlg.ResultComment };
        list.Add(row);
        _markDirty();

        var target = blacklist ? BlacklistGrid : WhitelistGrid;
        target.SelectedItem = row;
        target.ScrollIntoView(row);
        return name;
    }

    // ── Whitelist ─────────────────────────────────────────────────────────

    private void AddWhitelist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new IpEntryDialog(
            title: "Add to Whitelist",
            description: "When the whitelist is not empty, only listed IPs/CIDRs may send mail — all others are rejected at MAIL FROM.",
            extraValidate: v => _whitelist.Any(r => r.Entry == v) ? $"'{v}' is already in the whitelist." : null)
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        { _whitelist.Add(new IpEntryRow { Entry = dlg.ResultEntry, Comment = dlg.ResultComment }); _markDirty(); }
    }

    private void EditWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not IpEntryRow row) return;
        var dlg = new IpEntryDialog(
            title: "Edit Whitelist Entry",
            description: "When the whitelist is not empty, only listed IPs/CIDRs may send mail — all others are rejected at MAIL FROM.",
            initialEntry: row.Entry,
            initialComment: row.Comment,
            extraValidate: v => _whitelist.Any(r => r != row && r.Entry == v) ? $"'{v}' is already in the whitelist." : null)
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        { row.Entry = dlg.ResultEntry; row.Comment = dlg.ResultComment; _markDirty(); }
    }

    private void RemoveWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.DataContext is IpEntryRow r)
        { _whitelist.Remove(r); _markDirty(); }
    }

    // ── Blacklist ─────────────────────────────────────────────────────────

    private void AddBlacklist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new IpEntryDialog(
            title: "Add to Blacklist",
            description: "Blacklisted IPs/CIDRs are rejected at MAIL FROM.",
            extraValidate: v => _blacklist.Any(r => r.Entry == v) ? $"'{v}' is already in the blacklist." : null)
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        { _blacklist.Add(new IpEntryRow { Entry = dlg.ResultEntry, Comment = dlg.ResultComment }); _markDirty(); }
    }

    private void EditBlacklist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not IpEntryRow row) return;
        var dlg = new IpEntryDialog(
            title: "Edit Blacklist Entry",
            description: "Blacklisted IPs/CIDRs are rejected at MAIL FROM.",
            initialEntry: row.Entry,
            initialComment: row.Comment,
            extraValidate: v => _blacklist.Any(r => r != row && r.Entry == v) ? $"'{v}' is already in the blacklist." : null)
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        { row.Entry = dlg.ResultEntry; row.Comment = dlg.ResultComment; _markDirty(); }
    }

    private void RemoveBlacklist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.DataContext is IpEntryRow r)
        { _blacklist.Remove(r); _markDirty(); }
    }

    // ── Settings ──────────────────────────────────────────────────────────

    private void AnyField_Changed(object sender, TextChangedEventArgs e) => _markDirty();


    // ── Blocked (runtime) ─────────────────────────────────────────────────

    /// <summary>
    /// Reads the snapshot the running service publishes. Blocking happens in the service process,
    /// and the two only talk through files — so an absent or stale file is the normal way to learn
    /// that nothing is being blocked, not an error.
    /// </summary>
    private void RefreshBlocked_Click(object sender, RoutedEventArgs e) => LoadBlocked();

    internal void LoadBlocked()
    {
        _blocked.Clear();

        var snapshot = BlockedIpSnapshot.TryLoad(BlockedIpSnapshot.FilePath);
        var now = DateTime.UtcNow;

        // Expired entries survive in the file until the next write — and a file left behind by a
        // stopped service would otherwise show blocks that are long gone.
        foreach (var entry in snapshot?.ActiveAt(now) ?? [])
        {
            _blocked.Add(new BlockedIpRow(
                entry.Ip,
                entry.Failures,
                entry.BlockedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                entry.ExpiresAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        }

        BlockedAgeText.Text = BlockedAgeLabel(snapshot?.WrittenAtUtc, now);

        NoBlockedText.Text = snapshot is null
            ? "No IPs are currently blocked. (The service has not reported any blocks yet.)"
            : "No IPs are currently blocked.";
        NoBlockedText.Visibility = _blocked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// States how current the list is. The service writes only when a block appears or expires, so
    /// "no news" is normal — but the user still has to be able to tell fresh from forgotten.
    /// </summary>
    internal static string BlockedAgeLabel(DateTime? writtenAtUtc, DateTime nowUtc)
    {
        if (writtenAtUtc is null) return "The service has not reported any blocks yet.";

        var age = nowUtc - writtenAtUtc.Value;
        var when = age switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"{(int)age.TotalHours} h ago",
            _ => $"{(int)age.TotalDays} d ago",
        };

        return $"Last reported by the service {when}.";
    }
}

// ── Data models ──────────────────────────────────────────────────────────────

public class IpEntryRow : INotifyPropertyChanged
{
    private string _entry = "";
    private string _comment = "";

    public string Entry { get => _entry; set { _entry = value; OnPropChanged(); } }
    public string Comment { get => _comment; set { _comment = value; OnPropChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropChanged([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public record BlockedIpRow(string IpAddress, int Failures, string BlockedAt, string UnblocksAt);
