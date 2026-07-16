using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace GraphMailer.Service.Infrastructure.Security;

/// <summary>
/// Validates SMTP user credentials.
///
/// Passwords in config may be stored as:
///  - Plaintext: compared directly (initial setup only)
///  - ENC[...]: decrypted via Data Protection before comparison
///
/// No BCrypt hashing is used – all secrets are protected via the same
/// ENC[...] / Data Protection mechanism as other sensitive config values.
/// </summary>
internal sealed class AuthHandler
{
    private readonly IOptionsMonitor<SmtpAccessOptions> _access;
    private readonly IDataProtector _protector;
    private readonly ILogger<AuthHandler> _logger;

    public AuthHandler(
        IOptionsMonitor<SmtpAccessOptions> access,
        IDataProtectionProvider dpProvider,
        ILogger<AuthHandler> logger)
    {
        _access = access;
        _protector = dpProvider.CreateProtector(DataProtectionExtensions.ConfigPurpose);
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the username exists, is enabled, and the password matches.
    /// Also returns true (with <paramref name="captureRequired"/> = true) when
    /// <c>CaptureNextPassword</c> is set — the caller is responsible for triggering
    /// the capture after returning success to the SMTP client.
    /// </summary>
    public bool ValidateUser(string username, string password, out bool captureRequired)
        => ValidateUser(username, password, out captureRequired, out _);

    /// <summary>
    /// Same as <see cref="ValidateUser(string,string,out bool)"/> but also reports
    /// why validation failed — for log messages only, never for the SMTP response
    /// (which must stay generic so clients can't probe for valid usernames).
    /// </summary>
    public bool ValidateUser(string username, string password, out bool captureRequired, out string? failureReason)
    {
        captureRequired = false;
        failureReason = null;

        var user = _access.CurrentValue.Users
            .FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            failureReason = "unknown user";
            return false;
        }

        if (!user.Enabled)
        {
            failureReason = "user is disabled";
            return false;
        }

        // Capture mode: accept any password from the correct username, trigger capture.
        // Warning, not Information: while the flag is set, ANYONE who knows the username
        // authenticates — the log is the operator's only signal that the window is open.
        if (user.CaptureNextPassword)
        {
            _logger.LogWarning(
                "[AuthHandler] Capture mode active for {Username} – accepting ANY password for this user until the capture completes",
                username);
            captureRequired = true;
            return true;
        }

        var storedPassword = Decrypt(user.Password);
        if (storedPassword is null)
        {
            failureReason = "no usable password configured";
            return false;
        }

        // Constant-time comparison prevents timing side-channel attacks:
        // a regular == comparison can reveal password length and prefix matches
        // through response-time differences measurable by a local attacker.
        var storedBytes = System.Text.Encoding.UTF8.GetBytes(storedPassword);
        var inputBytes = System.Text.Encoding.UTF8.GetBytes(password);
        if (CryptographicOperations.FixedTimeEquals(storedBytes, inputBytes))
            return true;

        failureReason = "wrong password";
        return false;
    }

    /// <summary>
    /// Returns the per-user FromRestrictions list, or null if the user
    /// has none defined (caller should fall back to global AllowedSenders).
    /// </summary>
    public IReadOnlyList<string>? GetFromRestrictions(string username)
    {
        var user = _access.CurrentValue.Users
            .FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

        if (user is null || user.FromRestrictions.Count == 0)
            return null;

        return user.FromRestrictions;
    }

    // -------------------------------------------------------------------------

    private string? Decrypt(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        if (!stored.StartsWith("ENC[", StringComparison.Ordinal))
            return stored; // plaintext – accepted during initial setup

        try
        {
            var payload = stored[4..^1]; // strip ENC[ ... ]
            return _protector.Unprotect(payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AuthHandler] Failed to decrypt password: {Error}", ex.Message);
            return null;
        }
    }
}
