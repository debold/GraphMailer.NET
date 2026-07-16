using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace GraphMailer.Service.Services;

/// <summary>
/// A tenant user as relevant for sender validation: Graph object id (always a valid
/// sendMail user key), UPN and every SMTP address (primary + aliases).
/// AccountEnabled is informational only — shared mailboxes have it set to false
/// but are perfectly valid senders.
/// </summary>
internal sealed record TenantUser(
    string Id,
    string? UserPrincipalName,
    bool AccountEnabled,
    IReadOnlyList<string> SmtpAddresses);

/// <summary>
/// Thin Graph access layer for the tenant sender directory.
/// Separated from <see cref="TenantSenderDirectory"/> so the cache logic is unit-testable
/// without a GraphServiceClient. Requires the User.Read.All application permission.
/// </summary>
internal interface IGraphDirectoryGateway
{
    /// <summary>Enumerates all tenant users with their SMTP addresses (paged).</summary>
    Task<IReadOnlyList<TenantUser>> GetAllUsersAsync(CancellationToken ct);

    /// <summary>Looks up a single user by UPN, mail or any proxyAddress. Null = no such sender.</summary>
    Task<TenantUser?> FindBySmtpAddressAsync(string address, CancellationToken ct);
}

internal sealed class GraphDirectoryGateway : IGraphDirectoryGateway
{
    private static readonly string[] UserSelect =
        ["id", "userPrincipalName", "mail", "proxyAddresses", "accountEnabled"];

    private readonly GraphClientProvider _clientProvider;
    private readonly IOptionsMonitor<GraphApiOptions> _graphOptions;

    public GraphDirectoryGateway(
        GraphClientProvider clientProvider,
        IOptionsMonitor<GraphApiOptions> graphOptions)
    {
        _clientProvider = clientProvider;
        _graphOptions = graphOptions;
    }

    public async Task<IReadOnlyList<TenantUser>> GetAllUsersAsync(CancellationToken ct)
    {
        var client = _clientProvider.GetClient(_graphOptions.CurrentValue);

        var firstPage = await client.Users.GetAsync(rc =>
        {
            rc.QueryParameters.Select = UserSelect;
            rc.QueryParameters.Top = 999;
        }, ct);

        var users = new List<TenantUser>();
        if (firstPage is null) return users;

        var iterator = PageIterator<User, UserCollectionResponse>.CreatePageIterator(
            client, firstPage,
            user =>
            {
                var mapped = MapUser(user);
                if (mapped is not null) users.Add(mapped);
                return true;
            });

        await iterator.IterateAsync(ct);
        return users;
    }

    public async Task<TenantUser?> FindBySmtpAddressAsync(string address, CancellationToken ct)
    {
        var client = _clientProvider.GetClient(_graphOptions.CurrentValue);

        // OData string literal: single quotes are escaped by doubling.
        var escaped = address.Replace("'", "''");

        // proxyAddresses stores values with an "smtp:"/"SMTP:" prefix that is part of
        // the compared string, so both casings must be queried. Filtering on
        // proxyAddresses is an advanced query: requires $count=true + ConsistencyLevel.
        var response = await client.Users.GetAsync(rc =>
        {
            rc.QueryParameters.Filter =
                $"userPrincipalName eq '{escaped}' or mail eq '{escaped}' " +
                $"or proxyAddresses/any(p:p eq 'smtp:{escaped}') " +
                $"or proxyAddresses/any(p:p eq 'SMTP:{escaped}')";
            rc.QueryParameters.Count = true;
            rc.QueryParameters.Select = UserSelect;
            rc.QueryParameters.Top = 1;
            rc.Headers.Add("ConsistencyLevel", "eventual");
        }, ct);

        var user = response?.Value?.FirstOrDefault();
        return user is null ? null : MapUser(user);
    }

    /// <summary>Flattens UPN, mail and smtp proxyAddresses into one SMTP address list.</summary>
    private static TenantUser? MapUser(User user)
    {
        if (user.Id is null) return null;

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(user.UserPrincipalName) && user.UserPrincipalName.Contains('@'))
            addresses.Add(user.UserPrincipalName);
        if (!string.IsNullOrWhiteSpace(user.Mail))
            addresses.Add(user.Mail);

        foreach (var proxy in user.ProxyAddresses ?? [])
        {
            if (proxy.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
                addresses.Add(proxy["smtp:".Length..]);
        }

        return new TenantUser(
            user.Id,
            user.UserPrincipalName,
            user.AccountEnabled ?? true,
            [.. addresses]);
    }
}
