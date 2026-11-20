using System.IO;
using System.Text.Json;
using System.Windows;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>Where the main window was and whether it was maximized, in device-independent units.</summary>
internal sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>
/// Remembers the ConfigTool window's size, position and maximized state between starts.
///
/// Stored per Windows user under <c>%LOCALAPPDATA%\GraphMailer\configtool-window.json</c>, not in
/// <c>graphmailer.json</c>: it is a preference of whoever sits at the screen, not part of the
/// service configuration, and must never mark the config as changed or end up in a backup.
/// Every failure (missing, corrupt, unwritable file) falls back to the default placement.
/// </summary>
internal static class WindowPlacementStore
{
    /// <summary>At least this much of the title bar must lie on a screen to restore a position.</summary>
    internal const double MinVisible = 100;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GraphMailer", "configtool-window.json");

    internal static WindowPlacement? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static void Save(string path, WindowPlacement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(placement));
        }
        catch (Exception ex)
        {
            ConfigToolLog.Info("MainWindow", $"Could not save the window position: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes a saved placement fit the current screens, or returns null to use the default.
    /// The size is capped at the virtual screen and kept at or above the window's minimum; the
    /// position is kept only when enough of the title bar lands on a screen to grab it — a
    /// window restored onto a monitor that is no longer connected would be unreachable.
    /// </summary>
    internal static WindowPlacement? Fit(WindowPlacement? saved, Rect virtualScreen, double minWidth, double minHeight)
    {
        if (saved is null
            || !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top)
            || !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height)
            || saved.Width <= 0 || saved.Height <= 0)
            return null;

        var width = Math.Clamp(saved.Width, Math.Min(minWidth, virtualScreen.Width), virtualScreen.Width);
        var height = Math.Clamp(saved.Height, Math.Min(minHeight, virtualScreen.Height), virtualScreen.Height);

        var titleBar = new Rect(saved.Left, saved.Top, width, 30);
        var visible = Rect.Intersect(titleBar, virtualScreen);
        if (visible.IsEmpty || visible.Width < Math.Min(MinVisible, width) || visible.Height <= 0)
            return null;

        return saved with { Width = width, Height = height };
    }
}
