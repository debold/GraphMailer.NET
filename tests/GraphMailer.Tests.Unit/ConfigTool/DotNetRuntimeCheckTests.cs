using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Tests for the shared-framework folder scan behind the ConfigTool's
/// ".NET runtime installed?" check (framework-dependent deployments).
/// </summary>
public sealed class DotNetRuntimeCheckTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "dotnet-check-tests-" + Guid.NewGuid().ToString("N"));

    public DotNetRuntimeCheckTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void AddRuntimeFolder(string framework, string version)
        => Directory.CreateDirectory(Path.Combine(_root, "shared", framework, version));

    [Fact]
    public void GetInstalledMajors_VersionFolders_ReturnsMajors()
    {
        AddRuntimeFolder("Microsoft.NETCore.App", "8.0.16");
        AddRuntimeFolder("Microsoft.NETCore.App", "6.0.36");

        DotNetRuntimeCheck.GetInstalledMajors(_root, "Microsoft.NETCore.App")
            .Should().BeEquivalentTo([8, 6]);
    }

    [Fact]
    public void GetInstalledMajors_PreviewFolder_ParsesMajor()
    {
        AddRuntimeFolder("Microsoft.NETCore.App", "9.0.0-preview.5.24306.7");

        DotNetRuntimeCheck.GetInstalledMajors(_root, "Microsoft.NETCore.App")
            .Should().BeEquivalentTo([9]);
    }

    [Fact]
    public void GetInstalledMajors_GarbageFolderNames_AreIgnored()
    {
        AddRuntimeFolder("Microsoft.NETCore.App", "not-a-version");
        AddRuntimeFolder("Microsoft.NETCore.App", "8.0.16");

        DotNetRuntimeCheck.GetInstalledMajors(_root, "Microsoft.NETCore.App")
            .Should().BeEquivalentTo([8]);
    }

    [Fact]
    public void GetInstalledMajors_MissingFrameworkFolder_ReturnsEmpty()
    {
        DotNetRuntimeCheck.GetInstalledMajors(_root, "Microsoft.NETCore.App")
            .Should().BeEmpty();
    }

    [Fact]
    public void GetInstalledMajors_OtherFrameworkOnly_ReturnsEmpty()
    {
        // A Desktop-Runtime folder must not satisfy a query for the base runtime
        AddRuntimeFolder("Microsoft.WindowsDesktop.App", "8.0.16");

        DotNetRuntimeCheck.GetInstalledMajors(_root, "Microsoft.NETCore.App")
            .Should().BeEmpty();
    }

    [Fact]
    public void IsServiceRuntimeInstalled_OnThisMachine_ReturnsTrue()
    {
        // The test itself runs on .NET 8 — the real check must find it
        DotNetRuntimeCheck.IsServiceRuntimeInstalled().Should().BeTrue();
    }
}
