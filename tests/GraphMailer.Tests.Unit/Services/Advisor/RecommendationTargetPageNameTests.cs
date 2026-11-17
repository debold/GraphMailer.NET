using FluentAssertions;
using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.Tests.Unit.Services.Advisor;

/// <summary>
/// <see cref="Recommendation.TargetPageName"/> is shown in the ConfigTool and the report email
/// ("ConfigTool → Malware Scan") and must match the sidebar label. Its switch falls back to the
/// raw enum name, so a target added without a label would show "MalwareScan". The table below
/// must list every target: a new one fails the test until it is given its sidebar label.
/// </summary>
public sealed class RecommendationTargetPageNameTests
{
    private static readonly Dictionary<RecommendationTarget, string> SidebarLabels = new()
    {
        [RecommendationTarget.ServersAndTls] = "Servers & TLS",
        [RecommendationTarget.AccessControl] = "Access Control",
        [RecommendationTarget.GraphApi] = "Graph API",
        [RecommendationTarget.MailQueue] = "Mail Queue",
        [RecommendationTarget.Monitoring] = "Monitoring",
        [RecommendationTarget.Notifications] = "Notifications",
        [RecommendationTarget.BackupAndRestore] = "Backup & Restore",
        [RecommendationTarget.MalwareScan] = "Malware Scan",
    };

    [Fact]
    public void Every_target_shows_its_sidebar_label()
    {
        foreach (var target in Enum.GetValues<RecommendationTarget>())
        {
            SidebarLabels.Should().ContainKey(target, $"{target} needs an entry in this table and in TargetPageName");

            var recommendation = new Recommendation(
                "test", RecommendationSeverity.Low, RecommendationCategory.Security,
                "Title", "Detail", "Impact", target, "index.html", RecommendationState.Open, "Done");

            recommendation.TargetPageName.Should().Be(SidebarLabels[target]);
        }
    }
}
