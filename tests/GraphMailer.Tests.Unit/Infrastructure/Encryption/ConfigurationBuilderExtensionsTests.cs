using FluentAssertions;
using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace GraphMailer.Tests.Unit.Infrastructure.Encryption;

public sealed class ConfigurationBuilderExtensionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gm-cfgext-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void AddEncryptedJsonFile_MissingDirectory_CreatesItAndDoesNotThrow()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("test");
        // Fresh install: the config directory does not exist yet.
        var configPath = Path.Combine(_dir, "config", "graphmailer.json");
        var configDir = Path.GetDirectoryName(configPath)!;
        Directory.Exists(configDir).Should().BeFalse();

        // Regression: with reloadOnChange the PhysicalFileProvider ctor threw
        // DirectoryNotFoundException when the directory was missing, crashing service startup.
        var act = () => new ConfigurationBuilder()
            .AddEncryptedJsonFile(configPath, protector, optional: true, reloadOnChange: true)
            .Build();

        act.Should().NotThrow();
        Directory.Exists(configDir).Should().BeTrue("the watched config directory is created");
    }
}
