using GraphMailer.Service.Configuration;

namespace GraphMailer.Service.Infrastructure.Rules;

/// <summary>The comparison family a condition field belongs to.</summary>
internal enum RuleFieldType
{
    /// <summary>Mail address; adds <see cref="RuleConditionOperator.DomainIs"/>.</summary>
    Address,
    Text,
    Number,
    Bool,
    /// <summary>Named header value; requires <c>HeaderName</c>.</summary>
    Header,
    /// <summary>IP address; adds <see cref="RuleConditionOperator.InIpRange"/>.</summary>
    Ip,
}

/// <summary>
/// Which operators are legal for which condition field, and which fields carry several values.
///
/// Single source of truth for the evaluator, the startup validator and the ConfigTool's operator
/// drop-down. A field/operator pair outside this table can never match, so it is reported as a
/// configuration problem instead of silently evaluating to false at delivery time.
/// </summary>
internal static class RuleConditionSchema
{
    private static readonly RuleConditionOperator[] TextOperators =
    [
        RuleConditionOperator.Equals, RuleConditionOperator.Contains,
        RuleConditionOperator.StartsWith, RuleConditionOperator.EndsWith,
        RuleConditionOperator.Matches, RuleConditionOperator.RegexMatches,
        RuleConditionOperator.Exists, RuleConditionOperator.IsEmpty,
    ];

    private static readonly RuleConditionOperator[] AddressOperators =
        [.. TextOperators, RuleConditionOperator.DomainIs];

    private static readonly RuleConditionOperator[] IpOperators =
    [
        RuleConditionOperator.Equals, RuleConditionOperator.StartsWith,
        RuleConditionOperator.Matches, RuleConditionOperator.InIpRange,
        RuleConditionOperator.Exists, RuleConditionOperator.IsEmpty,
    ];

    private static readonly RuleConditionOperator[] NumberOperators =
        [RuleConditionOperator.Equals, RuleConditionOperator.GreaterThan, RuleConditionOperator.LessThan];

    private static readonly RuleConditionOperator[] BoolOperators = [RuleConditionOperator.IsTrue];

    internal static RuleFieldType TypeOf(RuleConditionField field) => field switch
    {
        RuleConditionField.EnvelopeFrom or RuleConditionField.EnvelopeRecipient
            or RuleConditionField.HeaderFrom or RuleConditionField.HeaderTo
            or RuleConditionField.HeaderCc or RuleConditionField.HeaderReplyTo => RuleFieldType.Address,

        RuleConditionField.RecipientCount or RuleConditionField.MessageSizeBytes
            or RuleConditionField.ListenerPort or RuleConditionField.AttachmentCount
            or RuleConditionField.AttachmentSizeBytes => RuleFieldType.Number,

        RuleConditionField.Authenticated or RuleConditionField.Tls
            or RuleConditionField.IsSigned or RuleConditionField.IsEncrypted => RuleFieldType.Bool,

        RuleConditionField.Header => RuleFieldType.Header,
        RuleConditionField.ClientIp => RuleFieldType.Ip,

        _ => RuleFieldType.Text,
    };

    /// <summary>
    /// Fields that carry several values. These match existentially — true when <i>any</i> value
    /// matches — and <c>Negate</c> is applied afterwards, so a negated condition reads "none of
    /// them match".
    /// </summary>
    internal static bool IsMultiValued(RuleConditionField field) => field is
        RuleConditionField.EnvelopeRecipient or RuleConditionField.HeaderTo
        or RuleConditionField.HeaderCc or RuleConditionField.HeaderReplyTo
        or RuleConditionField.Header or RuleConditionField.AttachmentName
        or RuleConditionField.AttachmentExtension or RuleConditionField.AttachmentSizeBytes;

    internal static IReadOnlyList<RuleConditionOperator> OperatorsFor(RuleConditionField field)
        => TypeOf(field) switch
        {
            RuleFieldType.Address => AddressOperators,
            RuleFieldType.Number => NumberOperators,
            RuleFieldType.Bool => BoolOperators,
            RuleFieldType.Ip => IpOperators,
            _ => TextOperators,
        };

    internal static bool IsSupported(RuleConditionField field, RuleConditionOperator op)
        => OperatorsFor(field).Contains(op);

    /// <summary>Operators that need a non-empty <c>Value</c> to mean anything.</summary>
    internal static bool RequiresValue(RuleConditionOperator op) => op is not (
        RuleConditionOperator.Exists or RuleConditionOperator.IsEmpty or RuleConditionOperator.IsTrue);
}

/// <summary>Which properties of <see cref="RuleAction"/> a given action type uses.</summary>
[Flags]
internal enum RuleActionParam
{
    None = 0,
    Value = 1,
    Html = 2,
    HeaderName = 4,
    Recipient = 8,
    Match = 16,
    AttachmentMatch = 32,
    SmtpCode = 64,
}

