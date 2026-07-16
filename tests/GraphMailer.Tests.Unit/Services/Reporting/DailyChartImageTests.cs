using FluentAssertions;
using GraphMailer.Service.Services.Reporting;

namespace GraphMailer.Tests.Unit.Services.Reporting;

public sealed class DailyChartImageTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Render_ReturnsValidPngBytes()
    {
        var days = new[]
        {
            new DailyPoint(new DateOnly(2026, 6, 6), 540, 1),
            new DailyPoint(new DateOnly(2026, 6, 7), 632, 1),
            new DailyPoint(new DateOnly(2026, 6, 8), 680, 3),
            new DailyPoint(new DateOnly(2026, 6, 9), 720, 5),
        };

        var png = DailyChartImage.Render(days);

        png.Should().NotBeNull();
        png.Length.Should().BeGreaterThan(100);
        png.Take(8).Should().Equal(PngSignature); // valid PNG magic number
    }

    [Fact]
    public void Render_SingleDay_DoesNotThrow()
    {
        var png = DailyChartImage.Render([new DailyPoint(new DateOnly(2026, 6, 13), 1, 0)]);

        png.Take(8).Should().Equal(PngSignature);
    }

    [Fact]
    public void Render_AllZero_DoesNotThrow()
    {
        var days = new[]
        {
            new DailyPoint(new DateOnly(2026, 6, 12), 0, 0),
            new DailyPoint(new DateOnly(2026, 6, 13), 0, 0),
        };

        var png = DailyChartImage.Render(days);

        png.Take(8).Should().Equal(PngSignature);
    }
}
