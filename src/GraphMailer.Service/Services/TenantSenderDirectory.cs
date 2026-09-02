using System.Collections.Concurrent;
using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models.ODataErrors;

namespace GraphMailer.Service.Services;

internal enum SenderLookupResult
{
    /// <summary>The address belongs to a tenant recipient (incl. aliases / shared mailboxes / groups).</summary>
    Valid,
    /// <summary>
    /// Not a known recipient, but the address sits in one of the tenant's own verified mail
    /// domains. This is what mail-enabled public folders and dynamic distribution groups look
    /// like — Graph cannot enumerate either of them.
    /// </summary>
    KnownDomain,
    /// <summary>Graph positively confirmed there is no such sender in the tenant.</summary>
    Unknown,
    /// <summary>Validation impossible right now (Graph unreachable, permission missing, timeout).</summary>
    Indeterminate,
}

/// <summary>Outcome of a full directory sync, surfaced in the ConfigTool status display.</summary>
/// <param name="Warning">
/// Set when the sync succeeded but an opt-in part of it did not — typically a missing
/// Group.Read.All. Surfaced in the ConfigTool status line so the operator sees what to grant.
/// </param>
internal sealed record SenderDirectoryRefreshResult(
    bool Success,
    int UserCount,
    int AddressCount,
    string? Error,
    int GroupCount = 0,
    int DomainCount = 0,
    string? Warning = null);

/// <summary>
/// Cached view of the tenant's sender addresses, used to validate SMTP MAIL FROM
/// before a message is queued.
/// </summary>
internal interface ITenantSenderDirectory
{
    Task<SenderLookupResult> ValidateAsync(string address, CancellationToken ct = default);

    /// <summary>
    /// Resolves an SMTP address to the Graph object id of the owning mailbox, so aliases
    /// (secondary proxyAddresses) can be used as user key in /users/{key}/sendMail.
    /// Returns false when the feature is disabled, the address is not cached, or the
    /// address belongs to a group — a group id is never a valid sendMail user key.
    /// </summary>
    bool TryResolveGraphUserKey(string address, out string graphUserKey);

    /// <summary>
    /// Like <see cref="TryResolveGraphUserKey"/> but also reports groups, so the caller can
    /// decide to relay instead of sending directly.
    /// </summary>
    bool TryResolveSender(string address, out string graphUserKey, out TenantRecipientKind kind);

    /// <summary>Full directory sync; replaces the positive cache atomically. Never throws.</summary>
    Task<SenderDirectoryRefreshResult> RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Every recipient currently cached, once each. Feeds the ConfigTool's read-only directory
    /// viewer so an operator can see what the tenant sync actually recognised.
    /// </summary>
    IReadOnlyList<TenantUser> Recipients();

    /// <summary>
    /// The tenant mail domains derived from those recipients. Shown next to them in the viewer:
    /// they decide whether a public folder or dynamic distribution group gets through, and nothing
    /// else makes them visible.
    /// </summary>
    IReadOnlyList<string> MailDomains();
}

/// <summary>
/// Caching strategy (pattern follows IpBlockingService — ConcurrentDictionary, lazy expiry):
///   - Positive cache: address → TenantUser, rebuilt by full sync (atomic reference swap,
///     lock-free reads) and extended by on-demand lookup hits.
///   - Negative cache: address → expiry, so repeated unknown senders don't hammer Graph.
///   - Domain list: the tenant mail domains derived from the directory, so senders Graph cannot
///     enumerate at all still have a way through (SenderValidation.AcceptMailboxlessSenders).
///   - On-demand lookups are bounded (SemaphoreSlim) and time-limited so MAIL FROM
///     never hangs on a slow Graph call.
/// Fail-open: any Graph failure yields Indeterminate; the caller decides (FailClosed).
/// </summary>
internal sealed class TenantSenderDirectory : ITenantSenderDirectory
{
    private readonly IGraphDirectoryGateway _gateway;
    private readonly IOptionsMonitor<SenderValidationOptions> _options;
    private readonly IOptionsMonitor<GraphApiOptions> _graphOptions;
    private readonly IAdminNotificationService _notify;
    private readonly ILogger<TenantSenderDirectory> _logger;

    private volatile ConcurrentDictionary<string, TenantUser> _byAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _negative =
        new(StringComparer.OrdinalIgnoreCase);

    // Tenant mail domains derived from the directory, stored with the leading '@' so matching is a plain suffix
    // compare — exactly the semantics of an "@domain" allow-list entry (no subdomains).
    private volatile string[] _mailDomains = [];

    // Bounds concurrent on-demand Graph lookups triggered from MAIL FROM
    private readonly SemaphoreSlim _lookupSemaphore = new(2, 2);

    // 0 = no outage notification pending; 1 = already notified for the current outage
    private int _outageNotified;

