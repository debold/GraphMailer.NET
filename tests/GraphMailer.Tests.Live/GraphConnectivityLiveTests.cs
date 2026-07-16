using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphMailer.Tests.Live;

/// <summary>
/// Live test for the connectivity probe used by GraphApiMonitoringService:
/// a real token acquisition against the test tenant.
/// </summary>
public class GraphConnectivityLiveTests
{
    [LiveFact]
    public async Task ProbeAsync_AcquiresToken_WithRequiredRoles()
    {
        var probe = new GraphConnectivityProbe(
            new GraphClientProvider(NullLogger<GraphClientProvider>.Instance),
            new StaticOptionsMonitor<GraphApiOptions>(LiveTestSettings.Current.ToGraphApiOptions()));

        var result = await probe.ProbeAsync(CancellationToken.None);

        result.GrantedRoles.Should().Contain(["Mail.Send", "Mail.ReadWrite", "User.Read.All"],
            "the test tenant's app registration should carry all permissions the wizard grants");
    }
}
