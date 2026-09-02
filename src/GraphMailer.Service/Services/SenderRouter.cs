using System.Collections.Concurrent;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// How one message is going to be addressed to Graph.
/// </summary>
/// <param name="GraphUserKey">Goes into /users/{key}/sendMail — always a real mailbox.</param>
/// <param name="IsRelay">
/// True when <paramref name="GraphUserKey"/> is a relay mailbox and the message keeps a
/// different From. Exchange authorises that via SendAs; the copy would land in the relay
/// mailbox, so relayed sends are never saved to Sent Items.
/// </param>
/// <param name="FallbackRelayMailbox">
/// Relay mailbox to retry through when the direct send turns out to have no mailbox behind
/// it. Null once we are already relaying — there is nothing left to fall back to.
/// </param>
/// <param name="RelayUnvouched">
/// True when nothing vouched for this sender: it is not in the directory, no route names it,
/// and sender validation did not vet it either. A SendAs denial for such an address is final
/// rather than a permission the operator is about to grant.
/// </param>
/// <param name="Reason">Short explanation for the log.</param>
internal readonly record struct SenderRoute(
    string GraphUserKey,
    bool IsRelay,
    string? FallbackRelayMailbox,
    bool RelayUnvouched,
    string Reason);

internal interface ISenderRouter
{
    /// <summary>Decides how the envelope sender reaches Graph. Never throws.</summary>
    SenderRoute Resolve(string envelopeFrom);

    /// <summary>
    /// Records that Graph rejected this sender for not having a mailbox, so the next message
    /// from the same address goes straight through the relay instead of failing first.
    /// </summary>
    void MarkMailboxUnavailable(string envelopeFrom);

    /// <summary>
    /// True when an explicit route covers the address. Lets sender validation accept
    /// recipients Graph cannot enumerate at all (mail-enabled public folders).
    /// </summary>
    bool HasExplicitRoute(string envelopeFrom);
}

/// <summary>
/// Picks the mailbox a message is sent through.
///
/// Graph accepts only a real Exchange Online mailbox as the user key in
/// /users/{key}/sendMail. Distribution groups, mail-enabled public folders and mail users
/// have none, so they are delivered through a relay mailbox that holds SendAs on them while
/// the message keeps the original address in its From header.
///
/// Three ways lead to the relay, in this order: an explicit route, a sender we have already
/// learned has no mailbox, and a sender the directory knows to be a group. Every other sender
/// is sent directly and carries the relay mailbox as a safety net for the case Graph rejects
/// the direct attempt — there is no reason to withhold it: a sender that does have a mailbox
/// never reaches the fallback, and one that does not would otherwise simply be bounced.
///
/// The learned markers use the same ConcurrentDictionary + lazy expiry pattern as
/// TenantSenderDirectory's negative cache.
/// </summary>
internal sealed class SenderRouter : ISenderRouter
{
    private readonly ITenantSenderDirectory _directory;
    private readonly IOptionsMonitor<SenderRoutingOptions> _options;
    private readonly IOptionsMonitor<SenderValidationOptions> _validation;
    private readonly ILogger<SenderRouter> _logger;
    private readonly TimeProvider _clock;

    private readonly ConcurrentDictionary<string, DateTime> _noMailbox =
        new(StringComparer.OrdinalIgnoreCase);

    public SenderRouter(
        ITenantSenderDirectory directory,
        IOptionsMonitor<SenderRoutingOptions> options,
        IOptionsMonitor<SenderValidationOptions> validation,
        ILogger<SenderRouter> logger,
        TimeProvider? clock = null)
    {
        _directory = directory;
        _options = options;
        _validation = validation;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public SenderRoute Resolve(string envelopeFrom)
    {
        var opts = _options.CurrentValue;
        var relay = opts.RelayMailbox?.Trim() ?? "";
        var canRelay = opts.Enabled && relay.Length > 0;

        // 1. Explicit route — the only way to reach recipients Graph cannot enumerate.
        if (opts.Enabled && FindRoute(envelopeFrom, opts.Routes) is { } routed)
            return new SenderRoute(routed.Mailbox, true, null, false, $"explicit route '{routed.Sender}'");

        // 2. Learned from an earlier Graph rejection.
        if (canRelay && HasNoMailbox(envelopeFrom))
            return new SenderRoute(relay, true, null, false, "sender is known to have no mailbox");

        // 3. Directory hit. A group never has a usable user key: /users/{groupId}/sendMail
        //    answers "Group Shard is used in non-Groups URI", and that holds for Microsoft
        //    365 groups too, even though they do own a mailbox.
        if (_directory.TryResolveSender(envelopeFrom, out var key, out var kind))
        {
            if (kind == TenantRecipientKind.Mailbox)
                return new SenderRoute(key, false, Fallback(canRelay, relay), false, "resolved to tenant mailbox");

            if (canRelay)
                return new SenderRoute(relay, true, null, false, "sender is a mail-enabled group");
        }

        // 4. Unknown to us — try it directly and let the relay catch the fallout. With sender
        //    validation on, the address at least passed MAIL FROM; without it, nothing vouched
        //    for the address at all and a SendAs denial later on is final, not a pending grant.
        var unvouched = !_validation.CurrentValue.Enabled;
        return new SenderRoute(envelopeFrom, false, Fallback(canRelay, relay), unvouched, "sender not resolved");
    }

    public void MarkMailboxUnavailable(string envelopeFrom)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled) return;

        _noMailbox[envelopeFrom] =
            _clock.GetUtcNow().UtcDateTime.AddMinutes(Math.Max(1, opts.RelayCacheMinutes));

        _logger.LogDebug(
            "[SenderRouting] {Address} marked as having no mailbox — relaying for the next {Minutes} minute(s)",
            envelopeFrom, Math.Max(1, opts.RelayCacheMinutes));
    }

    public bool HasExplicitRoute(string envelopeFrom)
    {
        var opts = _options.CurrentValue;
        return opts.Enabled && FindRoute(envelopeFrom, opts.Routes) is not null;
    }

    /// <summary>Only a direct send can fall back; a relayed send has nowhere left to go.</summary>
    private static string? Fallback(bool canRelay, string relay) => canRelay ? relay : null;

    /// <summary>
    /// First route whose pattern covers the address. Pattern semantics are shared with the
    /// allow/block lists so operators only have to learn them once.
    /// </summary>
    private static SenderRouteOptions? FindRoute(string address, List<SenderRouteOptions> routes)
    {
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Sender) || string.IsNullOrWhiteSpace(route.Mailbox))
                continue;

            if (MailAddressFilter.Matches(address, route.Sender))
                return route;
        }
        return null;
    }

    /// <summary>Lazy expiry: an entry past its TTL is dropped on read.</summary>
    private bool HasNoMailbox(string address)
    {
        if (!_noMailbox.TryGetValue(address, out var expiresAt)) return false;

        if (_clock.GetUtcNow().UtcDateTime < expiresAt) return true;

        _noMailbox.TryRemove(address, out _);
        return false;
    }
}