    public TenantSenderDirectory(
        IGraphDirectoryGateway gateway,
        IOptionsMonitor<SenderValidationOptions> options,
        IOptionsMonitor<GraphApiOptions> graphOptions,
        IAdminNotificationService notify,
        ILogger<TenantSenderDirectory> logger)
    {
        _gateway = gateway;
        _options = options;
        _graphOptions = graphOptions;
        _notify = notify;
        _logger = logger;
    }

    public async Task<SenderLookupResult> ValidateAsync(string address, CancellationToken ct = default)
    {
        // Null reverse path (MAIL FROM:<>) — legitimate for NDRs, never validated
        if (address == "@")
            return SenderLookupResult.Valid;

        // Positive cache (shared mailboxes have AccountEnabled=false but are valid senders)
        if (_byAddress.ContainsKey(address))
            return SenderLookupResult.Valid;

        // Negative cache with lazy expiry
        if (_negative.TryGetValue(address, out var expiresAt))
        {
            if (DateTime.UtcNow < expiresAt)
                return NotFound(address);
            _negative.TryRemove(address, out _);
        }

        if (!_graphOptions.CurrentValue.IsConfigured)
            return SenderLookupResult.Indeterminate;

        return await LookupOnDemandAsync(address, ct);
    }

    public bool TryResolveGraphUserKey(string address, out string graphUserKey)
    {
        graphUserKey = string.Empty;

        if (!TryResolveSender(address, out var key, out var kind)) return false;

        // A group object id is not a valid sendMail user key: /users/{groupId}/sendMail
        // answers "Group Shard is used in non-Groups URI".
        if (kind != TenantRecipientKind.Mailbox) return false;

        graphUserKey = key;
        return true;
    }

    public bool TryResolveSender(string address, out string graphUserKey, out TenantRecipientKind kind)
    {
        graphUserKey = string.Empty;
        kind = TenantRecipientKind.Mailbox;

        if (!_options.CurrentValue.Enabled) return false;
        if (!_byAddress.TryGetValue(address, out var recipient)) return false;

        graphUserKey = recipient.Id;
        kind = recipient.Kind;
        return true;
    }

    public async Task<SenderDirectoryRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;

        try
        {
            // User.Read.All is the hard requirement — without the user directory there is
            // nothing to validate against, so a failure here fails the whole sync.
            var users = await _gateway.GetAllUsersAsync(ct);

            // The group sync is an opt-in add-on behind its own extra permission.
            // A missing permission must degrade to "this part is stale" — never take the user
            // directory down with it, which would reject every sender the cache no longer knows.
            var (groups, groupWarning) = opts.AcceptMailboxlessSenders
                ? await TryLoadAsync(_gateway.GetAllMailEnabledGroupsAsync, "groups", "Group.Read.All", ct)
                : (Array.Empty<TenantUser>(), null);

            // Always asked for, not just when mailbox-less senders are accepted: the domain list
            // decides which of a mailbox's addresses can send at all, so it shapes the directory
            // itself. Still optional in the sense that a missing permission only degrades.
            var (domains, domainWarning) = await TryLoadAsync(
                _gateway.GetVerifiedMailDomainsAsync, "mail domains", "Domain.Read.All", ct);

            // Keep the previous list when the lookup failed, so a missing permission does not
            // start rejecting public-folder senders that were working a minute ago.
            if (domainWarning is null)
                _mailDomains = ToDomainSuffixes(domains);
            var mailDomains = _mailDomains;

            var syncedUsers = PruneToMailDomains(users, mailDomains);

            var map = new ConcurrentDictionary<string, TenantUser>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in syncedUsers)
                foreach (var addr in user.SmtpAddresses)
                    map.TryAdd(addr, user);

            // Mailboxes win over groups on a shared address: only a mailbox has a usable
            // sendMail user key, so it is the better answer for the same address.
            var groupEntries = PruneToMailDomains(
                groupWarning is null ? groups : CarryOverGroups(), mailDomains);
            foreach (var group in groupEntries)
                foreach (var addr in group.SmtpAddresses)
                    map.TryAdd(addr, group);

            _byAddress = map;          // atomic swap — readers never see a partial cache
            _negative.Clear();         // fresh sync supersedes all negative results

            var groupCount = groupEntries.Distinct().Count();
            var warning = Combine(groupWarning, domainWarning);

            if (warning is null) OnGraphSuccess();

            _logger.LogInformation(
                "[SenderValidation] Directory sync complete: {Users} user(s), {Groups} group(s), " +
                "{Addresses} sender address(es), {Domains} mail domain(s)",
                syncedUsers.Count, groupCount, map.Count, mailDomains.Length);