/// <summary>
/// The parameter contract of every action type.
///
/// <see cref="RuleAction"/> is one wide class with nullable properties, which keeps binding and
/// JSON simple but by itself allows nonsense (a header name on a subject action). This table
/// closes that: it drives the startup validator, the config writer (which emits only the keys an
/// action actually uses), the ConfigTool's field visibility and its validation. One table, four
/// consumers, no drift.
/// </summary>
internal static class RuleActionSchema
{
    private static readonly Dictionary<RuleActionType, (RuleActionParam Required, RuleActionParam Optional)> Contract =
        new()
        {
            [RuleActionType.Reject] = (RuleActionParam.None, RuleActionParam.Value | RuleActionParam.SmtpCode),
            [RuleActionType.Discard] = (RuleActionParam.None, RuleActionParam.None),

            [RuleActionType.AddRecipient] = (RuleActionParam.Value | RuleActionParam.Recipient, RuleActionParam.None),
            [RuleActionType.RemoveRecipient] = (RuleActionParam.Match, RuleActionParam.None),
            [RuleActionType.ReplaceRecipient] = (RuleActionParam.Match | RuleActionParam.Value, RuleActionParam.Recipient),

            [RuleActionType.SetSubject] = (RuleActionParam.Value, RuleActionParam.None),
            [RuleActionType.PrefixSubject] = (RuleActionParam.Value, RuleActionParam.None),
            [RuleActionType.SuffixSubject] = (RuleActionParam.Value, RuleActionParam.None),

            [RuleActionType.PrependBody] = (RuleActionParam.Value, RuleActionParam.Html),
            [RuleActionType.AppendBody] = (RuleActionParam.Value, RuleActionParam.Html),

            [RuleActionType.SetHeader] = (RuleActionParam.HeaderName | RuleActionParam.Value, RuleActionParam.None),
            [RuleActionType.AddHeader] = (RuleActionParam.HeaderName | RuleActionParam.Value, RuleActionParam.None),
            [RuleActionType.RemoveHeader] = (RuleActionParam.HeaderName, RuleActionParam.None),

            [RuleActionType.RemoveAttachments] = (RuleActionParam.AttachmentMatch | RuleActionParam.Value, RuleActionParam.None),
            [RuleActionType.SetImportance] = (RuleActionParam.Value, RuleActionParam.None),
            [RuleActionType.SetFrom] = (RuleActionParam.Value, RuleActionParam.None),
            [RuleActionType.SetReplyTo] = (RuleActionParam.Value, RuleActionParam.None),
        };

    internal static RuleActionParam Required(RuleActionType type)
        => Contract.TryGetValue(type, out var c) ? c.Required : RuleActionParam.None;

    internal static RuleActionParam Optional(RuleActionType type)
        => Contract.TryGetValue(type, out var c) ? c.Optional : RuleActionParam.None;

    /// <summary>Every property the action type uses, required or not.</summary>
    internal static RuleActionParam Used(RuleActionType type) => Required(type) | Optional(type);

    internal static bool Uses(RuleActionType type, RuleActionParam param) => (Used(type) & param) != 0;

    /// <summary>
    /// Actions that rewrite the message body or its attachments. These are skipped on signed or
    /// encrypted mail — the signature covers exactly this content, and the armour cannot be
    /// meaningfully edited at all. Everything else (recipients, subject, headers) sits outside
    /// both the S/MIME signature scope and the PGP armour and still applies.
    /// </summary>
    internal static bool TouchesProtectedContent(RuleActionType type) => type is
        RuleActionType.PrependBody or RuleActionType.AppendBody or RuleActionType.RemoveAttachments;

    /// <summary>Actions that decide the message's fate rather than changing it.</summary>
    internal static bool IsTerminal(RuleActionType type) => type is
        RuleActionType.Reject or RuleActionType.Discard;

    /// <summary>
    /// Actions whose <see cref="RuleAction.Value"/> (and <see cref="RuleAction.Html"/>) is prose
    /// that is spliced into the message verbatim, so leading and trailing whitespace is part of
    /// what the operator wrote and must survive.
    ///
    /// A subject prefix of <c>"[EXTERNAL] "</c> is the obvious case: trimming it produces
    /// <c>"[EXTERNAL]Quarterly report"</c>. The same holds for a suffix, and for body text where
    /// indentation and blank lines carry the layout. Everywhere else — addresses, header names,
    /// patterns, tokens — trimming is what the rest of the tool does and is right.
    /// </summary>
    internal static bool PreservesWhitespace(RuleActionType type) => type is
        RuleActionType.SetSubject or RuleActionType.PrefixSubject or RuleActionType.SuffixSubject
        or RuleActionType.PrependBody or RuleActionType.AppendBody;

    /// <summary>
    /// Whether the action's required value is absent. For a whitespace-preserving action only an
    /// empty value counts as missing — a value of a single space is a deliberate choice there,
    /// and rejecting it would contradict the whole point of preserving whitespace.
    /// </summary>
    internal static bool IsValueMissing(RuleActionType type, string? value)
        => PreservesWhitespace(type)
            ? string.IsNullOrEmpty(value)
            : string.IsNullOrWhiteSpace(value);

    /// <summary>The valid <c>Value</c> tokens of <see cref="RuleActionType.SetImportance"/>.</summary>
    internal static readonly string[] ImportanceValues = ["Low", "Normal", "High"];
}
