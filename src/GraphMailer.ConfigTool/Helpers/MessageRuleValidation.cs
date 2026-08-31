using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Rules;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Input rules for the Message Rules page. Kept out of the WPF code-behind so every rule is
/// unit-testable.
///
/// Everything that has to agree with the runtime <b>delegates</b> to the service's own code —
/// <see cref="MessageRuleEvaluator"/>, <see cref="RuleConditionSchema"/>,
/// <see cref="RuleActionSchema"/>, <see cref="RuleRegexCache"/>. A second implementation of
/// "which operators are legal" or "does this pattern compile" would drift, and the symptom would
/// be a rule the tool accepts and the service silently never applies.
/// </summary>
internal static class MessageRuleValidation
{
    /// <summary>The operators the field allows, in the order the drop-down should list them.</summary>
    internal static IReadOnlyList<RuleConditionOperator> OperatorsFor(RuleConditionField field)
        => RuleConditionSchema.OperatorsFor(field);

    /// <summary>True when this field is a named header and needs a header name.</summary>
    internal static bool NeedsHeaderName(RuleConditionField field)
        => field == RuleConditionField.Header;

    /// <summary>True when the operator compares against a value the operator has to type.</summary>
    internal static bool NeedsValue(RuleConditionOperator op)
        => RuleConditionSchema.RequiresValue(op);

    /// <summary>Whether an action type uses a given property — drives field visibility.</summary>
    internal static bool ActionUses(RuleActionType type, RuleActionParam param)
        => RuleActionSchema.Uses(type, param);

    /// <summary>
    /// Validates a single concrete mail address, exactly as every other page in this tool does —
    /// notification recipients, backup recipients and the SMTP user dialog all go through
    /// <see cref="EmailValidation"/> / <see cref="AddressPatternValidator"/>. Using a different
    /// rule here would mean an address the Message Rules page accepts and the Notifications page
    /// refuses, or the other way round.
    /// </summary>
    internal static string? ValidateAddress(string? address)
        => EmailValidation.IsValidRecipient(address)
            ? null
            : "Enter a valid mail address, for example user@example.com.";

