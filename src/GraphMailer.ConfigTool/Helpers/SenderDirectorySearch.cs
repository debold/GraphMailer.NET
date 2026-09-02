namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Live-search predicate for the sender-directory viewer. Extracted from the window so the
/// matching rules are unit-testable without a UI: the grid can hold tens of thousands of rows,
/// and a filter that silently misses one defeats the point of the viewer.
/// </summary>
internal static class SenderDirectorySearch
{
    /// <summary>
    /// True when the row should stay visible. An empty query matches everything.
    ///
    /// The query is matched case-insensitively against the display name, the primary address and
    /// every alias — an operator looking for "reports@" must find the row whether that address is
    /// the primary one or the third proxy address.
    /// </summary>
    internal static bool Matches(
        string? displayName,
        string? primaryAddress,
        IReadOnlyList<string>? addresses,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        var needle = query.Trim();

        if (Contains(displayName, needle)) return true;
        if (Contains(primaryAddress, needle)) return true;

        foreach (var address in addresses ?? [])
            if (Contains(address, needle)) return true;

        return false;
    }

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
