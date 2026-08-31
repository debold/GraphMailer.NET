using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Infrastructure.Smtp;
using GraphMailer.Service.Services;
using MimeKit;

namespace GraphMailer.Service.Infrastructure.Rules;

/// <summary>A rule that cannot work as written, and why.</summary>
/// <param name="RuleName">The rule the problem belongs to.</param>
/// <param name="Detail">Operator-readable explanation, used in the log and the ConfigTool.</param>
/// <param name="IsError">
/// True when the rule (or part of it) can never match or apply; false for a warning about
/// something that works but will not survive delivery.
/// </param>
internal readonly record struct RuleProblem(string RuleName, string Detail, bool IsError);

/// <summary>
/// Decides whether a rule matches a message. Static and side-effect-free, mirroring
/// <see cref="IpFilterService"/> and <see cref="MailAddressFilter"/> — the same family, and
/// directly unit-testable through InternalsVisibleTo.
///
/// Nothing here mutates the message; that is <see cref="MessageRuleActions"/>' job.
/// </summary>
internal static class MessageRuleEvaluator
{
    /// <summary>
    /// Whether the rule applies to the message. A disabled rule never matches; a rule without
    /// conditions always matches (a deliberate "apply to all").
    /// </summary>
    internal static bool IsMatch(MessageRule rule, MessageRuleContext ctx, int regexTimeoutMs)
    {
        if (!rule.Enabled)
            return false;

        if (rule.Conditions.Count == 0)
            return true;

        return rule.Match == ConditionMatch.Any
            ? rule.Conditions.Any(c => Matches(c, ctx, regexTimeoutMs))
            : rule.Conditions.All(c => Matches(c, ctx, regexTimeoutMs));
    }

    /// <summary>
    /// Evaluates one condition, including <see cref="RuleCondition.Negate"/>.
    ///
    /// Multi-valued fields match existentially and negation is applied afterwards, so
    /// "EnvelopeRecipient DomainIs @x.com" with Negate reads "no recipient at x.com".
    /// </summary>
    internal static bool Matches(RuleCondition condition, MessageRuleContext ctx, int regexTimeoutMs)
        => MatchesRaw(condition, ctx, regexTimeoutMs) ^ condition.Negate;

    private static bool MatchesRaw(RuleCondition condition, MessageRuleContext ctx, int regexTimeoutMs)
    {
        // A pair the schema does not define can never match. It is reported by FindProblems at
        // startup and in the ConfigTool, so this is a safety net, not the primary notification.
        if (!RuleConditionSchema.IsSupported(condition.Field, condition.Operator))
            return false;

        return RuleConditionSchema.TypeOf(condition.Field) switch
        {
            RuleFieldType.Bool => condition.Operator == RuleConditionOperator.IsTrue
                                  && BoolValue(condition.Field, ctx),
            RuleFieldType.Number => MatchesNumber(condition, ctx),
            _ => MatchesText(condition, ctx, regexTimeoutMs),
        };
    }

    private static bool MatchesNumber(RuleCondition condition, MessageRuleContext ctx)
    {
        if (!long.TryParse(condition.Value.Trim(), out var expected))
            return false;

        foreach (var actual in NumberValues(condition.Field, ctx))
        {
            var hit = condition.Operator switch
            {
                RuleConditionOperator.Equals => actual == expected,
                RuleConditionOperator.GreaterThan => actual > expected,
                RuleConditionOperator.LessThan => actual < expected,
                _ => false,
            };
            if (hit) return true;
        }
        return false;
    }

