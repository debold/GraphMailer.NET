using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Storage;

namespace GraphMailer.Service.Infrastructure.Smtp;

/// <summary>
/// SmtpServer IMailboxFilter implementation.
///
/// Applies sender and recipient filter rules from config:
///  - AllowedSenders / BlockedSenders  → checked in CanAcceptFromAsync
///  - AllowedRecipients / BlockedRecipients → checked in CanDeliverToAsync
///  - Per-user FromRestrictions (if session is authenticated)
///
/// Also records blocked-sender attempts in IpBlockingService.
/// </summary>
internal sealed class SmtpMailboxFilter : MailboxFilter
{
    private readonly IpBlockingService _ipBlocking;
    private readonly AuthHandler _authHandler;
    private readonly IOptionsMonitor<SmtpAccessOptions> _access;
    private readonly IOptionsMonitor<SmtpOptions> _smtpOptions;
    private readonly IOptionsMonitor<SenderValidationOptions> _senderValidation;
    private readonly ITenantSenderDirectory _senderDirectory;
    private readonly ILogger<SmtpMailboxFilter> _logger;

    public SmtpMailboxFilter(
        IpBlockingService ipBlocking,
        AuthHandler authHandler,
        IOptionsMonitor<SmtpAccessOptions> access,
        IOptionsMonitor<SmtpOptions> smtpOptions,
        IOptionsMonitor<SenderValidationOptions> senderValidation,
        ITenantSenderDirectory senderDirectory,
        ILogger<SmtpMailboxFilter> logger)
    {
        _ipBlocking = ipBlocking;
        _authHandler = authHandler;
        _access = access;
        _smtpOptions = smtpOptions;
        _senderValidation = senderValidation;
        _senderDirectory = senderDirectory;
        _logger = logger;

        // Log the active filter rules once at startup so operators immediately
        // know which allow/block lists are in effect without opening the config file.
        var opts = access.CurrentValue;
        _logger.LogInformation(
            "[SmtpFilter] Active rules – Users: {Users}, IpWhitelist: {IpWL}, IpBlacklist: {IpBL}, " +
            "AllowedSenders: {AS}, BlockedSenders: {BS}, AllowedRecipients: {AR}, BlockedRecipients: {BR}",
            opts.Users.Count,
            opts.IpWhitelist.Count, opts.IpBlacklist.Count,
            opts.AllowedSenders.Count, opts.BlockedSenders.Count,
            opts.AllowedRecipients.Count, opts.BlockedRecipients.Count);
    }

