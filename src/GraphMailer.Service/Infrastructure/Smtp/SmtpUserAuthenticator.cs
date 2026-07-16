using GraphMailer.Service.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.Authentication;

namespace GraphMailer.Service.Infrastructure.Smtp;

/// <summary>
/// SmtpServer IUserAuthenticator implementation.
/// Delegates credential validation to AuthHandler.
/// Records failures in IpBlockingService.
/// Sets "Auth:Failed" in session properties when auth is attempted but credentials are wrong,
/// so SmtpMailboxFilter can reject mail even when AuthRequired = false.
/// When CaptureNextPassword is active for a user, the supplied password is captured and
/// persisted to config after returning success to the SMTP client.
/// </summary>
internal sealed class SmtpUserAuthenticator : IUserAuthenticator
{
    private readonly AuthHandler _authHandler;
    private readonly IpBlockingService _ipBlocking;
    private readonly IPasswordCaptureService _capture;
    private readonly ILogger<SmtpUserAuthenticator> _logger;

    public SmtpUserAuthenticator(
        AuthHandler authHandler,
        IpBlockingService ipBlocking,
        IPasswordCaptureService capture,
        ILogger<SmtpUserAuthenticator> logger)
    {
        _authHandler = authHandler;
        _ipBlocking = ipBlocking;
        _capture = capture;
        _logger = logger;
    }

    public Task<bool> AuthenticateAsync(
        ISessionContext context,
        string login,
        string password,
        CancellationToken cancellationToken)
    {
        var remoteIp = IpFilterService.GetRemoteIp(context) ?? "unknown";

        _logger.LogDebug("[SmtpAuth] AUTH attempt for {Username} from {Ip}", login, remoteIp);

        if (_ipBlocking.IsBlocked(remoteIp, out var blockedUntil))
        {
            _logger.LogWarning(
                "[SmtpAuth] Auth attempt rejected – IP {Ip} is blocked after repeated failures (until {Expires:HH:mm:ss} UTC)",
                remoteIp, blockedUntil);
            return Task.FromResult(false);
        }

        var valid = _authHandler.ValidateUser(login, password, out bool captureRequired, out var failureReason);

        if (!valid)
        {
            // Store the username so the MAIL FROM rejection can name it.
            // The SMTP response stays generic — the reason is for the log only.
            context.Properties["Auth:Failed"] = login;
            _ipBlocking.RecordFailure(remoteIp, "authFailure");
            _logger.LogWarning("[SmtpAuth] Failed auth for {Username} from {Ip}: {Reason}",
                login, remoteIp, failureReason);
        }
        else
        {
            // A previous failed attempt on this connection must not outlive a successful
            // re-authentication — otherwise SmtpMailboxFilter keeps rejecting MAIL FROM
            // for the rest of the session even though the client is now authenticated.
            context.Properties.Remove("Auth:Failed");

            _logger.LogInformation("[SmtpAuth] Authenticated {Username} from {Ip}", login, remoteIp);

            if (captureRequired)
            {
                // Fire-and-forget: the SMTP response is already determined (success).
                // CaptureAsync serialises writes internally and logs any error.
                _ = _capture.CaptureAsync(login, password);
            }
        }

        return Task.FromResult(valid);
    }
}
