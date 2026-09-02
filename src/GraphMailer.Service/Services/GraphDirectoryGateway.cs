using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace GraphMailer.Service.Services;

/// <summary>
/// What kind of recipient an address belongs to. Decides how a message is delivered:
/// only a <see cref="Mailbox"/> can be the user key in /users/{key}/sendMail — a
/// <see cref="Group"/> has to be relayed through a mailbox that holds SendAs on it.
/// </summary>
internal enum TenantRecipientKind
{
    Mailbox,
    Group,
}

/// <summary>
/// A tenant recipient as relevant for sender validation: Graph object id, UPN and every
/// SMTP address (primary + aliases).
/// AccountEnabled is informational only — shared mailboxes have it set to false
/// but are perfectly valid senders.
/// </summary>
internal sealed record TenantUser(
    string Id,
    string? UserPrincipalName,
    bool AccountEnabled,
    IReadOnlyList<string> SmtpAddresses,
    TenantRecipientKind Kind = TenantRecipientKind.Mailbox,
    string? DisplayName = null)
{
    /// <summary>
    /// The primary SMTP address, falling back to the UPN when the object carries no address at
    /// all. Not the other way round: the UPN may be a sign-in name in a domain that carries no
    /// mail, which would be a misleading thing to show as an address.
    /// </summary>
    internal string? PrimaryOrFirstAddress()
        => SmtpAddresses.FirstOrDefault() ?? UserPrincipalName;
}

/// <summary>
/// Thin Graph access layer for the tenant sender directory.
/// Separated from <see cref="TenantSenderDirectory"/> so the cache logic is unit-testable
/// without a GraphServiceClient. Requires the User.Read.All application permission;
/// the group lookups additionally need Group.Read.All.
/// </summary>
internal interface IGraphDirectoryGateway
{
    /// <summary>Enumerates all tenant users with their SMTP addresses (paged).</summary>
    Task<IReadOnlyList<TenantUser>> GetAllUsersAsync(CancellationToken ct);

    /// <summary>
    /// Enumerates all mail-enabled groups — distribution groups, mail-enabled security
    /// groups and Microsoft 365 groups. Dynamic distribution groups are not exposed by
    /// Graph and are therefore never returned.
    /// </summary>
    Task<IReadOnlyList<TenantUser>> GetAllMailEnabledGroupsAsync(CancellationToken ct);

    /// <summary>Looks up a single user by UPN, mail or any proxyAddress. Null = no such sender.</summary>
    Task<TenantUser?> FindBySmtpAddressAsync(string address, CancellationToken ct);

    /// <summary>Looks up a single mail-enabled group by mail or proxyAddress. Null = no such group.</summary>
    Task<TenantUser?> FindGroupBySmtpAddressAsync(string address, CancellationToken ct);

    /// <summary>
    /// The tenant's verified domains that carry mail — including the coexistence domain
    /// <c>&lt;tenant&gt;.mail.onmicrosoft.com</c>. Used to accept senders Graph cannot enumerate
    /// (mail-enabled public folders, dynamic distribution groups). Needs Domain.Read.All.
    /// </summary>
    Task<IReadOnlyList<string>> GetVerifiedMailDomainsAsync(CancellationToken ct);
}

internal sealed class GraphDirectoryGateway : IGraphDirectoryGateway
{
    private static readonly string[] UserSelect =
        ["id", "displayName", "userPrincipalName", "mail", "proxyAddresses", "accountEnabled", "userType"];

    private static readonly string[] GroupSelect =
        ["id", "displayName", "mail", "proxyAddresses"];

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

    public async Task<IReadOnlyList<TenantUser>> GetAllMailEnabledGroupsAsync(CancellationToken ct)
    {
        var client = _clientProvider.GetClient(_graphOptions.CurrentValue);

        var firstPage = await client.Groups.GetAsync(rc =>
        {
            rc.QueryParameters.Filter = "mailEnabled eq true";
            rc.QueryParameters.Select = GroupSelect;
            rc.QueryParameters.Top = 999;
        }, ct);

        var groups = new List<TenantUser>();
        if (firstPage is null) return groups;

        var iterator = PageIterator<Group, GroupCollectionResponse>.CreatePageIterator(
            client, firstPage,
            group =>
            {
                var mapped = MapGroup(group);
                if (mapped is not null) groups.Add(mapped);
                return true;
            });

        await iterator.IterateAsync(ct);
        return groups;
    }