    /// <summary>
    /// Validates an address <i>pattern</i>: an exact address, an <c>@domain</c> entry, or a
    /// <c>*</c>/<c>?</c> wildcard; several separated by <c>;</c>.
    ///
    /// The shape rules come from <see cref="AddressPatternValidator"/>, the same ones the Access
    /// Control lists use. Wildcards are substituted before the check rather than given their own
    /// grammar, so the two can never disagree about what an address looks like.
    /// </summary>
    internal static string? ValidateAddressPattern(string? pattern)
    {
        var text = pattern?.Trim() ?? string.Empty;

        if (text.Length == 0)
            return "Enter an address, a domain such as @example.com, or a pattern such as *@example.com.";

        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsValidAddressPatternEntry(entry))
                return $"'{entry}' is not a valid address, domain or pattern.\n"
                     + "Accepted formats:\n"
                     + "  user@example.com  — exact address\n"
                     + "  @example.com      — every address at that domain\n"
                     + "  *@example.com     — wildcard (* for any text, ? for one character)";
        }

        return null;
    }

    private static bool IsValidAddressPatternEntry(string entry)
    {
        // A bare "*" is every recipient — legitimate, and the only entry that carries no
        // address shape at all.
        if (entry == "*") return true;

        // Substitute the wildcards with an ordinary character so the entry can be checked by
        // the same grammar an address without wildcards goes through.
        var probe = entry.Replace('*', 'x').Replace('?', 'x');
        return AddressPatternValidator.IsValidPattern(probe);
    }

    /// <summary>
    /// Validates one condition. Returns <see langword="null"/> when acceptable, otherwise the
    /// message to show.
    /// </summary>
    internal static string? ValidateCondition(
        RuleConditionField field,
        RuleConditionOperator op,
        string? value,
        string? headerName,
        bool caseSensitive,
        int regexTimeoutMs)
    {
        if (!RuleConditionSchema.IsSupported(field, op))
            return $"'{op}' cannot be used with {field}.";

        if (NeedsHeaderName(field) && string.IsNullOrWhiteSpace(headerName))
            return "Enter the name of the header to look at.";

        var text = value?.Trim() ?? string.Empty;

        if (NeedsValue(op) && text.Length == 0)
            return "Enter a value to compare against.";

        switch (op)
        {
            case RuleConditionOperator.RegexMatches
                when !RuleRegexCache.IsValid(text, caseSensitive, regexTimeoutMs):
                return "This is not a valid regular expression.";

            case RuleConditionOperator.InIpRange:
                foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (MalwareScanValidation.ValidateIpOrCidr(entry) is { } problem)
                        return problem;
                }
                return null;

            case RuleConditionOperator.Equals or RuleConditionOperator.GreaterThan or RuleConditionOperator.LessThan
                when RuleConditionSchema.TypeOf(field) == RuleFieldType.Number && !long.TryParse(text, out _):
                return "Enter a number.";

            // Same grammar the Access Control lists apply to an @domain entry, so a domain the
            // one page accepts is a domain the other accepts.
            case RuleConditionOperator.DomainIs:
                foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!entry.StartsWith('@'))
                        return $"'{entry}' must start with '@', for example @example.com.";
                    if (!AddressPatternValidator.IsValidPattern(entry))
                        return $"'{entry}' is not a valid domain.";
                }
                return null;
        }

        return null;
    }

    /// <summary>
    /// Validates one action. Returns <see langword="null"/> when acceptable, otherwise the
    /// message to show. Warnings about delivery are separate — see
    /// <see cref="DescribeActionWarning"/>.
    /// </summary>
    internal static string? ValidateAction(RuleActionType type, ConfigDocument.RuleActionEntry entry)
    {
        var required = RuleActionSchema.Required(type);

        // Whitespace-preserving actions accept a value that is only whitespace: a subject prefix
        // of a single space is a deliberate choice, not an empty field.
        if ((required & RuleActionParam.Value) != 0 && RuleActionSchema.IsValueMissing(type, entry.Value))
            return "Enter a value.";

        if ((required & RuleActionParam.HeaderName) != 0 && string.IsNullOrWhiteSpace(entry.HeaderName))
            return "Enter the header name.";

        if ((required & RuleActionParam.Match) != 0 && ValidateAddressPattern(entry.Match) is { } matchProblem)
            return matchProblem;

        if ((required & RuleActionParam.Recipient) != 0 && string.IsNullOrWhiteSpace(entry.Recipient))
            return "Choose To, Cc or Bcc.";

        if ((required & RuleActionParam.AttachmentMatch) != 0 && string.IsNullOrWhiteSpace(entry.AttachmentMatch))
            return "Choose how attachments are selected.";

        switch (type)
        {
            case RuleActionType.Reject when entry.SmtpCode is { } code && (code < 400 || code > 599):
                return "An SMTP rejection code is between 400 and 599.";

            case RuleActionType.RemoveAttachments
                when MessageRuleModel.ParseNullable<AttachmentMatchMode>(entry.AttachmentMatch)
                     == AttachmentMatchMode.MinSizeBytes && !long.TryParse(entry.Value?.Trim(), out _):
                return "Enter the size in bytes.";

            case RuleActionType.SetImportance
                when !RuleActionSchema.ImportanceValues.Contains(entry.Value?.Trim(), StringComparer.OrdinalIgnoreCase):
                return $"Choose one of {string.Join(", ", RuleActionSchema.ImportanceValues)}.";

            // These all take one concrete address, so they go through the tool's shared address
            // rule rather than the more permissive parser the runtime uses.
            case RuleActionType.AddRecipient or RuleActionType.ReplaceRecipient
                or RuleActionType.SetFrom or RuleActionType.SetReplyTo:
                return ValidateAddress(entry.Value);
        }

        return null;
    }

    /// <summary>
    /// What an operator should know about this action before saving it, or
    /// <see langword="null"/> when there is nothing to say. These are not errors — the action
    /// works, it just may not have the effect the wording suggests.
    /// </summary>
    internal static string? DescribeActionWarning(RuleActionType type, ConfigDocument.RuleActionEntry entry)
        => type switch
        {
            RuleActionType.SetHeader or RuleActionType.AddHeader when entry.HeaderName is { } name
                => MessageRuleEvaluator.DescribeHeaderDeliveryWarning(name),

            RuleActionType.PrependBody or RuleActionType.AppendBody when string.IsNullOrWhiteSpace(entry.Html)
                => "No HTML version given — it will be generated from the text. On a message that has "
                   + "both a plain-text and an HTML body, only the HTML one is delivered.",

            RuleActionType.SetFrom
                => "This changes the sending mailbox. The sender allow and block lists are checked "
                   + "again afterwards, but the tenant sender check is not — a sender the directory "
                   + "does not know will fail at delivery, not here.",

            RuleActionType.Discard
                => "The sending application is told the message was accepted. Nothing is delivered "
                   + "and nothing is retried.",

            _ => null,
        };

    /// <summary>Every problem in the current rule set, as the service would report it at startup.</summary>
    internal static IReadOnlyList<RuleProblem> FindProblems(ConfigDocument.MessageRulesSection section)
        => MessageRuleEvaluator.FindProblems(MessageRuleModel.ToOptions(section));

    /// <summary>True when another rule already uses this name (case-insensitive).</summary>
    internal static bool IsDuplicateName(
        IEnumerable<ConfigDocument.MessageRuleEntry> existing,
        string? candidate,
        ConfigDocument.MessageRuleEntry? ignore = null)
    {
        var name = (candidate ?? string.Empty).Trim();
        return name.Length > 0
            && existing.Any(r => !ReferenceEquals(r, ignore)
                                 && r.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One-line summary of a condition, for the rule grid.</summary>
    internal static string Describe(ConfigDocument.RuleConditionEntry entry)
        => MessageRuleEvaluator.Describe(MessageRuleModel.ToCondition(entry));

    /// <summary>One-line summary of an action, for the rule grid.</summary>
    internal static string Describe(ConfigDocument.RuleActionEntry entry)
        => MessageRuleEvaluator.Describe(MessageRuleModel.ToAction(entry));
}
