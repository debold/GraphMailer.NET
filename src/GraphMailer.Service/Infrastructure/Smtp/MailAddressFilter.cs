namespace GraphMailer.Service.Infrastructure.Smtp;

/// <summary>
/// Stateless utility for matching email addresses against allow/block lists.
/// Supports exact-address matching and @domain wildcard entries.
///
/// Mirrors IpFilterService in design: static, side-effect-free, directly
/// unit-testable via InternalsVisibleTo.
///
/// Note on address format: this class operates on pre-parsed addresses of the
/// form "localpart@domain" produced by SmtpServer after RFC-5321 parsing.
/// SmtpServer rejects syntactically invalid MAIL FROM / RCPT TO addresses at
/// the protocol level before our filter code is ever invoked.
///
/// Special case – null reverse path (MAIL FROM:&lt;&gt;):
///   SmtpServer passes User="" and Host="" → address becomes "@".
///   This is valid per RFC-5321 (used for NDRs / delivery status notifications).
///   With empty allow/block lists the null-path sender passes through, which is
///   the correct behaviour for an outbound relay.
/// </summary>
internal static class MailAddressFilter
{
    /// <summary>
    /// Determines whether an email address is permitted by the given lists.
    ///
    /// Logic (mirrors IpFilterService):
    ///  1. If block list is non-empty and the address matches → deny
    ///  2. If allow list is non-empty and the address does not match → deny
    ///  3. Otherwise → allow
    /// </summary>
    internal static bool IsAllowed(
        string address,
        IReadOnlyList<string> allowList,
        IReadOnlyList<string> blockList)
    {
        if (blockList.Count > 0 && MatchesAny(address, blockList))
            return false;

        if (allowList.Count > 0 && !MatchesAny(address, allowList))
            return false;

        return true;
    }

    /// <summary>
    /// Returns true when the address matches at least one entry in the list.
    ///
    /// List entries starting with '@' are treated as domain wildcards that
    /// match all addresses at exactly that domain (subdomains are NOT matched):
    ///   @example.com  matches     user@example.com
    ///   @example.com  does NOT match  user@sub.example.com
    ///
    /// All other entries are compared as full addresses (case-insensitive).
    /// </summary>
    internal static bool MatchesAny(string address, IReadOnlyList<string> list)
        => FindMatch(address, list) is not null;

    /// <summary>Returns the first list entry the address matches, or null.</summary>
    internal static string? FindMatch(string address, IReadOnlyList<string> list)
    {
        foreach (var entry in list)
        {
            if (entry.StartsWith('@'))
            {
                // Domain wildcard: the address must end with exactly "@domain",
                // which naturally prevents both subdomain and suffix spoofing.
                if (address.EndsWith(entry, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            else if (address.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Explains why <see cref="IsAllowed"/> returned false for the address —
    /// used for log messages so operators see which rule caused a rejection.
    /// </summary>
    internal static string GetDenyReason(
        string address,
        IReadOnlyList<string> allowList,
        IReadOnlyList<string> blockList)
    {
        if (FindMatch(address, blockList) is { } blockedBy)
            return $"matches block list entry '{blockedBy}'";

        if (allowList.Count > 0 && !MatchesAny(address, allowList))
            return "not covered by any allow list entry";

        return "no matching rule";   // unreachable when IsAllowed returned false
    }
}
