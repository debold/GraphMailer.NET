using GraphMailer.Service.Services;
using GraphMailer.Tests.Unit.Infrastructure.Security;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Tests for <see cref="PortProbeRegistry"/> — the handshake between the port
/// health checks and the SMTP session logging that keeps probe connections
/// out of the Information/Warning log.
/// </summary>
public sealed class PortProbeRegistryTests
{
    [Fact]
    public void IsProbeConnection_MarkedPortFromLoopback_ReturnsTrue()
    {
        var sut = new PortProbeRegistry();
        sut.MarkProbe(25);

        sut.IsProbeConnection(25, "127.0.0.1").Should().BeTrue();
        sut.IsProbeConnection(25, "::1").Should().BeTrue();
    }

    [Fact]
    public void IsProbeConnection_UnmarkedPort_ReturnsFalse()
    {
        var sut = new PortProbeRegistry();
        sut.MarkProbe(25);

        sut.IsProbeConnection(465, "127.0.0.1").Should().BeFalse();
    }

    [Fact]
    public void IsProbeConnection_NonLoopbackIp_ReturnsFalse()
    {
        // A real client connecting during the probe window must keep its log entry
        var sut = new PortProbeRegistry();
        sut.MarkProbe(25);

        sut.IsProbeConnection(25, "10.0.0.5").Should().BeFalse();
        sut.IsProbeConnection(25, "unknown").Should().BeFalse();
        sut.IsProbeConnection(25, null).Should().BeFalse();
    }

    [Fact]
    public void IsProbeConnection_ProbeWindowElapsed_ReturnsFalse()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new PortProbeRegistry(clock);
        sut.MarkProbe(25);

        clock.Advance(PortProbeRegistry.ProbeWindow + TimeSpan.FromSeconds(1));

        sut.IsProbeConnection(25, "127.0.0.1").Should().BeFalse();
    }
}
