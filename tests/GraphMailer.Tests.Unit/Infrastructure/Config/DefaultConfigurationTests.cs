using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.Tests.Unit.Infrastructure.Config;

/// <summary>
/// Verifies the shared first-run array defaults (listeners + IP whitelist) used by both the
/// service provisioner and the ConfigTool fallback.
/// </summary>
public sealed class DefaultConfigurationTests
{
    [Fact]
    public void Servers_AreIndustryStandard_25_465_587_AllEnabled()
    {
        var servers = DefaultConfiguration.Servers();

        servers.Should().HaveCount(3);
        servers.Should().OnlyContain(s => s.Enabled);
        servers.Select(s => s.Port).Should().Equal(25, 465, 587);
    }

    [Fact]
    public void Servers_ModesAndAuth_MatchProtocolConventions()
    {
        var byPort = DefaultConfiguration.Servers().ToDictionary(s => s.Port);

        byPort[25].Mode.Should().Be("Plain");
        byPort[25].AuthMode.Should().Be("None");
        byPort[465].Mode.Should().Be("Tls");
        byPort[465].AuthMode.Should().Be("Optional");
        byPort[587].Mode.Should().Be("StartTls");
        byPort[587].AuthMode.Should().Be("Optional");
    }

    [Fact]
    public void IpWhitelist_CoversPrivateRanges()
    {
        DefaultConfiguration.IpWhitelist().Should().BeEquivalentTo(
        [
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
            "127.0.0.0/8", "::1/128", "fc00::/7", "fe80::/10",
        ]);
    }

    [Fact]
    public void IpWhitelistComments_HaveAnEntryForEveryWhitelistRange()
    {
        var comments = DefaultConfiguration.IpWhitelistComments();

        foreach (var entry in DefaultConfiguration.IpWhitelist())
            comments.Should().ContainKey(entry).WhoseValue.Should().NotBeNullOrWhiteSpace();
    }
}
