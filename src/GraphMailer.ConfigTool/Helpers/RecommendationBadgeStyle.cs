using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// The single palette for every recommendation badge — the sidebar per-page count bubbles, the
/// aggregate count on the Recommendations entry, and the per-card "Recommendation" links on the
/// config pages. One source so a colour tweak lands on all of them at once, and every badge in the
/// app is the same bordered pill.
///
/// The tones are the toolbar's badge family, so recommendations speak the same visual language as
/// "Unsaved changes" / "Restart required": High reuses the Restart badge's peach+orange, Medium the
/// Unsaved badge's yellow+amber, Low the pale-blue recommendation tone with a matching border.
/// Deliberately not red/green — the palette survives a colour-vision check. Pure red is reserved for
/// the setup/error markers (undecryptable secret, Graph not configured) and is not part of it.
/// </summary>
internal static class RecommendationBadgeStyle
{
    // Frozen so a single brush trio is reused across every badge.
    private static readonly (Brush Background, Brush Foreground, Brush Border) High =
        (Freeze(0xFF, 0xE4, 0xCE), Freeze(0x7A, 0x30, 0x00), Freeze(0xED, 0x70, 0x00));   // peach · brown · orange
    private static readonly (Brush Background, Brush Foreground, Brush Border) Medium =
        (Freeze(0xFF, 0xF4, 0xCE), Freeze(0x7A, 0x57, 0x00), Freeze(0xED, 0xBE, 0x00));   // yellow · amber
    private static readonly (Brush Background, Brush Foreground, Brush Border) Low =
        (Freeze(0xDE, 0xEC, 0xF9), Freeze(0x1F, 0x4A, 0x77), Freeze(0x7F, 0xA8, 0xD8));   // pale blue · navy

    private static (Brush Background, Brush Foreground, Brush Border) ForSeverity(RecommendationSeverity severity)
        => severity switch
        {
            RecommendationSeverity.High => High,
            RecommendationSeverity.Medium => Medium,
            _ => Low,
        };

    /// <summary>
    /// Drives one sidebar count badge (a <see cref="Border"/> wrapping a <see cref="TextBlock"/>):
    /// hidden when <paramref name="count"/> is zero, otherwise the count in the colour of the
    /// most-severe open hint it represents. Identical styling to the per-card badges.
    /// </summary>
    internal static void Apply(Border badge, TextBlock text, int count, RecommendationSeverity maxSeverity)
    {
        if (count <= 0)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }

        Paint(badge, text, maxSeverity);
        text.Text = count.ToString();
        badge.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Drives a per-card "Recommendation" badge from the open hints that belong to that card: hidden
    /// when the list is empty, otherwise shown in the colour of the most-severe hint. Unlike the
    /// count bubbles in the navigation, the card badge keeps its static label (a clickable link to
    /// the Recommendations page) — repeating the sidebar's number on the very page it points at tells
    /// the operator nothing new. Lets a config page map "recommendation id → card badge" in a single
    /// line; <paramref name="text"/> is recoloured but its <c>Text</c> is left untouched.
    /// </summary>
    internal static void ApplyLabel(Border badge, TextBlock text, IReadOnlyCollection<Recommendation> recommendations)
    {
        if (recommendations.Count == 0)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }

        Paint(badge, text, recommendations.Min(r => r.Severity));
        badge.Visibility = Visibility.Visible;
    }

    private static void Paint(Border badge, TextBlock text, RecommendationSeverity severity)
    {
        var (background, foreground, border) = ForSeverity(severity);
        badge.Background = background;
        badge.BorderBrush = border;
        text.Foreground = foreground;
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
