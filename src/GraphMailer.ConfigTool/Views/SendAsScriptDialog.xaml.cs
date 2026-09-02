using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure.Config;
using Microsoft.Win32;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Shows the Exchange Online PowerShell script that grants the relay mailbox SendAs, and lets the
/// admin copy or save it. Nothing is executed here — Graph cannot set the permission, and driving
/// Exchange PowerShell from this process would need Exchange admin rights the service must not have.
/// </summary>
public partial class SendAsScriptDialog : Window
{
    private readonly string _relayMailbox;
    private readonly string _version;
    private readonly Func<ConfigDocument?>? _liveConfig;
    private readonly ObservableCollection<MailEnabledGroup> _groups = [];

    internal SendAsScriptDialog(string relayMailbox, string version, Func<ConfigDocument?>? liveConfig)
    {
        _relayMailbox = relayMailbox;
        _version = version;
        _liveConfig = liveConfig;

        InitializeComponent();
        GroupList.ItemsSource = _groups;
        Regenerate();
    }

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        // Guard: Checked fires during InitializeComponent, before the panel exists.
        if (SelectionPanel is null) return;

        SelectionPanel.Visibility = ScopeSelected.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        Regenerate();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Regenerate();

    private void ExtraAddresses_TextChanged(object sender, TextChangedEventArgs e) => Regenerate();

    private void Regenerate()
    {
        if (ScriptBox is null) return;

        ScriptBox.Text = ScopeSelected.IsChecked == true
            ? SendAsScriptGenerator.GenerateForObjects(_relayMailbox, _version, DateTime.UtcNow, SelectedObjects())
            : SendAsScriptGenerator.GenerateForAllObjects(_relayMailbox, _version, DateTime.UtcNow);
    }

    /// <summary>Ticked groups plus whatever was typed into the free-text box.</summary>
    private List<string> SelectedObjects()
    {
        var objects = GroupList.SelectedItems
            .OfType<MailEnabledGroup>()
            .Select(g => g.Address)
            .ToList();

        objects.AddRange(
            (ExtraAddresses.Text ?? "")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return objects;
    }

    private async void LoadGroups_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _liveConfig?.Invoke();
        var graph = cfg?.GraphApi;

        if (string.IsNullOrWhiteSpace(graph?.TenantId) || string.IsNullOrWhiteSpace(graph.ClientId))
        {
            LoadStatus.Text = "Configure and save the Graph API credentials first.";
            return;
        }

        LoadGroups.IsEnabled = false;
        LoadStatus.Text = "Loading…";

        try
        {
            var groups = await GraphRecipientLookup.ListMailEnabledGroupsAsync(
                graph.TenantId!, graph.ClientId!, graph.ClientSecret,
                graph.ClientCertificateThumbprint, CancellationToken.None);

            _groups.Clear();
            foreach (var group in groups)
                _groups.Add(group);

            LoadStatus.Text = groups.Count == 0
                ? "No mail-enabled groups found."
                : $"{groups.Count} mail-enabled group(s). Public folders and mail users are not listable — add them below.";
        }
        catch (Exception ex)
        {
            // Most likely cause by far: Group.Read.All was never granted.
            LoadStatus.Text = $"Could not load groups: {ex.Message}";
        }
        finally
        {
            LoadGroups.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ScriptBox.Text);
            ActionStatus.Text = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = $"Could not copy: {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = "GraphMailer-Grant-SendAs.ps1",
            Filter = "PowerShell script (*.ps1)|*.ps1|All files (*.*)|*.*",
            DefaultExt = ".ps1",
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, ScriptBox.Text);
            ActionStatus.Text = $"Saved to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = $"Could not save: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
