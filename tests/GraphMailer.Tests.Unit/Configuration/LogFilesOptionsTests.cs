using FluentAssertions;
using GraphMailer.Service.Configuration;

namespace GraphMailer.Tests.Unit.Configuration;

public class LogFilesOptionsTests
{
    [Fact]
    public void Defaults_MatchTheFixedRetentionOfEarlierVersions()
    {
        var opts = new LogFilesOptions();

        opts.RetentionDays.Should().Be(7);
        opts.ErrorRetentionDays.Should().Be(30);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(14, 14)]
    [InlineData(3650, 3650)]
    [InlineData(99999, 3650)]
    public void TimeLimit_IsTheClampedNumberOfDays(int configured, int expectedDays)
        => LogFilesOptions.TimeLimit(configured).Should().Be(TimeSpan.FromDays(expectedDays));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(7, 28)]
    [InlineData(30, 120)]
    public void FileCountLimit_AllowsFourSizeRolledFilesPerRetainedDay(int configured, int expected)
        => LogFilesOptions.FileCountLimit(configured).Should().Be(expected);
}
