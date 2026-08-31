namespace GraphMailer.Service.Configuration;

/// <summary>How a matching rule's actions are acted upon.</summary>
public enum MessageRuleMode
{
    /// <summary>
    /// Evaluate and log what the rule <i>would</i> do, but change nothing. The rollout mode:
    /// an operator watches a rule against production mail before letting it rewrite anything.
    /// </summary>
    Audit,

    /// <summary>Apply the rule's actions to the message.</summary>
    Enforce,
}

/// <summary>How a rule's conditions are combined.</summary>
public enum ConditionMatch
{
    /// <summary>Every condition must be true.</summary>
    All,

    /// <summary>At least one condition must be true.</summary>
    Any,
}

/// <summary>Which recipient list an address is added to or removed from.</summary>
public enum RecipientKind { To, Cc, Bcc }

/// <summary>How <see cref="RuleActionType.RemoveAttachments"/> selects what to remove.</summary>
public enum AttachmentMatchMode
{
    /// <summary>Wildcard match against the file name; ';'-separated alternatives.</summary>
    NamePattern,

    /// <summary>';'-separated extension list, leading dot optional.</summary>
    Extension,

    /// <summary>Every attachment at or above the given encoded size in bytes.</summary>
    MinSizeBytes,
}

/// <summary>
/// What a condition looks at. Session and envelope fields are available without parsing the
/// message; everything else comes from the parsed MIME and is materialised on demand, so a
/// rule set that never asks for the body never decodes one.
/// </summary>
public enum RuleConditionField
{
    // --- envelope / session: available before the MIME parse ---
    EnvelopeFrom,
    /// <summary>Multi-valued: true when <i>any</i> envelope recipient matches.</summary>
    EnvelopeRecipient,
    RecipientCount,
    MessageSizeBytes,
    ClientIp,
    AuthUser,
    ListenerPort,
    Authenticated,
    Tls,

    // --- parsed MIME ---
    HeaderFrom,
    /// <summary>Multi-valued: true when <i>any</i> To: address matches.</summary>
    HeaderTo,
    /// <summary>Multi-valued: true when <i>any</i> Cc: address matches.</summary>
    HeaderCc,
    HeaderReplyTo,
    Subject,
    BodyText,
    BodyHtml,
    /// <summary>Named header; requires <see cref="RuleCondition.HeaderName"/>. Multi-valued.</summary>
    Header,
    /// <summary>Multi-valued: true when <i>any</i> attachment's file name matches.</summary>
    AttachmentName,
    /// <summary>Multi-valued: true when <i>any</i> attachment's extension matches.</summary>
    AttachmentExtension,
    AttachmentCount,
    /// <summary>Multi-valued: true when <i>any</i> attachment satisfies the comparison.</summary>
    AttachmentSizeBytes,
    Importance,
    IsSigned,
    IsEncrypted,
}

/// <summary>
/// How a condition compares. Which operators are legal for which field is defined once in
/// <c>RuleConditionSchema</c> — a pair outside that table never matches and is reported as a
/// configuration problem rather than throwing.
/// </summary>
public enum RuleConditionOperator
{
    Equals,
    Contains,
    StartsWith,
    EndsWith,
    /// <summary>Wildcards <c>*</c> and <c>?</c>; ';'-separated alternatives.</summary>
    Matches,
    RegexMatches,
    /// <summary>Address fields only. Same semantics as the sender/recipient lists: exact domain, no subdomains.</summary>
    DomainIs,
    /// <summary><see cref="RuleConditionField.ClientIp"/> only; ';'-separated CIDRs or bare IPs.</summary>
    InIpRange,
    GreaterThan,
    LessThan,
    /// <summary>Value present and non-empty (a named header: present at all).</summary>
    Exists,
    IsEmpty,
    /// <summary>Boolean fields only.</summary>
    IsTrue,
}

/// <summary>What a rule does to a matching message.</summary>
public enum RuleActionType
{
    /// <summary>Refuse the message during DATA with a configurable SMTP reply.</summary>
    Reject,
    /// <summary>Accept with 250 but never deliver. Recorded under <c>mail\blocked\</c>.</summary>
    Discard,

    AddRecipient,
    RemoveRecipient,
    ReplaceRecipient,

    SetSubject,
    PrefixSubject,
    SuffixSubject,

    PrependBody,
    AppendBody,

    /// <summary>Replace every existing occurrence of the header, or add it when absent.</summary>
    SetHeader,
    /// <summary>Append another occurrence, leaving existing ones in place.</summary>
    AddHeader,
    /// <summary>Remove every occurrence of the header.</summary>
    RemoveHeader,

    RemoveAttachments,
    SetImportance,
    SetFrom,
    SetReplyTo,
}

/// <summary>
/// One condition of a rule.
///
/// Multi-valued fields (recipients, headers, attachments) use existential semantics — the
/// condition is true when <i>any</i> value matches — and <see cref="Negate"/> is applied
/// <i>after</i> that. So "EnvelopeRecipient DomainIs @x.com" with Negate means "no recipient
/// at x.com", which is the reading operators expect.
/// </summary>
public sealed class RuleCondition
{
    public RuleConditionField Field { get; init; }

    public RuleConditionOperator Operator { get; init; } = RuleConditionOperator.Contains;