    public override async Task<bool> CanAcceptFromAsync(
        ISessionContext context,
        IMailbox from,
        int size,
        CancellationToken cancellationToken)
    {
        var opts = _access.CurrentValue;
        var remoteIp = IpFilterService.GetRemoteIp(context) ?? "unknown";
        var address = $"{from.User}@{from.Host}";

        _logger.LogDebug("[SmtpFilter] MAIL FROM: <{Address}> from {Ip} (declared size: {Size} bytes)",
            address, remoteIp, size);

        // Warn early when the client declares a size that exceeds our limit.
        // SmtpServer will ultimately reject the DATA transfer itself, but without
        // this Warning there would be no log entry at all for a size rejection –
        // SaveAsync is not called when SmtpServer enforces MaxMessageSize internally.
        var maxSizeBytes = _smtpOptions.CurrentValue.MaxSizeBytes;
        if (size > 0 && size > maxSizeBytes)
        {
            _logger.LogWarning(
                "[SmtpFilter] MAIL FROM: declared size {DeclaredSize:N0} bytes from {Ip} exceeds " +
                "MaxSizeBytes limit ({Limit:N0} bytes); server will reject the message",
                size, remoteIp, maxSizeBytes);
        }

        // IP whitelist / blacklist – network-level gate, evaluated before any
        // application logic so blacklisted IPs are rejected as early as possible.
        if (!IpFilterService.IsAllowed(remoteIp, opts.IpWhitelist, opts.IpBlacklist))
        {
            _logger.LogWarning(
                "[SmtpFilter] MAIL FROM rejected from {Ip}: {Reason}",
                remoteIp,
                IpFilterService.GetDenyReason(remoteIp, opts.IpWhitelist, opts.IpBlacklist));
            return false;
        }

        // Dynamic IP blocking – reject at MAIL FROM rather than waiting until DATA.
        // Without this check a blocked IP would be allowed to send the full message
        // body before being rejected in SmtpMessageStore, wasting bandwidth and CPU.
        if (_ipBlocking.IsBlocked(remoteIp, out var blockedUntil))
        {
            _logger.LogWarning(
                "[SmtpFilter] MAIL FROM rejected from {Ip}: IP is blocked after repeated failures (until {Expires:HH:mm:ss} UTC)",
                remoteIp, blockedUntil);
            return false;
        }

        // Enforce authentication requirement: sessions on auth-required endpoints
        // must be authenticated before being allowed to send mail.
        if (context.Properties.TryGetValue("Auth:Required", out _) &&
            !context.Authentication.IsAuthenticated)
        {
            _logger.LogWarning(
                "[SmtpFilter] MAIL FROM rejected from {Ip}: authentication required but session is unauthenticated",
                remoteIp);
            return false;
        }

        // Reject sessions that attempted authentication but provided wrong credentials.
        // Applies even when AuthRequired = false: if you tried to authenticate and failed,
        // you must not be allowed to send.
        if (context.Properties.TryGetValue("Auth:Failed", out var failedUser))
        {
            _logger.LogWarning(
                "[SmtpFilter] MAIL FROM rejected from {Ip}: prior auth attempt failed (user: {User})",
                remoteIp, failedUser);
            return false;
        }

        // Per-user from-restrictions (authenticated sessions only)
        if (context.Authentication.IsAuthenticated)
        {
            var username = context.Authentication.User;
            if (username is not null)
            {
                var restrictions = _authHandler.GetFromRestrictions(username);
                if (restrictions is not null && !MailAddressFilter.IsAllowed(address, restrictions, []))
                {
                    _ipBlocking.RecordFailure(remoteIp, "blockedSender");
                    _logger.LogWarning(
                        "[SmtpFilter] User {User} not allowed to send from {Address}",
                        username, address);
                    return false;
                }
            }
        }

        if (!MailAddressFilter.IsAllowed(address, opts.AllowedSenders, opts.BlockedSenders))
        {
            _ipBlocking.RecordFailure(remoteIp, "blockedSender");
            _logger.LogWarning("[SmtpFilter] Sender {Address} rejected from {Ip}: {Reason}",
                address, remoteIp,
                MailAddressFilter.GetDenyReason(address, opts.AllowedSenders, opts.BlockedSenders));
            return false;
        }

        // Tenant sender validation: reject senders unknown to the Microsoft 365 tenant
        // so doomed messages fail here with a 550 instead of after 3 Graph retries.
        // The null reverse path ("@", NDRs) is exempt.
        var validation = _senderValidation.CurrentValue;
        if (validation.Enabled && address != "@")
        {
            var result = await _senderDirectory.ValidateAsync(address, cancellationToken);

            if (result == SenderLookupResult.Unknown)
            {
                _ipBlocking.RecordFailure(remoteIp, "unknownSender");
                _logger.LogWarning(
                    "[SmtpFilter] Sender {Address} rejected from {Ip}: not found in Microsoft 365 tenant",
                    address, remoteIp);
                return false;
            }

            if (result == SenderLookupResult.Indeterminate && validation.FailClosed)
            {
                _logger.LogWarning(
                    "[SmtpFilter] Sender {Address} rejected from {Ip}: tenant validation unavailable and FailClosed is enabled",
                    address, remoteIp);
                return false;
            }
        }

        _logger.LogDebug("[SmtpFilter] Sender {Address} accepted from {Ip}", address, remoteIp);
        return true;
    }

    public override Task<bool> CanDeliverToAsync(
        ISessionContext context,
        IMailbox to,
        IMailbox from,
        CancellationToken cancellationToken)
    {
        var opts = _access.CurrentValue;
        var address = $"{to.User}@{to.Host}";
        var remoteIp = IpFilterService.GetRemoteIp(context) ?? "unknown";

        _logger.LogDebug("[SmtpFilter] RCPT TO: <{Address}> from {Ip}", address, remoteIp);

        var allowed = MailAddressFilter.IsAllowed(address, opts.AllowedRecipients, opts.BlockedRecipients);
        if (!allowed)
        {
            _logger.LogWarning("[SmtpFilter] Recipient {Address} rejected: {Reason}",
                address,
                MailAddressFilter.GetDenyReason(address, opts.AllowedRecipients, opts.BlockedRecipients));
        }
        else
        {
            _logger.LogDebug("[SmtpFilter] Recipient {Address} accepted", address);
        }

        return Task.FromResult(allowed);
    }
}