    public async Task<TenantUser?> FindBySmtpAddressAsync(string address, CancellationToken ct)
    {
        var client = _clientProvider.GetClient(_graphOptions.CurrentValue);

        var escaped = EscapeODataLiteral(address);

        // proxyAddresses stores values with an "smtp:"/"SMTP:" prefix that is part of
        // the compared string, so both casings must be queried. Filtering on
        // proxyAddresses is an advanced query: requires $count=true + ConsistencyLevel.
        //
        // Not matched on userPrincipalName, for the reason given on MapUser: it is a sign-in
        // name, not necessarily a mail address. Matching it here would accept as a sender what
        // the full sync deliberately leaves out.
        var response = await client.Users.GetAsync(rc =>
        {
            rc.QueryParameters.Filter =
                $"mail eq '{escaped}' " +
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

    public async Task<TenantUser?> FindGroupBySmtpAddressAsync(string address, CancellationToken ct)
    {
        var client = _clientProvider.GetClient(_graphOptions.CurrentValue);

        var escaped = EscapeODataLiteral(address);

        var response = await client.Groups.GetAsync(rc =>
        {
            rc.QueryParameters.Filter =
                $"mail eq '{escaped}' " +
                $"or proxyAddresses/any(p:p eq 'smtp:{escaped}') " +
                $"or proxyAddresses/any(p:p eq 'SMTP:{escaped}')";
            rc.QueryParameters.Count = true;
            rc.QueryParameters.Select = GroupSelect;
            rc.QueryParameters.Top = 1;
            rc.Headers.Add("ConsistencyLevel", "eventual");
        }, ct);

        var group = response?.Value?.FirstOrDefault();
        return group is null ? null : MapGroup(group);
    }

    public async Task<IReadOnlyList<string>> GetVerifiedMailDomainsAsync(CancellationToken ct)
    {
        var client = _clientProvider.GetClient(_graphOptions.CurrentValue);

        // No filter/paging parameters on purpose: both are documented as unreliable on
        // /domains, and a tenant has a handful of domains, not thousands.
        var response = await client.Domains.GetAsync(cancellationToken: ct);

        var domains = new List<string>();
        foreach (var domain in response?.Value ?? [])
        {
            if (domain.Id is null || domain.IsVerified != true) continue;

            var carriesMail = domain.SupportedServices?.Any(
                s => s.Equals("Email", StringComparison.OrdinalIgnoreCase)) ?? false;
            if (!carriesMail) continue;

            domains.Add(domain.Id);
        }

        return domains;
    }

    /// <summary>OData string literal: single quotes are escaped by doubling.</summary>
    private static string EscapeODataLiteral(string value) => value.Replace("'", "''");

    /// <summary>
    /// Flattens mail and the smtp proxyAddresses into one SMTP address list.
    ///
    /// The UPN is deliberately not among them. It is a sign-in name that only *should* match the
    /// primary address — an account synced from on-premises Active Directory commonly keeps the
    /// internal AD suffix (<c>user@ad.corp.com</c>), which is not a mail domain at all. Taking it
    /// for a sender address would let MAIL FROM through for something that can neither send nor
    /// receive, and Exchange would refuse the send afterwards. Every address a mailbox may really
    /// send as is in mail/proxyAddresses, so nothing is lost by ignoring it.
    ///
    /// B2B guests are dropped. They have no mailbox in this tenant and can therefore never be a
    /// sender, but they would otherwise bloat the directory — and, worse, they carry their home
    /// organisation's address (<c>name_partner.com#EXT#@tenant.onmicrosoft.com</c> plus the real
    /// <c>name@partner.com</c>), which would drag a foreign domain into the derived mail-domain set
    /// and let anyone spoofing an address there past MAIL FROM.
    /// </summary>
    internal static TenantUser? MapUser(User user)
    {
        if (user.Id is null) return null;
        if (string.Equals(user.UserType, "Guest", StringComparison.OrdinalIgnoreCase)) return null;

        // Not mail-enabled in any way: no mail address, no proxy addresses. Admin accounts,
        // service principals' companion users, licence placeholders. They own no mailbox, so
        // sending as them can never work — better a clean 550 at MAIL FROM than a failed relay,
        // and it keeps them out of the directory the operator has to read.
        var mailEnabled = !string.IsNullOrWhiteSpace(user.Mail)
                          || (user.ProxyAddresses ?? []).Any(
                              p => p.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase));
        if (!mailEnabled) return null;

        return new TenantUser(
            user.Id,
            user.UserPrincipalName,
            user.AccountEnabled ?? true,
            FlattenAddresses(user.Mail, user.ProxyAddresses),
            DisplayName: user.DisplayName);
    }

    /// <summary>
    /// Same flattening for a mail-enabled group. A group without any SMTP address is
    /// dropped — it can neither be validated nor sent as.
    /// </summary>
    private static TenantUser? MapGroup(Group group)
    {
        if (group.Id is null) return null;

        var addresses = FlattenAddresses(group.Mail, group.ProxyAddresses);
        if (addresses.Count == 0) return null;

        return new TenantUser(
            group.Id,
            group.Mail,
            AccountEnabled: true,
            addresses,
            TenantRecipientKind.Group,
            group.DisplayName);
    }

    /// <summary>
    /// mail + the smtp proxyAddresses, de-duplicated case-insensitively. Order is deliberate:
    /// the primary address comes first so it can be shown as such.
    /// </summary>
    private static IReadOnlyList<string> FlattenAddresses(string? mail, List<string>? proxyAddresses)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new List<string>();

        void Add(string? address)
        {
            if (!string.IsNullOrWhiteSpace(address) && seen.Add(address)) addresses.Add(address);
        }

        Add(mail);
        foreach (var proxy in proxyAddresses ?? [])
        {
            if (proxy.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
                Add(proxy["smtp:".Length..]);
        }

        return addresses;
    }
}
