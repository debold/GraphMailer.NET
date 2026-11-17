using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Maps a <see cref="RecommendationTarget"/> to the sidebar entry (<c>x:Name</c> in
/// <c>MainWindow.xaml</c>) that opens its page. Clicking a recommendation raises that entry's
/// click, so the jump runs exactly the code a click in the sidebar would.
///
/// Kept as an exhaustive switch expression outside the window so a unit test can walk every
/// target: a new <see cref="RecommendationTarget"/> without a sidebar entry fails the build's
/// tests instead of producing a recommendation whose link silently goes nowhere.
/// </summary>
internal static class RecommendationNavigation
{
    /// <summary>The <c>x:Name</c> of the sidebar entry for <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The target has no sidebar entry.</exception>
    internal static string NavItemName(RecommendationTarget target) => target switch
    {
        RecommendationTarget.ServersAndTls => "NavSmtp",
        RecommendationTarget.AccessControl => "NavAccess",
        RecommendationTarget.GraphApi => "NavGraphApi",
        RecommendationTarget.MailQueue => "NavQueue",
        RecommendationTarget.Monitoring => "NavMonitoring",
        RecommendationTarget.Notifications => "NavNotifications",
        RecommendationTarget.BackupAndRestore => "NavBackup",
        RecommendationTarget.MalwareScan => "NavMalwareScan",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "No sidebar entry for this recommendation target."),
    };
}
