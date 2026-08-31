using MimeKit;

namespace GraphMailer.Service.Infrastructure.Rules;

/// <summary>Whether a message is cryptographically protected, and how.</summary>
internal enum MimeProtectionKind
{
    None,
    Signed,
    Encrypted,
}

/// <summary>
/// Classifies S/MIME and OpenPGP protection.
///
/// Deliberately matched on the content-type strings rather than on MimeKit's
/// <c>MimeKit.Cryptography</c> subclasses: those are only materialised when the cryptography
/// assembly has registered its parser hooks, and a relay must not depend on that to notice
/// that it is about to break a signature.
/// </summary>
internal static class MimeProtection
{
    internal static MimeProtectionKind Classify(MimeMessage message)
    {
        if (message.Body is null)
            return MimeProtectionKind.None;

        // multipart/signed and multipart/encrypted are unambiguous at the top level.
        if (message.Body.ContentType.IsMimeType("multipart", "signed"))
            return MimeProtectionKind.Signed;
        if (message.Body.ContentType.IsMimeType("multipart", "encrypted"))
            return MimeProtectionKind.Encrypted;

        // application/pkcs7-mime carries both cases; smime-type says which.
        if (IsPkcs7Mime(message.Body.ContentType))
        {
            var smimeType = message.Body.ContentType.Parameters["smime-type"];
            return smimeType is not null
                && smimeType.Contains("signed", StringComparison.OrdinalIgnoreCase)
                    ? MimeProtectionKind.Signed
                    : MimeProtectionKind.Encrypted;
        }

        return Walk(message.Body);
    }

    /// <summary>
    /// Looks for protection nested below the top level — a signed part inside a
    /// multipart/mixed, the way many mailers wrap an attachment around a signed body.
    /// </summary>
    private static MimeProtectionKind Walk(MimeEntity entity)
    {
        switch (entity)
        {
            case Multipart multipart:
                if (multipart.ContentType.IsMimeType("multipart", "encrypted"))
                    return MimeProtectionKind.Encrypted;
                if (multipart.ContentType.IsMimeType("multipart", "signed"))
                    return MimeProtectionKind.Signed;

                // Encryption outranks signing: an encrypted part cannot be edited at all,
                // whereas a signed one merely loses its signature.
                var result = MimeProtectionKind.None;
                foreach (var child in multipart)
                {
                    var childKind = Walk(child);
                    if (childKind == MimeProtectionKind.Encrypted) return MimeProtectionKind.Encrypted;
                    if (childKind == MimeProtectionKind.Signed) result = MimeProtectionKind.Signed;
                }
                return result;

            case MimePart part:
                var type = part.ContentType;
                if (type.IsMimeType("application", "pgp-encrypted"))
                    return MimeProtectionKind.Encrypted;
                if (type.IsMimeType("application", "pgp-signature")
                    || type.IsMimeType("application", "pkcs7-signature")
                    || type.IsMimeType("application", "x-pkcs7-signature"))
                    return MimeProtectionKind.Signed;
                if (IsPkcs7Mime(type))
                {
                    var smimeType = type.Parameters["smime-type"];
                    return smimeType is not null
                        && smimeType.Contains("signed", StringComparison.OrdinalIgnoreCase)
                            ? MimeProtectionKind.Signed
                            : MimeProtectionKind.Encrypted;
                }
                return MimeProtectionKind.None;

            default:
                return MimeProtectionKind.None;
        }
    }

    private static bool IsPkcs7Mime(ContentType type)
        => type.IsMimeType("application", "pkcs7-mime")
        || type.IsMimeType("application", "x-pkcs7-mime");

    /// <summary>Wording for the log line and the skip reason recorded on the action.</summary>
    internal static string Describe(MimeProtectionKind kind) => kind switch
    {
        MimeProtectionKind.Signed => "the message is signed",
        MimeProtectionKind.Encrypted => "the message is encrypted",
        _ => "the message is not protected",
    };
}
