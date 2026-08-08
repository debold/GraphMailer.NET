namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Builds the counter line shown next to the search box of the paged monitoring lists
/// (Metrics → Recent Activity, Logs, Messages).
///
/// Its single job is that a capped list never reads as a complete one: whenever rows were left
/// out — by the page limit, by a filter, or by a bounded scan — the label says so. The pages
/// differ in how they fetch their rows (SQL <c>LIMIT</c>, rolling log files, a folder of JSON
/// files), so only the wording is shared here, not the loading.
/// </summary>
internal static class ListCountLabel
{
    /// <param name="shown">Rows currently in the grid.</param>
    /// <param name="pool">
    /// Size of the set <paramref name="shown"/> was drawn from, or <c>null</c> where that is not
    /// knowable without doing the very work the cap exists to avoid (the log files are not counted
    /// up front). A null pool drops the "of N" part rather than inventing a number.
    /// </param>
    /// <param name="noun">Plural noun for the rows: "events", "entries", "messages".</param>
    /// <param name="filtered">A search term or filter is narrowing the list.</param>
    /// <param name="hasMore">Further rows exist beyond the ones shown.</param>
    /// <param name="note">Optional suffix for a second reason rows were left out (a scan cap).</param>
    internal static string Build(
        int shown, long? pool, string noun, bool filtered, bool hasMore, string? note = null)
    {
        var of = pool.HasValue ? $" of {Num(pool.Value)}" : "";

        var text = filtered
            // "+" reads as "at least": a filtered load that fills the page cannot know what follows
            ? $"{Num(shown)}{(hasMore ? "+" : "")} matches{of}"
            : hasMore
                ? pool.HasValue ? $"newest {Num(shown)}{of}" : $"newest {Num(shown)} {noun}"
                : shown > 0 ? $"{Num(shown)} {noun}" : "";

        if (string.IsNullOrEmpty(note)) return text;

        // An empty list plus a note must not start with the separator
        return text.Length == 0 ? note : $"{text} · {note}";
    }

    private static string Num(long n) => n.ToString("N0");
}
