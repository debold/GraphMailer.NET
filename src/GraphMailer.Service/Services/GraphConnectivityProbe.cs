using System.Text.Json;
using Azure.Core;
using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>Result of a successful probe: the app roles granted to the token.</summary>
internal sealed record GraphProbeResult(IReadOnlyCollection<string> GrantedRoles);

/// <summary>
/// Health probe for Graph API connectivity. Throws when the check fails —
/// callers translate that into down/restored state. On success it reports the
/// granted application permissions (from the token's "roles" claim) so the
/// caller can alert on missing grants.
/// </summary>
internal interface IGraphConnectivityProbe
{
    Task<GraphProbeResult> ProbeAsync(CancellationToken ct);
}

/// <summary>
/// Probes connectivity by acquiring an OAuth2 token from Entra ID with a fresh
/// credential instance. This validates the certificate/secret, the app registration
/// and the network path without sending any mail. A new credential per probe is
/// deliberate: MSAL caches tokens in-memory (~1 h), so a reused credential would
/// keep "succeeding" from cache during a real outage.
/// </summary>
internal sealed class GraphConnectivityProbe : IGraphConnectivityProbe
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly GraphClientProvider _clientProvider;
    private readonly IOptionsMonitor<GraphApiOptions> _options;

    public GraphConnectivityProbe(
        GraphClientProvider clientProvider,
        IOptionsMonitor<GraphApiOptions> options)
    {
        _clientProvider = clientProvider;
        _options = options;
    }

    public async Task<GraphProbeResult> ProbeAsync(CancellationToken ct)
    {
        var credential = _clientProvider.CreateCredential(_options.CurrentValue);
        var token = await credential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);
        return new GraphProbeResult(ParseRoles(token.Token));
    }

    /// <summary>
    /// Extracts the "roles" claim (granted application permissions) from the
    /// access token. No signature validation — the token came straight from
    /// Entra ID. Returns an empty list when the claim is absent (no roles
    /// granted) or the payload is unparseable.
    /// </summary>
    internal static IReadOnlyCollection<string> ParseRoles(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return [];

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!doc.RootElement.TryGetProperty("roles", out var roles) ||
                roles.ValueKind != JsonValueKind.Array)
                return [];

            return [.. roles.EnumerateArray().Select(r => r.GetString()).OfType<string>()];
        }
        catch
        {
            return [];
        }
    }
}
