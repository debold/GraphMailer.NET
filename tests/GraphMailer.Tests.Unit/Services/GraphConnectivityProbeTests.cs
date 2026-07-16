using System.Text;
using System.Text.Json;
using GraphMailer.Service.Services;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>Tests for the JWT "roles" claim extraction of <see cref="GraphConnectivityProbe"/>.</summary>
public sealed class GraphConnectivityProbeTests
{
    private static string BuildJwt(object payload)
    {
        static string Encode(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Encode("""{"alg":"RS256","typ":"JWT"}""");
        var body = Encode(JsonSerializer.Serialize(payload));
        return $"{header}.{body}.fake-signature";
    }

    [Fact]
    public void ParseRoles_TokenWithRoles_ReturnsThem()
    {
        var jwt = BuildJwt(new { aud = "https://graph.microsoft.com", roles = new[] { "Mail.Send", "Mail.ReadWrite" } });

        GraphConnectivityProbe.ParseRoles(jwt)
            .Should().BeEquivalentTo("Mail.Send", "Mail.ReadWrite");
    }

    [Fact]
    public void ParseRoles_TokenWithoutRolesClaim_ReturnsEmpty()
    {
        // No app roles granted → Entra omits the claim entirely
        var jwt = BuildJwt(new { aud = "https://graph.microsoft.com" });

        GraphConnectivityProbe.ParseRoles(jwt).Should().BeEmpty();
    }

    [Fact]
    public void ParseRoles_GarbageInput_ReturnsEmpty()
    {
        GraphConnectivityProbe.ParseRoles("not-a-jwt").Should().BeEmpty();
        GraphConnectivityProbe.ParseRoles("a.b.c").Should().BeEmpty();
        GraphConnectivityProbe.ParseRoles("").Should().BeEmpty();
    }
}
