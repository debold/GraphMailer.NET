using GraphMailer.Service.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMailer.Tests.Unit.Infrastructure.Config;

/// <summary>
/// Covers first-run config seeding. The certificate step is injected so these tests never touch
/// the machine certificate store.
/// </summary>
public sealed class FirstRunProvisionerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;
    private readonly IDataProtector _protector;

    public FirstRunProvisionerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gm-firstrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "graphmailer.json");
        _protector = new EphemeralDataProtectionProvider().CreateProtector("GraphMailer.Config");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void EnsureProvisioned_NoFile_SeedsListenersWhitelistAndCert()
    {
        FirstRunProvisioner.EnsureProvisioned(_filePath, _protector, () => "GraphMailer SMTP");

        File.Exists(_filePath).Should().BeTrue();
        var doc = new ConfigService(_filePath, _protector).Load();

        doc.Servers.Select(s => s.Port).Should().Equal(25, 465, 587);
        doc.Access.IpWhitelist.Should().Contain("10.0.0.0/8").And.Contain("fc00::/7");
        doc.Access.IpWhitelistComments.Should().ContainKey("10.0.0.0/8");
        doc.Certificate.SubjectName.Should().Be("GraphMailer SMTP");
    }

    [Fact]
    public void EnsureProvisioned_CertUnavailable_StillSeedsButLeavesSubjectUnset()
    {
        FirstRunProvisioner.EnsureProvisioned(_filePath, _protector, () => null);

        var doc = new ConfigService(_filePath, _protector).Load();
        doc.Servers.Should().HaveCount(3);
        doc.Certificate.SubjectName.Should().BeNull();
    }

    [Fact]
    public void EnsureProvisioned_ExistingFile_IsNoOp()
    {
        File.WriteAllText(_filePath, """{ "Smtp": { "Banner": "UserConfig" } }""");

        FirstRunProvisioner.EnsureProvisioned(_filePath, _protector, () => "should-not-be-used");

        // Untouched: no servers seeded, original content preserved.
        var doc = new ConfigService(_filePath, _protector).Load();
        doc.Smtp.Banner.Should().Be("UserConfig");
        doc.Servers.Should().BeEmpty();
    }
}
