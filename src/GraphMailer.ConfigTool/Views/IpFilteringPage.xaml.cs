using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GraphMailer.Service.Infrastructure.Config;

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

    private void RefreshBlocked_Click(object sender, RoutedEventArgs e)
    {
        // TODO: read from SQLite / running service
        _blocked.Clear();
        NoBlockedText.Visibility = _blocked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Unblock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.DataContext is BlockedIpRow r)
        {
            _blocked.Remove(r);
            NoBlockedText.Visibility = _blocked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
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
