using System.Net;
using System.Text.RegularExpressions;

namespace GraphMailer.ConfigTool.Services;

/// <summary>
/// Pulls the IP addresses out of a log message so the log list can offer them for the IP filter
/// lists directly, instead of the operator copying an address across two pages by hand.
///
/// Candidates are matched loosely and then validated with <see cref="IPAddress.TryParse"/>, which
/// is what keeps the ordinary numeric noise in a log out of the menu: a build number like
/// <c>1.3.3.1067</c> or a platform version like <c>4.18.26070.9</c> looks like an address to a
/// regex but fails to parse, and a wall-clock time like <c>13:45:30</c> is not a valid IPv6
/// address either. Everything that survives that is offered — the operator picks, so being
/// slightly generous costs nothing while a missed address costs the whole feature.
/// </summary>
internal static class LogIpExtractor
{
    /// <summary>
    /// Four dot-separated groups of up to three digits. The bounds check is left to
    /// <see cref="IPAddress.TryParse"/> rather than being spelled out in the pattern, so
    /// <c>999.1.1.1</c> is matched here and rejected there — one place decides what an address is.
    /// </summary>
    private static readonly Regex Ipv4Candidate = new(
        @"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);

    /// <summary>
    /// Two or more colon-separated hex groups, optionally with a zone id. IPv6 has too many valid
    /// shapes (<c>::1</c>, <c>fe80::1%3</c>, full eight-group forms) to pin down precisely without
    /// a pattern nobody can read, so this over-matches and lets TryParse decide.
    ///
    /// The lookarounds are load-bearing rather than decorative: without them the pattern happily
    /// finds <c>::ba</c> inside a qualified name like <c>Foo::Bar</c>, which parses as a perfectly
    /// valid address and would put nonsense in the menu.
    /// </summary>
    private static readonly Regex Ipv6Candidate = new(
        @"(?<![0-9A-Za-z:])(?:[0-9A-Fa-f]{0,4}:){2,}[0-9A-Fa-f]{0,4}(?:%\d+)?(?![0-9A-Za-z])",
        RegexOptions.Compiled);

    /// <summary>
    /// Addresses found in <paramref name="text"/>, in the order they appear and without
    /// duplicates — a line that names the same address twice must not offer it twice.
    /// </summary>
    internal static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Ipv4Candidate.Matches(text))
            Consider(match.Value);

        foreach (Match match in Ipv6Candidate.Matches(text))
            Consider(match.Value);

        return found;

        void Consider(string candidate)
        {
            if (!IsAddress(candidate)) return;
            if (seen.Add(candidate)) found.Add(candidate);
        }
    }

    /// <summary>
    /// True when the candidate is a real address. The round-trip comparison is the important part:
    /// <see cref="IPAddress.TryParse"/> accepts forms the operator never typed and would silently
    /// rewrite — so anything that does not come back as it went in is refused rather than turned
    /// into a filter entry that does not match what the log showed.
    /// </summary>
    private static bool IsAddress(string candidate)
        // "::" parses fine as the unspecified address, but a filter entry for it is meaningless.
        => candidate.Any(Uri.IsHexDigit)
           && IPAddress.TryParse(candidate, out var address)
           && string.Equals(address.ToString(), candidate, StringComparison.OrdinalIgnoreCase);
}
