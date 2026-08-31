using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphMailer.Service.Infrastructure.Rules;

/// <summary>
/// Compiles and caches the regular expressions a rule set uses, with a hard guard against
/// catastrophic backtracking.
///
/// Patterns come from <c>graphmailer.json</c>, so they are operator-authored rather than
/// attacker-authored — but the <i>subject</i> they run against is attacker-supplied mail, which
/// is exactly the combination that turns an innocent-looking pattern into a stalled SMTP session.
/// Two layers guard it:
///   1. <see cref="RegexOptions.NonBacktracking"/> where the pattern allows it — linear time,
///      immune to catastrophic backtracking by construction.
///   2. Otherwise (lookarounds, backreferences) the backtracking engine with an explicit
///      match timeout.
///
/// A pattern that cannot be compiled is cached as <see langword="null"/> so the failure is
/// reported once rather than on every message; the calling condition then evaluates to false.
/// </summary>
internal static class RuleRegexCache
{
    /// <summary>
    /// Bounded so a config full of throwaway patterns cannot grow the cache without limit.
    /// Hot reload replays the same patterns, so stale entries are simply reused.
    /// </summary>
    private const int MaxEntries = 256;

    private static readonly ConcurrentDictionary<(string Pattern, bool CaseSensitive, int TimeoutMs), Regex?> Cache = new();

    /// <summary>
    /// The compiled expression, or <see langword="null"/> when the pattern is not valid.
    /// Never throws.
    /// </summary>
    internal static Regex? Get(string pattern, bool caseSensitive, int timeoutMs)
    {
        if (string.IsNullOrEmpty(pattern))
            return null;

        var key = (pattern, caseSensitive, timeoutMs);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var compiled = Compile(pattern, caseSensitive, timeoutMs);

        // Racing writers are harmless — both produce an equivalent Regex.
        if (Cache.Count < MaxEntries)
            Cache[key] = compiled;

        return compiled;
    }

    /// <summary>True when the pattern compiles at all — used by the validator and the ConfigTool.</summary>
    internal static bool IsValid(string pattern, bool caseSensitive = false, int timeoutMs = 100)
        => Get(pattern, caseSensitive, timeoutMs) is not null;

    /// <summary>
    /// Runs a match without ever letting a regex problem escape. A timeout or an invalid
    /// pattern is "no match" — a broken rule must not stop mail.
    /// </summary>
    internal static bool IsMatch(string input, string pattern, bool caseSensitive, int timeoutMs, out string? failure)
    {
        failure = null;
        var regex = Get(pattern, caseSensitive, timeoutMs);
        if (regex is null)
        {
            failure = $"pattern '{pattern}' is not a valid regular expression";
            return false;
        }

        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            failure = $"pattern '{pattern}' timed out after {timeoutMs} ms";
            return false;
        }
    }

    private static Regex? Compile(string pattern, bool caseSensitive, int timeoutMs)
    {
        var options = RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;

        try
        {
            // Linear-time engine first: no backtracking means no ReDoS, whatever the input.
            return new Regex(pattern, options | RegexOptions.NonBacktracking);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            // NonBacktracking rejects lookarounds, backreferences and atomic groups. Those
            // patterns are still legal — they just need the backtracking engine, and there
            // the timeout is the only thing standing between a nested quantifier and a
            // wedged SMTP session.
        }

        try
        {
            return new Regex(pattern, options, TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1, 10_000)));
        }
        catch (ArgumentException)
        {
            return null;   // genuinely malformed pattern
        }
    }

    /// <summary>
    /// Translates a wildcard pattern into an anchored regular expression so wildcards inherit
    /// the same guards. Supports <c>*</c> and <c>?</c>; ';' separates alternatives, and an
    /// entry matching any alternative matches the whole pattern.
    /// </summary>
    internal static string WildcardToRegex(string wildcard)
    {
        var alternatives = wildcard.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (alternatives.Length == 0)
            alternatives = [wildcard];

        var sb = new StringBuilder("^(?:");
        for (var i = 0; i < alternatives.Length; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(Regex.Escape(alternatives[i]).Replace("\\*", ".*").Replace("\\?", "."));
        }
        sb.Append(")$");
        return sb.ToString();
    }
}
