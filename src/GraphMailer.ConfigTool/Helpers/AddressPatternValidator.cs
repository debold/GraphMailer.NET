using System.Text.RegularExpressions;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Validates SMTP address/domain patterns entered in the Access Control page.
///
/// Supported formats (mirrors <c>MailAddressFilter.MatchesAny</c> in the service):
///   user@domain.com   – exact address match (case-insensitive)
///   @domain.com       – all addresses at exactly that domain (no subdomains)
///
/// Also detects:
///   • Duplicate entries in the same list
///   • Redundant entries (exact address when @domain wildcard is already present)
///   • Cross-list conflicts (allow entry overridden by block, or block making allow ineffective)
/// </summary>
internal static class AddressPatternValidator
{
    private static readonly Regex DomainRegex =
        new(@"^[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)+$",
            RegexOptions.Compiled);

    // Local part: printable ASCII excluding whitespace, @, and * (which would be ambiguous)
    private static readonly Regex LocalPartRegex =
        new(@"^[^\s@*]+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns <c>null</c> when the entry is valid, or an error message string.
    /// </summary>
    /// <param name="pattern">The raw user input.</param>
    /// <param name="sameList">All existing patterns in the same list (excluding the entry being edited).</param>
    /// <param name="oppositeList">All existing patterns in the opposing list (allow ↔ block). Pass <c>null</c> to skip cross-list checks.</param>
    /// <param name="isAllowList"><c>true</c> when validating an allow-list entry; <c>false</c> for a block-list entry.</param>
    internal static string? Validate(
        string pattern,
        IReadOnlyList<string> sameList,
        IReadOnlyList<string>? oppositeList = null,
        bool isAllowList = true)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "Pattern must not be empty.";

        var s = pattern.Trim();

        if (!IsValidPattern(s))
            return $"'{s}' is not a valid address or pattern.\n"
                 + "Accepted formats:\n"
                 + "  user@domain.com  \u2014 exact address\n"
                 + "  @domain.com      \u2014 all addresses at that domain";

        // ── Duplicate in same list ─────────────────────────────────────────
        if (sameList.Any(p => p.Trim().Equals(s, StringComparison.OrdinalIgnoreCase)))
            return $"'{s}' is already in the list.";

        bool isExact = !s.StartsWith('@');
        string domainWildcard = isExact ? "@" + s.Split('@')[1] : s;

        // ── Redundancy within the same list ───────────────────────────────
        // user@domain added when @domain is already in the same list.
        if (isExact && sameList.Any(p => p.Trim().Equals(domainWildcard, StringComparison.OrdinalIgnoreCase)))
            return $"Redundant entry: '{domainWildcard}' is already in this list "
                 + "and covers all addresses at that domain.";

        // ── Cross-list conflict checks ────────────────────────────────────
        var opp = oppositeList ?? [];

        if (opp.Count == 0)
            return null;

        if (isAllowList)
        {
            // Block always wins over allow.
            if (opp.Any(p => p.Trim().Equals(s, StringComparison.OrdinalIgnoreCase)))
                return $"'{s}' is on the block list. Block takes precedence \u2014 "
                     + "this allow entry would have no effect. "
                     + "Remove it from the block list first.";

            // Adding exact address to allow, but its whole domain is blocked.
            if (isExact && opp.Any(p => p.Trim().Equals(domainWildcard, StringComparison.OrdinalIgnoreCase)))
                return $"All addresses at '{domainWildcard}' are blocked. "
                     + "This allow entry would have no effect.";
        }
        else
        {
            // Adding @domain to block: flag allow-list entries that become ineffective.
            // (Adding an exact address to block never makes allow entries ineffective
            //  since a domain wildcard on the allow side is still useful for other addresses.)
            if (!isExact)
            {
                var nowIneffective = opp
                    .Where(p =>
                    {
                        var t = p.Trim();
                        return t.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                               (!t.StartsWith('@') &&
                                t.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                    })
                    .Select(p => p.Trim())
                    .ToList();

                if (nowIneffective.Count > 0)
                {
                    var examples = string.Join(", ", nowIneffective.Take(3).Select(p => $"'{p}'"));
                    var more = nowIneffective.Count > 3 ? $" (+{nowIneffective.Count - 3} more)" : "";
                    return $"Blocking '{s}' would make the following allow entries ineffective "
                         + $"(block takes precedence): {examples}{more}.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when the string is a syntactically valid pattern
    /// that <c>MailAddressFilter</c> can evaluate.
    /// </summary>
    internal static bool IsValidPattern(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;

        // @domain.com — domain wildcard
        if (s.StartsWith('@'))
            return DomainRegex.IsMatch(s[1..]);

        // user@domain.com — exact address
        var parts = s.Split('@');
        return parts.Length == 2
            && LocalPartRegex.IsMatch(parts[0])
            && DomainRegex.IsMatch(parts[1]);
    }
}
