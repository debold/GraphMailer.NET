using GraphMailer.ConfigTool.Services;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Finds the addresses the Logs page offers for the IP filter lists on right-click. The risk here
/// is entirely false positives: a log line is full of dotted and colon-separated numbers, and
/// offering "add 1.3.3.1067 to the blacklist" would make the menu useless. Every case below is a
/// shape that actually occurs in this application's log output.
/// </summary>
public sealed class LogIpExtractorTests
{
    // =========================================================================
    // Real log lines
    // =========================================================================

    /// <summary>
    /// A deny reason names both the client and the rule it matched. Both addresses are offered;
    /// the rule's prefix length is not, because the extractor deals in addresses and widening one
    /// back to a range is what the entry dialog is for.
    /// </summary>
    [Fact]
    public void Extract_RejectionLine_FindsTheClientAndTheMatchedRulesAddress()
        => LogIpExtractor.Extract(
                "MAIL FROM rejected from 192.168.1.50: matches IP blacklist entry '192.168.1.0/24'")
            .Should().Equal("192.168.1.50", "192.168.1.0");

    [Fact]
    public void Extract_BlockedIpWarning_FindsTheAddress()
        => LogIpExtractor.Extract(
                "MAIL FROM rejected from 10.20.30.40: IP is blocked after repeated failures (until 14:05:00 UTC)")
            .Should().Equal("10.20.30.40");

    [Fact]
    public void Extract_LoopbackIpv6_IsFound()
        => LogIpExtractor.Extract("Connection from ::1 accepted").Should().Equal("::1");

    [Fact]
    public void Extract_LinkLocalIpv6WithZoneId_IsFound()
        => LogIpExtractor.Extract("Session opened from fe80::1%3 on port 25")
            .Should().Equal("fe80::1%3");

    [Fact]
    public void Extract_FullIpv6_IsFound()
        => LogIpExtractor.Extract("Sender rejected from 2001:db8::8a2e:370:7334")
            .Should().Equal("2001:db8::8a2e:370:7334");

    // =========================================================================
    // False positives — the whole reason this class exists
    // =========================================================================

    [Fact]
    public void Extract_VersionNumber_IsNotAnAddress()
        => LogIpExtractor.Extract("Update available: 1.3.3.1067 supersedes 1.3.2.998")
            .Should().BeEmpty("a four-part build number is not an address — the last group is out of range");

    [Fact]
    public void Extract_DefenderPlatformPath_IsNotAnAddress()
        => LogIpExtractor.Extract(@"Provider at C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.26070.9-0\MpOav.dll")
            .Should().BeEmpty();

    [Fact]
    public void Extract_WallClockTime_IsNotAnAddress()
        => LogIpExtractor.Extract("Block expires at 13:45:30 UTC")
            .Should().BeEmpty("three colon-separated numbers are not eight IPv6 groups");

    [Fact]
    public void Extract_QualifiedNameWithDoubleColon_IsNotAnAddress()
        => LogIpExtractor.Extract("Native call failed in Amsi::ScanBuffer")
            .Should().BeEmpty("without the lookarounds this yields '::ba', which parses as a valid address");

    [Fact]
    public void Extract_OutOfRangeOctets_AreRejected()
        => LogIpExtractor.Extract("Counter reached 999.888.777.666").Should().BeEmpty();

    [Fact]
    public void Extract_PaddedOctets_AreRejected()
        // Zero-padded octets are ambiguous (octal, historically), so they must never become a
        // filter entry that reads back differently from what the log showed.
        => LogIpExtractor.Extract("Peer 010.000.000.001 connected").Should().BeEmpty();

    // =========================================================================
    // Shape of the result
    // =========================================================================

    [Fact]
    public void Extract_AddressNamedTwice_IsOfferedOnce()
        => LogIpExtractor.Extract("10.1.1.1 blocked; later 10.1.1.1 retried")
            .Should().Equal("10.1.1.1");

    [Fact]
    public void Extract_SeveralAddresses_KeepsTheOrderTheyAppearIn()
        => LogIpExtractor.Extract("Relay from 10.1.1.1 to 10.2.2.2 via 10.3.3.3")
            .Should().Equal("10.1.1.1", "10.2.2.2", "10.3.3.3");

    [Fact]
    public void Extract_AddressWithPort_YieldsTheAddressWithoutThePort()
        => LogIpExtractor.Extract("Remote endpoint 192.168.0.9:587 closed")
            .Should().Equal("192.168.0.9");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Listener configured on port 25 (mode: StartTls, auth: required)")]
    public void Extract_NothingToFind_ReturnsEmpty(string? text)
        => LogIpExtractor.Extract(text).Should().BeEmpty();
}
