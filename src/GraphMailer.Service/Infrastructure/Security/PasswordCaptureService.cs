using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Infrastructure.Security;

/// <summary>
/// Reads the per-user <c>CaptureNextPassword</c> flag from the current options snapshot,
/// and — when triggered — writes the encrypted password back to <c>graphmailer.json</c>
/// via <see cref="ConfigService"/>.
///
/// Design notes:
/// - Uses <see cref="IOptionsMonitor{T}"/> so the flag is always current without a restart.
/// - <see cref="CaptureAsync"/> is intentionally fire-and-forget from the SMTP pipeline:
///   the AUTH response is already sent before this writes to disk; any I/O error is logged
///   but never propagated to the caller.
/// - Writing is serialised through a <see cref="SemaphoreSlim"/> to avoid concurrent edits
///   when two captures arrive at nearly the same time (unlikely but possible in theory).
/// </summary>
internal sealed class PasswordCaptureService : IPasswordCaptureService
{
    private readonly IOptionsMonitor<SmtpAccessOptions> _access;
    private readonly IDataProtector _protector;
    private readonly ILogger<PasswordCaptureService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PasswordCaptureService(
        IOptionsMonitor<SmtpAccessOptions> access,
        IDataProtectionProvider dpProvider,
        ILogger<PasswordCaptureService> logger)
    {
        _access = access;
        _protector = dpProvider.CreateProtector(DataProtectionExtensions.ConfigPurpose);
        _logger = logger;
    }

    public bool IsCaptureEnabled(string username)
        => _access.CurrentValue.Users
            .Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
                   && u.CaptureNextPassword);

    public async Task CaptureAsync(string username, string password)
    {
        await _writeLock.WaitAsync();
        try
        {
            _logger.LogInformation("[PasswordCapture] Capturing password for user {Username}", username);

            var svc = new ConfigService(AppPaths.ConfigFilePath, _protector);
            var doc = svc.Load();

            var user = doc.Access.Users
                .FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user is null)
            {
                _logger.LogWarning("[PasswordCapture] User {Username} not found in config – skipping capture", username);
                return;
            }

            if (!user.CaptureNextPassword)
            {
                // Race: another thread already captured and cleared the flag
                _logger.LogDebug("[PasswordCapture] CaptureNextPassword already cleared for {Username}", username);
                return;
            }

            user.Password = password;          // ConfigService.Save() encrypts it as ENC[...]
            user.CaptureNextPassword = false;

            svc.Save(doc);

            _logger.LogInformation("[PasswordCapture] Password captured and persisted for user {Username}", username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PasswordCapture] Failed to persist captured password for {Username}: {Error}",
                username, ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
