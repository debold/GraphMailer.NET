using System.Reflection;
using FluentAssertions;
using GraphMailer.ConfigTool;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Every recommendation links to the ConfigTool page holding its setting. A target without a
/// sidebar mapping made the link a silent no-op (the Malware Scan recommendations did exactly
/// that), so each target must resolve to a sidebar entry that really exists in MainWindow.xaml.
/// </summary>
public sealed class RecommendationNavigationTests
{
    [Fact]
    public void Every_target_maps_to_an_existing_sidebar_entry()
    {
        foreach (var target in Enum.GetValues<RecommendationTarget>())
        {
            var name = RecommendationNavigation.NavItemName(target);

            // x:Name elements compile to fields on the window class, so the entry can be
            // checked without creating a WPF window.
            var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            field.Should().NotBeNull($"{target} maps to sidebar entry '{name}', which must exist in MainWindow.xaml");
        }
    }

    [Fact]
    public void Targets_map_to_distinct_sidebar_entries()
    {
        var names = Enum.GetValues<RecommendationTarget>().Select(RecommendationNavigation.NavItemName).ToList();

        names.Should().OnlyHaveUniqueItems();
    }
}
