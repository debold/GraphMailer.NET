using GraphMailer.Service.Infrastructure.Security;

namespace GraphMailer.Tests.Unit.Infrastructure.Security;

public sealed class IpFilterServiceTests
{
    // -------------------------------------------------------------------------
    // Empty lists → always allow
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAllowed_EmptyLists_AllowsAnyIp()
    {
        IpFilterService.IsAllowed("10.0.0.1", [], []).Should().BeTrue();
        IpFilterService.IsAllowed("192.168.1.100", [], []).Should().BeTrue();
        IpFilterService.IsAllowed("::1", [], []).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Blacklist
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAllowed_BlacklistedIp_ReturnsFalse()
    {
        IpFilterService.IsAllowed("10.0.0.5", [], ["10.0.0.5"]).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_BlacklistedCidr_ReturnsFalse()
    {
        IpFilterService.IsAllowed("192.168.1.50", [], ["192.168.1.0/24"]).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_IpOutsideBlacklistedCidr_ReturnsTrue()
    {
        IpFilterService.IsAllowed("192.168.2.1", [], ["192.168.1.0/24"]).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Whitelist
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAllowed_WhitelistedIp_ReturnsTrue()
    {
        IpFilterService.IsAllowed("10.0.0.1", ["10.0.0.1"], []).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_NonWhitelistedIp_ReturnsFalse()
    {
        IpFilterService.IsAllowed("10.0.0.2", ["10.0.0.1"], []).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_WhitelistedCidr_ReturnsTrue()
    {
        IpFilterService.IsAllowed("172.16.5.10", ["172.16.0.0/12"], []).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Blacklist takes priority over whitelist
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAllowed_IpInBothLists_BlacklistWins()
    {
        IpFilterService.IsAllowed("10.0.0.1", ["10.0.0.1"], ["10.0.0.1"]).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // IPv6
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAllowed_IPv6Loopback_WithWhitelist_Allowed()
    {
        IpFilterService.IsAllowed("::1", ["::1/128"], []).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_IPv6_Cidr_Blacklist()
    {
        IpFilterService.IsAllowed("2001:db8::1", [], ["2001:db8::/32"]).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Invalid / malformed input
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAllowed_InvalidIp_ReturnsFalse()
    {
        IpFilterService.IsAllowed("not-an-ip", [], []).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_MalformedCidrEntry_IsSkipped()
    {
        // Malformed entry is ignored; second valid entry matches
        IpFilterService.IsAllowed("10.0.0.1", ["INVALID_CIDR", "10.0.0.1"], []).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Bare IP (no /prefix) in list
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAllowed_BareIpInWhitelist_Matched()
    {
        IpFilterService.IsAllowed("10.10.10.10", ["10.10.10.10"], []).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // GetDenyReason – log diagnostics for rejections
    // -------------------------------------------------------------------------

    [Fact]
    public void GetDenyReason_BlacklistedByCidr_NamesTheEntry()
    {
        IpFilterService.GetDenyReason("203.0.113.7", [], ["203.0.113.0/24"])
            .Should().Be("matches IP blacklist entry '203.0.113.0/24'");
    }

    [Fact]
    public void GetDenyReason_NotInWhitelist_SaysSo()
    {
        IpFilterService.GetDenyReason("198.51.100.1", ["10.0.0.0/8"], [])
            .Should().Be("not covered by any IP whitelist entry");
    }

    [Fact]
    public void GetDenyReason_UnparsableIp_SaysSo()
    {
        IpFilterService.GetDenyReason("unknown", ["10.0.0.0/8"], [])
            .Should().Be("remote IP could not be parsed");
    }

    [Fact]
    public void GetDenyReason_BlacklistWinsOverWhitelist()
    {
        // IP is covered by both lists — the blacklist match must be reported,
        // mirroring the precedence in IsAllowed.
        IpFilterService.GetDenyReason("10.0.0.5", ["10.0.0.0/8"], ["10.0.0.5"])
            .Should().Be("matches IP blacklist entry '10.0.0.5'");
    }
}
