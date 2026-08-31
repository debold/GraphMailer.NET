using GraphMailer.Service.Configuration;

namespace GraphMailer.Service.Infrastructure.Rules;

/// <summary>What the rule set decided about the message's fate.</summary>
internal enum RuleVerdict
{
    /// <summary>Queue the message (possibly modified).</summary>
    Continue,

    /// <summary>Refuse it during DATA with <see cref="MessageRuleOutcome.SmtpCode"/>.</summary>
    Reject,

    /// <summary>Accept with 250 but never deliver.</summary>
    Discard,
}

/// <summary>
/// How one rule ended, as recorded in the metrics. A rule that matched but whose every action
/// was skipped counts as <see cref="RuleOutcomes.Skipped"/> rather than not counting at all —
/// otherwise "my rule matches but nothing happens" is exactly the question the statistics
/// cannot answer.
/// </summary>
internal static class RuleOutcomes
{
    internal const string Modified = "modified";
    internal const string Rejected = "rejected";
    internal const string Discarded = "discarded";
    internal const string Skipped = "skipped";
}

/// <summary>One rule that matched the message.</summary>
internal readonly record struct MatchedRule(
    string Name, MessageRuleMode Mode, string Outcome, bool StoppedProcessing);

/// <summary>Why a rule did or did not act on the message.</summary>
internal enum RuleEvaluationStatus
{
    Matched,
    /// <summary>Conditions did not hold.</summary>
    NotMatched,
    /// <summary>Switched off, so never evaluated.</summary>
    Disabled,
    /// <summary>An earlier rule ended the run before this one was reached.</summary>
    NotReached,
}

/// <summary>
/// The fate of one rule, recorded only when the caller asks for an explanation.
///
/// The service does not need this — it acts on what matched. The rule tester does: "my rule does
/// nothing" is the single most common question about a rule set, and an output that lists only
/// the rules that fired cannot answer it.
/// </summary>
/// <param name="Reason">Why, for anything other than <see cref="RuleEvaluationStatus.Matched"/>.</param>
internal readonly record struct RuleEvaluation(
    string Name, MessageRuleMode Mode, RuleEvaluationStatus Status, string? Reason = null);

/// <summary>One action, and whether it actually ran.</summary>
/// <param name="Applied">False in Audit mode, and when the action was skipped.</param>
/// <param name="SkipReason">Why it did not run, when it did not.</param>
internal readonly record struct RuleActionRecord(
    string RuleName, RuleActionType Type, string Detail, bool Applied, string? SkipReason = null);

/// <summary>
/// The complete result of running a rule set over one message: the verdict, what each rule and
/// action did, and whether the message or its envelope need writing back.
/// </summary>
internal sealed record MessageRuleOutcome
{
    internal RuleVerdict Verdict { get; init; } = RuleVerdict.Continue;

    /// <summary>Only meaningful for <see cref="RuleVerdict.Reject"/>.</summary>
    internal int SmtpCode { get; init; } = MessageRuleDefaults.RejectCode;

    /// <summary>Already sanitised — safe to hand straight to the client.</summary>
    internal string SmtpText { get; init; } = MessageRuleDefaults.RejectText;

    /// <summary>The rule behind a Reject or Discard, for the log and the evidence record.</summary>
    internal string? DecidingRule { get; init; }

    /// <summary>Rules whose conditions matched, in evaluation order — Audit and Enforce alike.</summary>
    internal IReadOnlyList<MatchedRule> Matched { get; init; } = [];

    /// <summary>
    /// Every rule and what became of it. Populated only when the caller asked to explain the run;
    /// empty on the mail path, which has no use for it.
    /// </summary>
    internal IReadOnlyList<RuleEvaluation> Evaluated { get; init; } = [];

    internal IReadOnlyList<RuleActionRecord> Actions { get; init; } = [];

    /// <summary>The message was modified and has to be re-serialised.</summary>
    internal bool MessageChanged { get; init; }

    /// <summary>The envelope recipient list changed.</summary>
    internal bool EnvelopeChanged { get; init; }

    /// <summary>The envelope sender changed — the sending mailbox changes with it.</summary>
    internal bool EnvelopeFromChanged { get; init; }

    /// <summary>Anything an operator should see: skipped actions, unusable values, caps hit.</summary>
    internal IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Nothing matched and nothing changed.</summary>
    internal static readonly MessageRuleOutcome Unchanged = new();
}

/// <summary>
/// Limits from other config sections that the rule engine has to honour once it has changed a
/// message. Passed in rather than read from <c>IOptions</c> so the pure entry point stays pure
/// and the ConfigTool's rule tester can supply the same values.
/// </summary>
internal sealed record RulePolicyLimits
{
    /// <summary>Mirrors <c>Smtp.MaxRecipients</c>.</summary>
    internal int MaxRecipients { get; init; } = int.MaxValue;

    /// <summary>Mirrors <c>AllowedSenders</c> — re-checked after a SetFrom.</summary>
    internal IReadOnlyList<string> AllowedSenders { get; init; } = [];

    /// <summary>Mirrors <c>BlockedSenders</c> — re-checked after a SetFrom.</summary>
    internal IReadOnlyList<string> BlockedSenders { get; init; } = [];

    internal static readonly RulePolicyLimits None = new();
}