            return new SenderDirectoryRefreshResult(
                true, syncedUsers.Count, map.Count, null, groupCount, mailDomains.Length, warning);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown — not an outage
            return new SenderDirectoryRefreshResult(false, 0, 0, "canceled");
        }
        catch (Exception ex)
        {
            await OnGraphFailureAsync("directory sync", ex, ct);
            return new SenderDirectoryRefreshResult(false, 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// Runs one optional part of the sync. On failure the part is reported as degraded instead of
    /// failing the sync: the missing piece is almost always a permission that was never granted
    /// (the option was switched on without re-running the Entra setup), and taking sender
    /// validation down over that would be far worse than working with the previous data.
    /// </summary>
    private async Task<(T[] Items, string? Warning)> TryLoadAsync<T>(
        Func<CancellationToken, Task<IReadOnlyList<T>>> load,
        string what,
        string permission,
        CancellationToken ct)
    {
        try
        {
            return ([.. await load(ct)], null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown — handled by the caller
        }
        catch (Exception ex)
        {
            var denied = ex is ODataError { ResponseStatusCode: 403 or 401 };
            var warning = denied
                ? $"{what} could not be synced — the {permission} application permission is missing. " +
                  "Re-run the Entra setup in the ConfigTool (Graph API page) to grant it."
                : $"{what} could not be synced: {ex.Message}";

            // The operator's only notification channel for a permission they have to grant
            // themselves, and the reason a whole sender type keeps being rejected.
            _logger.LogError(
                "[SenderValidation] {Warning} The rest of the directory was synced normally.",
                warning);

            if (Interlocked.Exchange(ref _outageNotified, 1) == 0)
            {
                try { await _notify.NotifyGraphApiErrorAsync($"Sender validation: {warning}", ct); }
                catch (Exception notifyEx) { _logger.LogDebug(notifyEx, "[SenderValidation] Admin notification failed"); }
            }

            return ([], warning);
        }
    }

    /// <summary>
    /// One entry per recipient. The cache is keyed by address, so a mailbox with three aliases
    /// appears three times — distinct by object identity collapses that back to one row.
    /// </summary>
    public IReadOnlyList<TenantUser> Recipients() => [.. _byAddress.Values.Distinct()];

    /// <summary>The derived tenant mail domains, each with its leading '@'.</summary>
    public IReadOnlyList<string> MailDomains() => _mailDomains;

    /// <summary>
    /// Group entries from the previous cache, so a failed group sync keeps the groups that were
    /// already known instead of silently turning them into unknown senders.
    /// </summary>
    private TenantUser[] CarryOverGroups()
        => [.. _byAddress.Values.Where(r => r.Kind == TenantRecipientKind.Group).Distinct()];

    /// <summary>
    /// The tenant's own mail domains, derived from the directory we already hold instead of asking
    /// Graph for them — which saves the Domain.Read.All permission and is, for this purpose, the
    /// better source anyway.
    ///
    /// Taken from Entra's verified domains rather than derived from the directory entries, because
    /// deriving cannot be made correct: an unlicensed Entra account has no Exchange object behind
    /// it, yet can carry any address at all in its proxyAddresses — including a primary
    /// <c>SMTP:</c> in a domain the tenant does not own. Graph offers nothing that separates such
    /// an account from a genuine mail user, so any address-based rule either lets a foreign domain
    /// in or drops real recipients. Meanwhile the domain that matters most for hybrid tenants,
    /// <c>&lt;tenant&gt;.mail.onmicrosoft.com</c>, appears in no UPN at all.
    ///
    /// The blind spot is a verified domain that carries no mail service. Senders there need a route.
    ///
    /// Stored with the leading '@' so matching is a plain suffix compare — exactly the semantics
    /// of an "@domain" allow-list entry (no subdomains).
    /// </summary>
    internal static string[] ToDomainSuffixes(IReadOnlyList<string> verifiedDomains)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var domain in verifiedDomains)
        {
            if (string.IsNullOrWhiteSpace(domain)) continue;
            domains.Add(domain.StartsWith('@') ? domain : "@" + domain);
        }

        return [.. domains];
    }

    /// <summary>
    /// Drops every address outside the tenant's mail domains, and every recipient left without one.
    ///
    /// Directory synchronisation copies the on-premises <c>proxyAddresses</c> attribute into Entra
    /// as it stands, so a mailbox routinely carries addresses in the internal AD namespace
    /// (<c>user@ad.corp.com</c>) next to its real ones. Those domains are not accepted domains in
    /// Exchange — nothing can be sent from them, and Exchange refuses even a SendAs grant over one.
    /// Keeping them would accept such a sender at MAIL FROM only for Microsoft 365 to reject the
    /// message at delivery, which is the outcome sender validation exists to prevent.
    ///
    /// With no domain list — permission missing, lookup failed, never synced — nothing is pruned:
    /// filtering against an empty list would empty the directory.
    /// </summary>
    internal static IReadOnlyList<TenantUser> PruneToMailDomains(
        IReadOnlyList<TenantUser> recipients, string[] mailDomains)
    {
        if (mailDomains.Length == 0) return recipients;

        var kept = new List<TenantUser>(recipients.Count);

        foreach (var recipient in recipients)
        {
            var addresses = recipient.SmtpAddresses
                .Where(a => EndsWithAny(a, mailDomains))
                .ToArray();

            if (addresses.Length == 0) continue;

            kept.Add(addresses.Length == recipient.SmtpAddresses.Count
                ? recipient
                : recipient with { SmtpAddresses = addresses });
        }

        return kept;
    }

    private static bool EndsWithAny(string address, string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (address.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? Combine(string? first, string? second)
        => (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => $"{first} {second}",
        };

    /// <summary>
    /// A confirmed miss. Downgrades to <see cref="SenderLookupResult.KnownDomain"/> when the
    /// address at least belongs to one of the tenant's own mail domains — the only signal
    /// Graph leaves us for mail-enabled public folders and dynamic distribution groups.
    /// </summary>
    private SenderLookupResult NotFound(string address)
        => IsInTenantDomain(address) ? SenderLookupResult.KnownDomain : SenderLookupResult.Unknown;

    private bool IsInTenantDomain(string address) => EndsWithAny(address, _mailDomains);

    private async Task<SenderLookupResult> LookupOnDemandAsync(string address, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, opts.LookupTimeoutSeconds)));

        try
        {
            await _lookupSemaphore.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[SenderValidation] Lookup for {Address} skipped — too many concurrent lookups", address);
            return SenderLookupResult.Indeterminate;
        }

        try
        {
            var recipient = await _gateway.FindBySmtpAddressAsync(address, cts.Token);

            // Groups live in a different Graph collection, so a user miss is not yet a miss.
            if (recipient is null && opts.AcceptMailboxlessSenders)
                recipient = await _gateway.FindGroupBySmtpAddressAsync(address, cts.Token);

            // An object can carry the address and still not be able to send from it: an
            // on-premises address synced into proxyAddresses belongs to no accepted domain.
            // The full sync prunes those, and this path must agree with it — otherwise the
            // lookup would quietly re-admit exactly what the sync leaves out.
            if (recipient is not null && _mailDomains.Length > 0 && !IsInTenantDomain(address))
            {
                _logger.LogDebug(
                    "[SenderValidation] Sender {Address} exists in the tenant but is outside its " +
                    "mail domains — not a usable sender address", address);
                recipient = null;
            }

            OnGraphSuccess();

            if (recipient is null)
            {
                _negative[address] = DateTime.UtcNow.AddSeconds(Math.Max(1, opts.NegativeCacheSeconds));
                _logger.LogInformation(
                    "[SenderValidation] Sender {Address} not found in tenant (negative-cached for {Ttl}s)",
                    address, opts.NegativeCacheSeconds);
                return NotFound(address);
            }

            foreach (var entry in PruneToMailDomains([recipient], _mailDomains))
                foreach (var addr in entry.SmtpAddresses)
                    _byAddress.TryAdd(addr, entry);

            _logger.LogDebug(
                "[SenderValidation] Sender {Address} resolved to {Kind} {Id}",
                address, recipient.Kind, recipient.Id);
            return SenderLookupResult.Valid;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[SenderValidation] Lookup for {Address} timed out after {Timeout}s",
                address, opts.LookupTimeoutSeconds);
            return SenderLookupResult.Indeterminate;
        }
        catch (Exception ex)
        {
            await OnGraphFailureAsync($"lookup for {address}", ex, ct);
            return SenderLookupResult.Indeterminate;
        }
        finally
        {
            _lookupSemaphore.Release();
        }
    }

    private void OnGraphSuccess() => Interlocked.Exchange(ref _outageNotified, 0);

    /// <summary>Logs the failure (with a permission hint on 403) and notifies once per outage.</summary>
    private async Task OnGraphFailureAsync(string operation, Exception ex, CancellationToken ct)
    {
        var detail = ex is ODataError { ResponseStatusCode: 403 }
            ? "access denied — grant the Entra app registration User.Read.All (plus Group.Read.All " +
              "for groups, if that option is enabled)"
            : ex.Message;

        _logger.LogWarning(
            "[SenderValidation] Graph {Operation} failed ({Detail}) — senders are accepted unvalidated (fail-open)",
            operation, detail);

        if (Interlocked.Exchange(ref _outageNotified, 1) == 0)
        {
            try
            {
                await _notify.NotifyGraphApiErrorAsync($"Sender validation {operation} failed: {detail}", ct);
            }
            catch (Exception notifyEx)
            {
                _logger.LogDebug(notifyEx, "[SenderValidation] Admin notification failed");
            }
        }
    }
}
