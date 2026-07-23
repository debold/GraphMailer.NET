using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Lists the hints from the shared <see cref="RecommendationEngine"/> for this installation in
/// three collapsible sections — <b>Open</b> (expanded), <b>Done</b> and <b>Hidden</b>. Showing the
/// satisfied ones rather than dropping them turns the page into a record of what was suggested and
/// already handled, instead of a list that silently shrinks as settings are fixed.
///
/// The page holds no state of its own: it asks the owner (MainWindow) for the current split on
/// every refresh, and reports hide/restore back through a callback that persists the decision.
/// </summary>
public partial class RecommendationsPage : UserControl
{
    private readonly Func<RecommendationSummary> _load;
    private readonly Action<string, bool> _setDismissed;
    private readonly Action<RecommendationTarget> _navigate;

    internal RecommendationsPage(
        Func<RecommendationSummary> load,
        Action<string, bool> setDismissed,
        Action<RecommendationTarget> navigate)
    {
        _load = load;
        _setDismissed = setDismissed;
        _navigate = navigate;
        InitializeComponent();
        Refresh();
    }

    /// <summary>Re-reads the recommendations and rebuilds all three sections.</summary>
    internal void Refresh()
    {
        var summary = _load();

        SummaryText.Text = summary.Open.Count switch
        {
            0 when summary.All.Count == 0 => "No suggestions apply to this installation yet.",
            0 => "Nothing left to do — every suggestion is either handled or hidden.",
            1 => "1 open suggestion for this installation.",
            _ => $"{summary.Open.Count} open suggestions for this installation.",
        };

        Fill(OpenSection, OpenList, "Open", summary.Open);
        Fill(DoneSection, DoneList, "Done", summary.Done);
        Fill(HiddenSection, HiddenList, "Hidden", summary.Dismissed);

        // A section with nothing in it is a header that can never tell the user anything.
        EmptyState.Visibility = summary.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Fill(Expander section, ItemsControl list, string label, IReadOnlyList<Recommendation> items)
    {
        section.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        section.Header = $"{label} ({items.Count})";
        list.ItemsSource = items.Select(RecommendationItem.From).ToList();
    }

    private void GoToSetting_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is RecommendationItem item)
            _navigate(item.Target);
    }

    private void ToggleDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RecommendationItem item) return;
        _setDismissed(item.Id, !item.IsDismissed);
        Refresh();
    }

    /// <summary>One rendered card. Plain properties — the lists are rebuilt on every change, so
    /// there is nothing to notify about.</summary>
    internal sealed class RecommendationItem
    {
        public required string Id { get; init; }
        public required RecommendationTarget Target { get; init; }
        public required string Title { get; init; }
        /// <summary>The description while open, the short satisfied line once done.</summary>
        public required string Body { get; init; }
        /// <summary>The argument for acting; empty (and hidden) once the suggestion is done.</summary>
        public required string Impact { get; init; }
        public Visibility ImpactVisibility { get; init; }
        public required string TargetHint { get; init; }
        public required string StateGlyph { get; init; }
        public required string SeverityLabel { get; init; }
        public required Brush SeverityBackground { get; init; }
        public required Brush SeverityForeground { get; init; }
        public required string DismissLabel { get; init; }
        public bool IsDismissed { get; init; }
        public double CardOpacity { get; init; }

        internal static RecommendationItem From(Recommendation r)
        {
            var isDone = r.State == RecommendationState.Done;
            var (background, foreground) = SeverityColours(r.Severity, isDone);

            return new RecommendationItem
            {
                Id = r.Id,
                Target = r.Target,
                Title = r.Title,
                // A satisfied suggestion keeps its imperative title but drops the argument for it —
                // "Without it, a message …" reads wrong for something already switched on.
                Body = isDone ? r.DoneSummary : r.Detail,
                Impact = r.Impact,
                ImpactVisibility = isDone ? Visibility.Collapsed : Visibility.Visible,
                TargetHint = $"{DescribeCategory(r.Category)} · {r.TargetPageName}",
                StateGlyph = r.State switch
                {
                    RecommendationState.Done => "✔",
                    RecommendationState.Dismissed => "⊘",
                    _ => "●",
                },
                SeverityLabel = r.SeverityLabel.ToUpperInvariant(),
                SeverityBackground = background,
                SeverityForeground = foreground,
                DismissLabel = r.State == RecommendationState.Dismissed ? "Show again" : "Hide",
                IsDismissed = r.State == RecommendationState.Dismissed,
                CardOpacity = r.State == RecommendationState.Open ? 1.0 : 0.65,
            };
        }

        /// <summary>
        /// Severity chip colours. A handled suggestion drops to neutral grey: keeping a red "HIGH"
        /// chip on a card that is already done would read as an unresolved problem.
        /// Deliberately not red/green — the palette pairs amber/blue/grey so it survives a
        /// colour-vision check, and the glyph carries the state independently of hue.
        /// </summary>
        private static (Brush Background, Brush Foreground) SeverityColours(
            RecommendationSeverity severity, bool isDone)
        {
            if (isDone)
                return (new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                        new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)));

            return severity switch
            {
                RecommendationSeverity.High =>
                    (new SolidColorBrush(Color.FromRgb(0xFF, 0xE4, 0xCE)),
                     new SolidColorBrush(Color.FromRgb(0x7A, 0x30, 0x00))),
                RecommendationSeverity.Medium =>
                    (new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)),
                     new SolidColorBrush(Color.FromRgb(0x7A, 0x57, 0x00))),
                _ =>
                    (new SolidColorBrush(Color.FromRgb(0xDE, 0xEC, 0xF9)),
                     new SolidColorBrush(Color.FromRgb(0x1F, 0x4A, 0x77))),
            };
        }

        private static string DescribeCategory(RecommendationCategory category) => category switch
        {
            RecommendationCategory.Security => "Security",
            RecommendationCategory.Reliability => "Reliability",
            RecommendationCategory.Operations => "Operations",
            RecommendationCategory.Product => "Product",
            _ => category.ToString(),
        };
    }
}
