using System.Net;
using SmtpServer;

namespace GraphMailer.Service.Infrastructure.Security;

/// <summary>
/// Stateless utility for CIDR-based IP allow/deny decisions.
/// Uses System.Net.IPNetwork (.NET 8 built-in) – no external package needed.
/// </summary>
internal static class IpFilterService
{
    /// <summary>
    /// Properties key used by SmtpServer to store the remote endpoint.
    /// Defined in SmtpServer.Net.EndpointListener.RemoteEndPointKey.
    /// </summary>
    internal const string RemoteEndPointKey = "EndpointListener:RemoteEndPoint";

    /// <summary>
    /// Reads the remote IP from a SmtpServer session context.
    /// Returns null if the key is not present (e.g. in unit tests).
    /// </summary>
    public static string? GetRemoteIp(ISessionContext context)
    {
        if (context.Properties.TryGetValue(RemoteEndPointKey, out var raw) && raw is IPEndPoint ep)
            return ep.Address.ToString();
        return null;
    }

    /// <summary>
    /// Determines whether the given IP address is allowed based on
    /// whitelist and blacklist CIDR rules.
    ///
    /// Logic (mirrors Node.js ipFilter.js):
    ///  1. Blacklist: deny if matched
    ///  2. Whitelist: deny if non-empty and not matched
    ///  3. Otherwise: allow
    /// </summary>
    public static bool IsAllowed(string ip, IReadOnlyList<string> whitelist, IReadOnlyList<string> blacklist)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return false;

        if (blacklist.Count > 0 && MatchesAny(address, blacklist))
            return false;

        if (whitelist.Count > 0 && !MatchesAny(address, whitelist))
            return false;

        return true;
    }

    /// <summary>
    /// Explains why <see cref="IsAllowed"/> returned false for the IP —
    /// used for log messages so operators see which rule caused a rejection.
    /// </summary>
    public static string GetDenyReason(string ip, IReadOnlyList<string> whitelist, IReadOnlyList<string> blacklist)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return "remote IP could not be parsed";

        if (FindMatch(address, blacklist) is { } blockedBy)
            return $"matches IP blacklist entry '{blockedBy}'";

        if (whitelist.Count > 0 && !MatchesAny(address, whitelist))
            return "not covered by any IP whitelist entry";

        return "no matching rule";   // unreachable when IsAllowed returned false
    }

    /// <summary>
    /// True when the IP matches a blacklist entry — distinguishes a blacklist hit from
    /// missing whitelist coverage for the rejection statistics.
    /// </summary>
    public static bool IsBlacklisted(string ip, IReadOnlyList<string> blacklist)
        => IPAddress.TryParse(ip, out var address) && blacklist.Count > 0 && MatchesAny(address, blacklist);

    /// <summary>
    /// True when the IP falls inside any of the given CIDR ranges or bare addresses.
    /// Exposed so other policies (the malware-scan bypass) match ranges exactly the way the
    /// IP filter does, rather than growing a second, subtly different CIDR implementation.
    /// </summary>
    public static bool IsInAnyRange(string ip, IReadOnlyList<string> cidrs)
        => cidrs.Count > 0 && IPAddress.TryParse(ip, out var address) && MatchesAny(address, cidrs);

    private static bool MatchesAny(IPAddress address, IReadOnlyList<string> cidrs)
        => FindMatch(address, cidrs) is not null;

    /// <summary>Returns the first CIDR/IP entry the address matches, or null.</summary>
    private static string? FindMatch(IPAddress address, IReadOnlyList<string> cidrs)
    {
        foreach (var cidr in cidrs)
        {
            if (TryMatch(address, cidr))
                return cidr;
        }
        return null;
    }

    /// <summary>
    /// The entries of <paramref name="cidrs"/> that are not a usable IP or CIDR range.
    ///
    /// Such an entry matches nothing, which is silent by nature: on a blacklist it means a rule
    /// the operator believes is active does nothing at all, and neither the rejection log nor
    /// the rule count reveals it. The ConfigTool validates what is typed into it, but a
    /// hand-edited or migrated <c>graphmailer.json</c> never passes through that check — so the
    /// service reports them itself at startup.
    /// </summary>
    public static IReadOnlyList<string> FindInvalidEntries(IReadOnlyList<string> cidrs)
        => [.. cidrs.Where(entry => !TryParseEntry(entry, out _))];

    /// <summary>
    /// Parses one list entry. Bare IPs are normalised to /32 (IPv4) or /128 (IPv6) so a single
    /// address and a range go through the same code path — and so "valid" means the same thing
    /// to <see cref="TryMatch"/> and to <see cref="FindInvalidEntries"/>.
    /// </summary>
    private static bool TryParseEntry(string cidr, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        try
        {
            var trimmed = cidr.Trim();
            var entry = trimmed.Contains('/') ? trimmed : trimmed + (trimmed.Contains(':') ? "/128" : "/32");
            return IPNetwork.TryParse(entry, out network);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMatch(IPAddress address, string cidr)
        => TryParseEntry(cidr, out var network) && network.Contains(address);
}
