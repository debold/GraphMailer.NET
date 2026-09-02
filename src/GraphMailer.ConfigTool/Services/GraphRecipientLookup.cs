using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace GraphMailer.ConfigTool.Services;

/// <summary>One mail-enabled group offered for SendAs selection.</summary>
internal sealed record MailEnabledGroup(string DisplayName, string Address);

/// <summary>
/// Lists the tenant's mail-enabled groups so the SendAs script can be narrowed to a chosen set.
///
/// Groups are the only sender type that can be enumerated here: mail-enabled public folders have
/// no Graph API at all, dynamic distribution groups are not exposed either, and Graph offers no
/// reliable way to ask whether a user has a mailbox. Those are covered by the all-objects script
/// variant or by entering the addresses by hand.
///
/// Needs the Group.Read.All application permission.
/// </summary>
internal static class GraphRecipientLookup
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    internal static async Task<IReadOnlyList<MailEnabledGroup>> ListMailEnabledGroupsAsync(
        string tenantId,
        string clientId,
        string? clientSecret,
        string? certThumbprint,
        CancellationToken ct)
    {
        var graph = await BuildClientAsync(tenantId, clientId, clientSecret, certThumbprint, ct);

        var page = await graph.Groups.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = "mailEnabled eq true";
            rc.QueryParameters.Select = ["id", "displayName", "mail"];
            rc.QueryParameters.Top = 999;
        }, ct);

        var groups = new List<MailEnabledGroup>();
        if (page is null) return groups;

        var iterator = PageIterator<Group, GroupCollectionResponse>.CreatePageIterator(
            graph, page,
            group =>
            {
                if (!string.IsNullOrWhiteSpace(group.Mail))
                    groups.Add(new MailEnabledGroup(group.DisplayName ?? group.Mail, group.Mail));
                return true;
            });

        await iterator.IterateAsync(ct);

        return [.. groups.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Client-credentials Graph client from the credentials currently in the config —
    /// same construction as <see cref="GraphApiTestService"/>.
    /// </summary>
    private static async Task<GraphServiceClient> BuildClientAsync(
        string tenantId,
        string clientId,
        string? clientSecret,
        string? certThumbprint,
        CancellationToken ct)
    {
        IConfidentialClientApplication msal;

        if (!string.IsNullOrWhiteSpace(certThumbprint))
        {
            var cert = FindCertificate(certThumbprint)
                ?? throw new InvalidOperationException(
                    $"Certificate with thumbprint '{certThumbprint}' not found " +
                    """in LocalMachine\My or CurrentUser\My.""");

            msal = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithTenantId(tenantId)
                .WithCertificate(cert)
                .Build();
        }
        else if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            msal = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithTenantId(tenantId)
                .WithClientSecret(clientSecret)
                .Build();
        }
        else
        {
            throw new InvalidOperationException(
                "No authentication configured. Enter a Client Secret or select a certificate " +
                "on the Graph API page first.");
        }

        var token = await msal.AcquireTokenForClient(Scopes).ExecuteAsync(ct);

        return new GraphServiceClient(
            new BaseBearerTokenAuthenticationProvider(new StaticBearerProvider(token.AccessToken)));
    }

    private static X509Certificate2? FindCertificate(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", "").ToUpperInvariant();

        foreach (var (location, name) in new[]
                 {
                     (StoreLocation.LocalMachine, StoreName.My),
                     (StoreLocation.CurrentUser, StoreName.My),
                 })
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadOnly);

            foreach (var cert in store.Certificates)
            {
                if (cert.Thumbprint.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    return cert;
            }
        }

        return null;
    }

    /// <summary>Returns the pre-acquired token for every request (see GraphApiTestService).</summary>
    private sealed class StaticBearerProvider(string accessToken) : IAccessTokenProvider
    {
        public Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken ct = default)
            => Task.FromResult(accessToken);

        public AllowedHostsValidator AllowedHostsValidator { get; } = new();
    }
}