    private static bool MatchesText(RuleCondition condition, MessageRuleContext ctx, int regexTimeoutMs)
    {
        var values = TextValues(condition.Field, condition, ctx);

        // Exists / IsEmpty ask about the field itself, not about a comparison value.
        if (condition.Operator == RuleConditionOperator.Exists)
            return values.Any(v => !string.IsNullOrWhiteSpace(v));
        if (condition.Operator == RuleConditionOperator.IsEmpty)
            return values.Count == 0 || values.All(string.IsNullOrWhiteSpace);

        if (string.IsNullOrEmpty(condition.Value))
            return false;

        var comparison = condition.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        foreach (var value in values)
        {
            if (MatchesOne(value, condition, comparison, regexTimeoutMs))
                return true;
        }
        return false;
    }

    private static bool MatchesOne(
        string value, RuleCondition condition, StringComparison comparison, int regexTimeoutMs)
        => condition.Operator switch
        {
            RuleConditionOperator.Equals => value.Equals(condition.Value, comparison),
            RuleConditionOperator.Contains => value.Contains(condition.Value, comparison),
            RuleConditionOperator.StartsWith => value.StartsWith(condition.Value, comparison),
            RuleConditionOperator.EndsWith => value.EndsWith(condition.Value, comparison),

            // Wildcards go through the regex cache so they inherit its timeout guard.
            RuleConditionOperator.Matches => RuleRegexCache.IsMatch(
                value, RuleRegexCache.WildcardToRegex(condition.Value), condition.CaseSensitive, regexTimeoutMs, out _),

            RuleConditionOperator.RegexMatches => RuleRegexCache.IsMatch(
                value, condition.Value, condition.CaseSensitive, regexTimeoutMs, out _),

            // Same semantics as the sender/recipient allow lists: exact domain, no subdomains.
            RuleConditionOperator.DomainIs => MailAddressFilter.MatchesAny(value, SplitList(condition.Value)),

            RuleConditionOperator.InIpRange => IpFilterService.IsInAnyRange(value, SplitList(condition.Value)),

            _ => false,
        };

    /// <summary>
    /// Why <see cref="IsMatch"/> returned false, in the operator's terms.
    ///
    /// "The rule does nothing" is the commonest question about a rule set, and the answer is
    /// almost always one specific condition. Naming it turns a guess into a fact. Only called
    /// when an explanation was asked for — it evaluates the conditions a second time.
    /// </summary>
    internal static string ExplainMismatch(MessageRule rule, MessageRuleContext ctx, int regexTimeoutMs)
    {
        if (!rule.Enabled)
            return "the rule is switched off";

        if (rule.Conditions.Count == 0)
            return "the rule has no conditions, so it should have matched";

        if (rule.Match == ConditionMatch.Any)
            return "none of the conditions matched: "
                 + string.Join("; ", rule.Conditions.Select(Describe));

        foreach (var condition in rule.Conditions)
        {
            if (!Matches(condition, ctx, regexTimeoutMs))
                return $"this condition did not match: {Describe(condition)}";
        }

        return "the conditions matched — the rule should have applied";
    }

    /// <summary>
    /// What the message actually holds for the condition's field, so a mismatch can be compared
    /// against the rule rather than guessed at.
    /// </summary>
    internal static string DescribeActualValue(RuleCondition condition, MessageRuleContext ctx)
    {
        var type = RuleConditionSchema.TypeOf(condition.Field);

        if (type == RuleFieldType.Bool)
            return BoolValue(condition.Field, ctx) ? "yes" : "no";

        if (type == RuleFieldType.Number)
        {
            var numbers = NumberValues(condition.Field, ctx);
            return numbers.Count == 0 ? "(none)" : string.Join(", ", numbers);
        }

        var values = TextValues(condition.Field, condition, ctx)
            .Where(v => !string.IsNullOrEmpty(v))
            .Take(10)
            .ToList();

        return values.Count == 0 ? "(none)" : string.Join(", ", values);
    }

    // ---------------------------------------------------------------- value extraction

