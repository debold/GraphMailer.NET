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

    private static bool TryMatch(IPAddress address, string cidr)
    {
        try
        {
            // Normalise bare IPs to /32 (IPv4) or /128 (IPv6) so TryParse works
            var entry = cidr.Contains('/') ? cidr : cidr + (cidr.Contains(':') ? "/128" : "/32");
            if (IPNetwork.TryParse(entry, out var network))
                return network.Contains(address);
        }
        catch { /* ignore malformed entries */ }
        return false;
    }
}
