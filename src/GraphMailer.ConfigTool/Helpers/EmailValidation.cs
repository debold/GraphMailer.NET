using System.Net.Mail;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Single source of truth for validating a recipient email address in the ConfigTool.
/// Accepts a bare address (e.g. <c>jane.doe@contoso.com</c>) and rejects empty input
/// and display-name forms (<c>Name &lt;a@b.com&gt;</c>).
/// </summary>
internal static class EmailValidation
{
    internal static bool IsValidRecipient(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var trimmed = address.Trim();
        return MailAddress.TryCreate(trimmed, out var parsed)
            && parsed is not null
            && string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Contains('.');   // require a real domain (reject "user@host")
    }
}