    private static IReadOnlyList<string> TextValues(
        RuleConditionField field, RuleCondition condition, MessageRuleContext ctx) => field switch
    {
        RuleConditionField.EnvelopeFrom => [ctx.EnvelopeFrom],
        RuleConditionField.EnvelopeRecipient => ctx.EnvelopeRecipients,
        RuleConditionField.ClientIp => [ctx.Session.ClientIp],
        RuleConditionField.AuthUser => [ctx.Session.AuthUser],

        RuleConditionField.HeaderFrom => Addresses(ctx.Message.From),
        RuleConditionField.HeaderTo => Addresses(ctx.Message.To),
        RuleConditionField.HeaderCc => Addresses(ctx.Message.Cc),
        RuleConditionField.HeaderReplyTo => Addresses(ctx.Message.ReplyTo),

        RuleConditionField.Subject => [ctx.Message.Subject ?? string.Empty],
        RuleConditionField.BodyText => [ctx.BodyText],
        RuleConditionField.BodyHtml => [ctx.BodyHtml],

        RuleConditionField.Header => HeaderValues(ctx, condition.HeaderName),

        RuleConditionField.AttachmentName => AttachmentNames(ctx),
        RuleConditionField.AttachmentExtension => AttachmentExtensions(ctx),

        RuleConditionField.Importance => [ImportanceToken(ctx.Message)],

        _ => [],
    };

    private static IReadOnlyList<long> NumberValues(RuleConditionField field, MessageRuleContext ctx) => field switch
    {
        RuleConditionField.RecipientCount => [ctx.EnvelopeRecipients.Count],
        RuleConditionField.MessageSizeBytes => [ctx.MessageSizeBytes],
        RuleConditionField.ListenerPort => [ctx.Session.ListenerPort],
        RuleConditionField.AttachmentCount => [ctx.Split.Attachments.Count],
        RuleConditionField.AttachmentSizeBytes =>
            [.. ctx.Split.Attachments.Select(a => MimeMessageSplitter.MeasureEncodedSize(a.Entity))],
        _ => [],
    };

    private static bool BoolValue(RuleConditionField field, MessageRuleContext ctx) => field switch
    {
        RuleConditionField.Authenticated => ctx.Session.Authenticated,
        RuleConditionField.Tls => ctx.Session.Tls,
        RuleConditionField.IsSigned => ctx.Protection == MimeProtectionKind.Signed,
        RuleConditionField.IsEncrypted => ctx.Protection == MimeProtectionKind.Encrypted,
        _ => false,
    };

    private static IReadOnlyList<string> Addresses(InternetAddressList list)
        => [.. list.Mailboxes.Select(m => m.Address)];

