using System.Diagnostics;
using System.IO;
using System.Windows;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Post-migration notice shown once when the ConfigTool upgrades <c>graphmailer.json</c> to a newer
/// schema version. Unlike a plain MessageBox it exposes the backup path as selectable text with
/// copy-to-clipboard and reveal-in-Explorer actions, so the operator can locate the original.
/// </summary>
public partial class MigrationInfoDialog : Window
{
    private readonly string _backupPath;

    public MigrationInfoDialog(int from, int to, string backupPath)
    {
        InitializeComponent();
        _backupPath = backupPath;
        MessageLabel.Text = $"The configuration was migrated from schema v{from} to v{to}.";
        PathBox.Text = backupPath;
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_backupPath); } catch { /* clipboard may be locked by another app */ }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        // Reveal the backup file in Explorer (selects it inside config\backups\).
        var target = File.Exists(_backupPath)
            ? $"/select,\"{_backupPath}\""
            : $"\"{Path.GetDirectoryName(_backupPath)}\"";
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch { /* Explorer unavailable — the copyable path remains */ }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