    /// <summary>The comparison value. Unused by Exists/IsEmpty/IsTrue.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Required for <see cref="RuleConditionField.Header"/>, ignored otherwise.</summary>
    public string? HeaderName { get; init; }

    /// <summary>Inverts this condition's result. Every condition is individually negatable.</summary>
    public bool Negate { get; init; }

    /// <summary>Text comparison is case-insensitive by default — mail rarely means otherwise.</summary>
    public bool CaseSensitive { get; init; }
}

/// <summary>
/// One action of a rule.
///
/// Deliberately one wide class with nullable, <i>named</i> properties rather than a parameter
/// bag: the JSON stays readable, the binder stays trivial, and every value keeps its type.
/// Which properties belong to which <see cref="Type"/> is declared once in
/// <c>RuleActionSchema</c>, which drives the validator, the config writer and the ConfigTool
/// editor alike — so the four can never disagree.
/// </summary>
public sealed class RuleAction
{
    public RuleActionType Type { get; init; }

    /// <summary>
    /// Primary payload; its meaning follows <see cref="Type"/>: the subject text, header value,
    /// address, importance token, attachment pattern or size, or the reject text.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// HTML counterpart of <see cref="Value"/> for <see cref="RuleActionType.PrependBody"/> and
    /// <see cref="RuleActionType.AppendBody"/>. Blank → derived from <see cref="Value"/>
    /// (HTML-escaped, newlines to &lt;br&gt;).
    /// </summary>
    public string? Html { get; init; }

    /// <summary>Header name for the header actions.</summary>
    public string? HeaderName { get; init; }

    /// <summary>
    /// Which list to add to. Bcc is envelope-only and deliberately writes no header: Graph
    /// derives Bcc as "envelope minus To/Cc", so a Bcc header would be ignored on delivery
    /// and would leak the blind copy into the archived message.
    /// </summary>
    public RecipientKind? Recipient { get; init; }

    /// <summary>The address (exact or wildcard) being removed or replaced.</summary>
    public string? Match { get; init; }

    /// <summary>Selector for <see cref="RuleActionType.RemoveAttachments"/>.</summary>
    public AttachmentMatchMode? AttachmentMatch { get; init; }

    /// <summary>SMTP reply code for <see cref="RuleActionType.Reject"/>; 400–599, default 550.</summary>
    public int? SmtpCode { get; init; }
}

/// <summary>
/// One rule: conditions that select a message, actions that act on it.
///
/// Evaluation order is the order of <see cref="MessageRulesOptions.Rules"/> — there is no
/// separate order field to drift out of sync with the array.
/// </summary>
public sealed class MessageRule
{
    public bool Enabled { get; init; } = true;

    public string Name { get; init; } = string.Empty;

    /// <summary>Free-text note, so a later reader knows what the rule is for.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Defaults to <see cref="MessageRuleMode.Audit"/> — same reasoning as the malware scan's
    /// mode: a rule must never start rewriting production mail the moment it is typed in.
    /// </summary>
    public MessageRuleMode Mode { get; init; } = MessageRuleMode.Audit;

    public ConditionMatch Match { get; init; } = ConditionMatch.All;

    /// <summary>An empty list matches every message — a deliberate "apply to all" rule.</summary>
    public List<RuleCondition> Conditions { get; init; } = [];

    public List<RuleAction> Actions { get; init; } = [];

    /// <summary>
    /// Stop after this rule. Honoured in <b>both</b> modes, so an Audit run predicts the
    /// Enforce flow exactly — otherwise flipping a rule to Enforce would silently change
    /// which <i>later</i> rules ran.
    /// </summary>
    public bool StopProcessing { get; init; }
}

public sealed class MessageRulesOptions
{
    public const string SectionName = "MessageRules";

    /// <summary>
    /// Global switch, off by default — including for existing installations, whose config
    /// predates this section and therefore binds to the default. An update must never start
    /// rewriting or refusing mail that flowed yesterday.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// How much decoded body text the content conditions look at. Oversized bodies are
    /// <i>truncated</i>, not skipped: truncation gives predictable prefix semantics, whereas
    /// skipping would make a negated condition fire on every large message — a silent
    /// inversion of the operator's intent.
    /// </summary>
    public long MaxBodyScanBytes { get; init; } = 1_048_576;

    /// <summary>Match timeout for the backtracking regex fallback (ReDoS guard).</summary>
    public int RegexTimeoutMs { get; init; } = 100;

    /// <summary>
    /// Whether a discarded message's full content is written next to its record under
    /// <c>mail\blocked\</c>. Off by default — a silent drop is otherwise impossible to debug,
    /// but storing every discarded message is a deliberate choice, not a default.
    /// </summary>
    public bool StoreDiscardedMessages { get; init; }

    /// <summary>
    /// How long discard records are kept. Deliberately separate from
    /// <see cref="MalwareScanOptions.BlockedRecordRetentionDays"/>: that one lives on the
    /// malware page and applies even when scanning is switched off entirely, so sharing it
    /// would let a setting nobody is looking at delete this evidence.
    /// </summary>
    public int DiscardRecordRetentionDays { get; init; } = 60;

    /// <summary>Ordered. The array order <i>is</i> the evaluation order.</summary>
    public List<MessageRule> Rules { get; init; } = [];
}