    private static IReadOnlyList<string> HeaderValues(MessageRuleContext ctx, string? headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
            return [];

        return [.. ctx.Message.Headers
            .Where(h => h.Field.Equals(headerName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value ?? string.Empty)];
    }

    /// <summary>
    /// Attachment names as the delivery path sees them — classified by
    /// <see cref="MimeMessageSplitter"/>, the same source <c>GraphApiClient.BuildMessage</c> and
    /// the reception statistics use. MimeKit's own <c>Attachments</c> disagrees on named text
    /// parts and on malformed Content-Disposition values.
    /// </summary>
    internal static IReadOnlyList<string> AttachmentNames(MessageRuleContext ctx)
        => [.. ctx.Split.Attachments
            .Select(a => FileNameOf(a.Entity))
            .Where(n => !string.IsNullOrEmpty(n))];

    /// <summary>
    /// Attachment extensions in <b>both</b> spellings — <c>.xml</c> and <c>xml</c>.
    ///
    /// The <c>RemoveAttachments</c> action already accepts an extension written either way, so an
    /// operator who writes "xml" there and sees it work writes "xml" in a condition too. Matching
    /// only the dotted form made the condition silently never fire, which is the worst possible
    /// failure for a rule: it looks configured and does nothing. Offering both spellings as values
    /// keeps every operator working — Equals, Contains, a wildcard or a regular expression alike —
    /// without touching what the operator wrote.
    /// </summary>
    private static IReadOnlyList<string> AttachmentExtensions(MessageRuleContext ctx)
    {
        var values = new List<string>();

        foreach (var name in AttachmentNames(ctx))
        {
            var extension = Path.GetExtension(name);
            if (extension.Length <= 1) continue;   // "" or a trailing dot carries no extension

            values.Add(extension);          // ".xml"
            values.Add(extension[1..]);     // "xml"
        }

        return values;
    }

    internal static string FileNameOf(MimeEntity entity)
        => entity.ContentDisposition?.FileName
           ?? (entity as MimePart)?.FileName
           ?? entity.ContentType?.Name
           ?? string.Empty;

    /// <summary>
    /// The importance token, resolved exactly the way <c>GraphApiClient.MapImportance</c>
    /// resolves it — Importance header first, then X-Priority, then RFC 2156 Priority — so a
    /// condition sees the value that will actually be delivered.
    /// </summary>
    internal static string ImportanceToken(MimeMessage mime) => mime.Importance switch
    {
        MessageImportance.High => "High",
        MessageImportance.Low => "Low",
        _ => mime.XPriority switch
        {
            XMessagePriority.Highest or XMessagePriority.High => "High",
            XMessagePriority.Lowest or XMessagePriority.Low => "Low",
            _ => mime.Priority switch
            {
                MessagePriority.Urgent => "High",
                MessagePriority.NonUrgent => "Low",
                _ => "Normal",
            },
        },
    };

    /// <summary>';'-separated list entry, trimmed and without empties.</summary>
    internal static IReadOnlyList<string> SplitList(string value)
        => [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // ---------------------------------------------------------------- validation

    /// <summary>
    /// Everything wrong with a rule set: patterns that cannot compile, field/operator pairs the
    /// schema does not define, missing action parameters, headers that will not survive delivery.
    ///
    /// Used by the startup validator <i>and</i> the ConfigTool, so both report the same thing.
    /// A hand-edited <c>graphmailer.json</c> never passes through the UI, which is why the
    /// service has to check for itself.
    /// </summary>
    internal static IReadOnlyList<RuleProblem> FindProblems(MessageRulesOptions options)
    {
        var problems = new List<RuleProblem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in options.Rules)
        {
            var name = string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name;

            if (string.IsNullOrWhiteSpace(rule.Name))
                problems.Add(new RuleProblem(name, "the rule has no name — it cannot be identified in the log", false));
            else if (!seenNames.Add(rule.Name))
                problems.Add(new RuleProblem(name, "another rule already uses this name — log lines will be ambiguous", false));

            if (rule.Actions.Count == 0)
                problems.Add(new RuleProblem(name, "the rule has no actions and therefore does nothing", true));

            foreach (var condition in rule.Conditions)
                CheckCondition(problems, name, condition, options.RegexTimeoutMs);

            foreach (var action in rule.Actions)
                CheckAction(problems, name, action);
        }

        return problems;
    }

    private static void CheckCondition(
        List<RuleProblem> problems, string ruleName, RuleCondition condition, int regexTimeoutMs)
    {
        if (!RuleConditionSchema.IsSupported(condition.Field, condition.Operator))
        {
            problems.Add(new RuleProblem(ruleName,
                $"condition '{condition.Field} {condition.Operator}' is not a valid combination and can never match", true));
            return;
        }

        if (condition.Field == RuleConditionField.Header && string.IsNullOrWhiteSpace(condition.HeaderName))
            problems.Add(new RuleProblem(ruleName, "a Header condition needs a header name", true));

        if (RuleConditionSchema.RequiresValue(condition.Operator) && string.IsNullOrWhiteSpace(condition.Value))
        {
            problems.Add(new RuleProblem(ruleName,
                $"condition '{Describe(condition)}' has no value and can never match", true));
            return;
        }

        switch (condition.Operator)
        {
            case RuleConditionOperator.RegexMatches
                when !RuleRegexCache.IsValid(condition.Value, condition.CaseSensitive, regexTimeoutMs):
                problems.Add(new RuleProblem(ruleName,
                    $"'{condition.Value}' is not a valid regular expression", true));
                break;

            case RuleConditionOperator.InIpRange
                when IpFilterService.FindInvalidEntries(SplitList(condition.Value)) is { Count: > 0 } invalid:
                problems.Add(new RuleProblem(ruleName,
                    $"not a valid IP or CIDR range: {string.Join(", ", invalid)}", true));
                break;

            case RuleConditionOperator.Equals or RuleConditionOperator.GreaterThan or RuleConditionOperator.LessThan
                when RuleConditionSchema.TypeOf(condition.Field) == RuleFieldType.Number
                     && !long.TryParse(condition.Value.Trim(), out _):
                problems.Add(new RuleProblem(ruleName,
                    $"'{condition.Value}' is not a number — the condition can never match", true));
                break;
        }
    }

    private static void CheckAction(List<RuleProblem> problems, string ruleName, RuleAction action)
    {
        var required = RuleActionSchema.Required(action.Type);

        // Whitespace-preserving actions accept a value that is only whitespace — see
        // RuleActionSchema.IsValueMissing.
        if ((required & RuleActionParam.Value) != 0 && RuleActionSchema.IsValueMissing(action.Type, action.Value))
            problems.Add(new RuleProblem(ruleName, $"action '{action.Type}' needs a value", true));

        if ((required & RuleActionParam.HeaderName) != 0 && string.IsNullOrWhiteSpace(action.HeaderName))
            problems.Add(new RuleProblem(ruleName, $"action '{action.Type}' needs a header name", true));

        if ((required & RuleActionParam.Match) != 0 && string.IsNullOrWhiteSpace(action.Match))
            problems.Add(new RuleProblem(ruleName, $"action '{action.Type}' needs an address to match", true));

        if ((required & RuleActionParam.Recipient) != 0 && action.Recipient is null)
            problems.Add(new RuleProblem(ruleName, $"action '{action.Type}' needs a recipient list (To, Cc or Bcc)", true));

        if ((required & RuleActionParam.AttachmentMatch) != 0 && action.AttachmentMatch is null)
            problems.Add(new RuleProblem(ruleName, $"action '{action.Type}' needs a selector", true));

        switch (action.Type)
        {
            case RuleActionType.Reject when action.SmtpCode is { } code && (code < 400 || code > 599):
                problems.Add(new RuleProblem(ruleName,
                    $"SMTP reply code {code} is not a rejection code — use 400–599", true));
                break;

            case RuleActionType.RemoveAttachments
                when action.AttachmentMatch == AttachmentMatchMode.MinSizeBytes
                     && !long.TryParse(action.Value?.Trim(), out _):
                problems.Add(new RuleProblem(ruleName,
                    $"'{action.Value}' is not a byte count", true));
                break;

            case RuleActionType.SetImportance
                when !RuleActionSchema.ImportanceValues.Contains(action.Value?.Trim(), StringComparer.OrdinalIgnoreCase):
                problems.Add(new RuleProblem(ruleName,
                    $"importance '{action.Value}' is not one of {string.Join(", ", RuleActionSchema.ImportanceValues)}", true));
                break;

            case RuleActionType.SetHeader or RuleActionType.AddHeader when action.HeaderName is { } header:
                if (DescribeHeaderDeliveryWarning(header) is { } warning)
                    problems.Add(new RuleProblem(ruleName, warning, false));
                break;
        }
    }

    /// <summary>
    /// Why a header will not reach the recipient, or <see langword="null"/> when it will.
    ///
    /// Graph does not relay raw MIME — <c>GraphApiClient.BuildMessage</c> rebuilds the message
    /// property by property, and only headers named <c>x-…</c> survive as internet headers, plus
    /// the handful mapped onto Graph properties. Setting anything else changes the archived
    /// message but not the delivered one, which is precisely the kind of thing that has to be
    /// said out loud rather than discovered.
    /// </summary>
    internal static string? DescribeHeaderDeliveryWarning(string headerName)
    {
        var name = headerName.Trim();
        if (name.Length == 0)
            return null;

        if (name.StartsWith("x-ms-exchange", StringComparison.OrdinalIgnoreCase))
            return $"header '{name}' is reserved by Exchange and is dropped before delivery";

        if (name.Equals("x-priority", StringComparison.OrdinalIgnoreCase))
            return $"header '{name}' is not relayed as a header — use the SetImportance action instead";

        if (!name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
            return $"header '{name}' is not carried to Microsoft 365 (only 'x-…' headers are) — " +
                   "it will appear in the archived message but not at the recipient";

        return null;
    }

    // ---------------------------------------------------------------- descriptions

    /// <summary>One-line summary for grids and log lines.</summary>
    internal static string Describe(RuleCondition condition)
    {
        var field = condition.Field == RuleConditionField.Header && !string.IsNullOrWhiteSpace(condition.HeaderName)
            ? $"Header[{condition.HeaderName}]"
            : condition.Field.ToString();

        var negate = condition.Negate ? "NOT " : string.Empty;

        return condition.Operator switch
        {
            RuleConditionOperator.Exists or RuleConditionOperator.IsEmpty or RuleConditionOperator.IsTrue
                => $"{negate}{field} {condition.Operator}",
            _ => $"{negate}{field} {condition.Operator} '{condition.Value}'",
        };
    }

    /// <summary>One-line summary for grids and log lines.</summary>
    internal static string Describe(RuleAction action) => action.Type switch
    {
        RuleActionType.Reject => $"Reject {action.SmtpCode ?? MessageRuleDefaults.RejectCode} '{Shorten(action.Value)}'",
        RuleActionType.Discard => "Discard",
        RuleActionType.AddRecipient => $"Add {action.Recipient} {action.Value}",
        RuleActionType.RemoveRecipient => $"Remove recipient {action.Match}",
        RuleActionType.ReplaceRecipient => $"Replace recipient {action.Match} with {action.Value}",
        RuleActionType.SetSubject => $"Set subject '{Shorten(action.Value)}'",
        RuleActionType.PrefixSubject => $"Prefix subject '{Shorten(action.Value)}'",
        RuleActionType.SuffixSubject => $"Suffix subject '{Shorten(action.Value)}'",
        RuleActionType.PrependBody => $"Prepend body '{Shorten(action.Value)}'",
        RuleActionType.AppendBody => $"Append body '{Shorten(action.Value)}'",
        RuleActionType.SetHeader => $"Set header {action.HeaderName}: {Shorten(action.Value)}",
        RuleActionType.AddHeader => $"Add header {action.HeaderName}: {Shorten(action.Value)}",
        RuleActionType.RemoveHeader => $"Remove header {action.HeaderName}",
        RuleActionType.RemoveAttachments => $"Remove attachments ({action.AttachmentMatch} '{action.Value}')",
        RuleActionType.SetImportance => $"Set importance {action.Value}",
        RuleActionType.SetFrom => $"Set From {action.Value}",
        RuleActionType.SetReplyTo => $"Set Reply-To {action.Value}",
        _ => action.Type.ToString(),
    };

    private static string Shorten(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var single = value.ReplaceLineEndings(" ");
        return single.Length <= 40 ? single : single[..37] + "…";
    }
}

/// <summary>Values the engine falls back to when an action leaves them unset.</summary>
internal static class MessageRuleDefaults
{
    /// <summary>Permanent rejection; the message itself is the reason, so a retry is pointless.</summary>
    internal const int RejectCode = 550;

    internal const string RejectText = "Message rejected by policy";

    /// <summary>Longest reject text handed back to the client.</summary>
    internal const int MaxRejectTextLength = 200;
}
