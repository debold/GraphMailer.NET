using System.Windows;
using FluentAssertions;
using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// The ConfigTool reopens where it was closed. A restored placement must always be reachable:
/// a window put back onto a monitor that is no longer connected would be lost off-screen.
/// </summary>
public sealed class WindowPlacementStoreTests : IDisposable
{
    private static readonly Rect OneScreen = new(0, 0, 1920, 1080);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "placement-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var path = Path.Combine(_dir, "sub", "window.json");
        var placement = new WindowPlacement(100, 50, 1300, 900, Maximized: true);

        WindowPlacementStore.Save(path, placement);

        WindowPlacementStore.Load(path).Should().Be(placement);
    }

    [Fact]
    public void Missing_or_corrupt_file_loads_nothing()
    {
        Directory.CreateDirectory(_dir);
        var corrupt = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(corrupt, "{ not json");

        WindowPlacementStore.Load(Path.Combine(_dir, "missing.json")).Should().BeNull();
        WindowPlacementStore.Load(corrupt).Should().BeNull();
    }

    [Fact]
    public void Placement_on_the_screen_is_kept()
        => WindowPlacementStore.Fit(new WindowPlacement(200, 100, 1200, 800, false), OneScreen, 960, 680)
            .Should().Be(new WindowPlacement(200, 100, 1200, 800, false));

    [Fact]
    public void Placement_on_a_disconnected_monitor_falls_back_to_the_default()
        => WindowPlacementStore.Fit(new WindowPlacement(2500, 100, 1200, 800, true), OneScreen, 960, 680)
            .Should().BeNull("the second monitor at x = 1920+ is gone");

    [Fact]
    public void Title_bar_above_the_screen_falls_back_to_the_default()
        => WindowPlacementStore.Fit(new WindowPlacement(100, -500, 1200, 800, false), OneScreen, 960, 680)
            .Should().BeNull("the title bar could not be grabbed");

    [Fact]
    public void Placement_partly_off_screen_is_kept_while_the_title_bar_can_be_grabbed()
        => WindowPlacementStore.Fit(new WindowPlacement(1700, 100, 1200, 800, false), OneScreen, 960, 680)
            .Should().NotBeNull();

    [Fact]
    public void Size_is_capped_at_the_screen_and_kept_above_the_minimum()
    {
        WindowPlacementStore.Fit(new WindowPlacement(0, 0, 5000, 3000, false), OneScreen, 960, 680)!
            .Should().Match<WindowPlacement>(p => p.Width == 1920 && p.Height == 1080);
        WindowPlacementStore.Fit(new WindowPlacement(0, 0, 300, 200, false), OneScreen, 960, 680)!
            .Should().Match<WindowPlacement>(p => p.Width == 960 && p.Height == 680);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1000, 800)]
    [InlineData(0, 0, 0, 800)]
    [InlineData(0, 0, 1000, -1)]
    public void Nonsense_values_fall_back_to_the_default(double left, double top, double width, double height)
        => WindowPlacementStore.Fit(new WindowPlacement(left, top, width, height, false), OneScreen, 960, 680)
            .Should().BeNull();

    [Fact]
    public void Second_monitor_left_of_the_primary_is_a_valid_place()
        => WindowPlacementStore.Fit(new WindowPlacement(-1800, 100, 1200, 800, false), new Rect(-1920, 0, 3840, 1080), 960, 680)
            .Should().NotBeNull();
}
