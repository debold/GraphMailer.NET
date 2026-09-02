using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Services;

namespace GraphMailer.ConfigTool.Views;

/// <summary>One row of the sender-directory viewer. Read-only, so plain properties suffice.</summary>
public sealed class SenderDirectoryRow
{
    public string Kind { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? PrimaryAddress { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];

    public int AddressCount => Addresses.Count;

    /// <summary>
    /// One address per line. A mailbox can carry a dozen aliases, and on a single comma-separated
    /// line the interesting one is the part that got cut off.
    /// </summary>
    public string AllAddresses => string.Join(Environment.NewLine, Addresses);
}

/// <summary>
/// Read-only view of what the service's last tenant sync recognised, so an operator can check
/// which senders are actually known before wondering why one is rejected.
///
/// The data comes from the snapshot file the service writes after every sync — the ConfigTool is
/// a separate process and talks to the service through files, never through Graph directly, so
/// this shows the service's real state rather than a second opinion.
/// </summary>
public partial class SenderDirectoryWindow : Window
{
    private readonly List<SenderDirectoryRow> _rows = [];
    private readonly ICollectionView _view;

    internal SenderDirectoryWindow()
    {
        InitializeComponent();

        var snapshot = SenderDirectorySnapshot.TryLoad(SenderDirectorySnapshot.FilePath);
        if (snapshot is not null)
        {
            // Domains first: they are few, and they are the rule that decides everything the
            // recipient rows below cannot express — public folders and dynamic groups.
            foreach (var domain in snapshot.Domains)
                _rows.Add(new SenderDirectoryRow
                {
                    Kind = "Domain",
                    DisplayName = "(any sender in this domain)",
                    PrimaryAddress = domain,
                    Addresses = [domain],
                });

            foreach (var entry in snapshot.Entries)
                _rows.Add(new SenderDirectoryRow
                {
                    Kind = entry.Kind,
                    DisplayName = entry.DisplayName,
                    PrimaryAddress = entry.PrimaryAddress,
                    Addresses = entry.Addresses,
                });
        }

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is SenderDirectoryRow row
            && SenderDirectorySearch.Matches(row.DisplayName, row.PrimaryAddress, row.Addresses, FilterBox.Text);

        EntriesGrid.ItemsSource = _view;

        StatusText.Text = DescribeSource(snapshot);
        UpdateCount();
    }

    private static string DescribeSource(SenderDirectorySnapshot? snapshot)
    {
        if (snapshot is null)
            return "No directory sync on this machine yet — start the service with sender validation enabled.";

        var text = $"Synced {snapshot.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        if (snapshot.Truncated)
            text += $" · list cut off at {SenderDirectorySnapshot.MaxEntries:N0} entries";
        return text;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var shown = _view.Cast<object>().Count();
        CountText.Text = shown == _rows.Count
            ? $"{_rows.Count:N0} entries"
            : $"{shown:N0} of {_rows.Count:N0} entries";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
