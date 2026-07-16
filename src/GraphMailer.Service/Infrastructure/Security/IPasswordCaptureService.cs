namespace GraphMailer.Service.Infrastructure.Security;

/// <summary>
/// Captures a plaintext password supplied during SMTP AUTH, encrypts it, and persists
/// it to <c>graphmailer.json</c> for the matching user.
/// Also clears the <c>CaptureNextPassword</c> flag so only one password is ever captured.
/// </summary>
internal interface IPasswordCaptureService
{
    /// <summary>
    /// Returns true when <paramref name="username"/> has <c>CaptureNextPassword = true</c>.
    /// </summary>
    bool IsCaptureEnabled(string username);

    /// <summary>
    /// Encrypts <paramref name="password"/> and writes it to <c>graphmailer.json</c> for
    /// <paramref name="username"/>. Clears <c>CaptureNextPassword</c> atomically.
    /// Fire-and-forget safe: any I/O error is logged but never propagated to the caller.
    /// </summary>
    Task CaptureAsync(string username, string password);
}
