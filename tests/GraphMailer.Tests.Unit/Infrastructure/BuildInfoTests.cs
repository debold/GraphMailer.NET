using System.Text.RegularExpressions;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Tests.Unit.Infrastructure;

public sealed class BuildInfoTests
{
    [Fact]
    public void FileVersion_IsFourPartVersion_FromBuild()
    {
        // Set by src/Directory.Build.props: Version.BuildNumber, e.g. "1.1.0.163".
        BuildInfo.FileVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+\.\d+$");
    }

    [Fact]
    public void InformationalVersion_IsNotEmpty()
    {
        BuildInfo.InformationalVersion.Should().NotBeNullOrWhiteSpace();
    }
}
