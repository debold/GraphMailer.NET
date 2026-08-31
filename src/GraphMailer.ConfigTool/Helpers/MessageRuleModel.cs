using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Converts the ConfigTool's editable rule model into the runtime options the service binds.
///
/// This is what lets the tool validate and preview rules through the service's own engine
/// instead of a second implementation: <c>MessageRuleEvaluator.FindProblems</c> and
/// <c>MessageRuleProcessor.Run</c> both take <see cref="MessageRulesOptions"/>, so the page
/// converts once and everything downstream is the real thing.
///
/// Enum tokens are stored as strings in <see cref="ConfigDocument"/> so a value written by a
/// newer build survives a round-trip. Converting back, an unrecognised token falls back to the
/// safest choice rather than throwing — the same stance <c>ConfigService</c> takes on load.
/// </summary>
internal static class MessageRuleModel
{
    internal static MessageRulesOptions ToOptions(ConfigDocument.MessageRulesSection section) => new()
    {
        Enabled = section.Enabled,
        MaxBodyScanBytes = section.MaxBodyScanBytes,
        RegexTimeoutMs = section.RegexTimeoutMs,
        StoreDiscardedMessages = section.StoreDiscardedMessages,
        DiscardRecordRetentionDays = section.DiscardRecordRetentionDays,
        Rules = [.. section.Rules.Select(ToRule)],
    };

    internal static MessageRule ToRule(ConfigDocument.MessageRuleEntry entry) => new()
    {
        Enabled = entry.Enabled,
        Name = entry.Name,
        Description = entry.Description,
        Mode = Parse(entry.Mode, MessageRuleMode.Audit),
        Match = Parse(entry.Match, ConditionMatch.All),
        StopProcessing = entry.StopProcessing,
        Conditions = [.. entry.Conditions.Select(ToCondition)],
        Actions = [.. entry.Actions.Select(ToAction)],
    };

    internal static RuleCondition ToCondition(ConfigDocument.RuleConditionEntry entry) => new()
    {
        Field = Parse(entry.Field, RuleConditionField.Subject),
        Operator = Parse(entry.Operator, RuleConditionOperator.Contains),
        Value = entry.Value,
        HeaderName = entry.HeaderName,
        Negate = entry.Negate,
        CaseSensitive = entry.CaseSensitive,
    };

    internal static RuleAction ToAction(ConfigDocument.RuleActionEntry entry) => new()
    {
        Type = Parse(entry.Type, RuleActionType.PrefixSubject),
        Value = entry.Value,
        Html = entry.Html,
        HeaderName = entry.HeaderName,
        Recipient = ParseNullable<RecipientKind>(entry.Recipient),
        Match = entry.Match,
        AttachmentMatch = ParseNullable<AttachmentMatchMode>(entry.AttachmentMatch),
        SmtpCode = entry.SmtpCode,
    };

    internal static T Parse<T>(string? token, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(token, ignoreCase: true, out var value) ? value : fallback;

    internal static T? ParseNullable<T>(string? token) where T : struct, Enum
        => Enum.TryParse<T>(token, ignoreCase: true, out var value) ? value : null;
}
