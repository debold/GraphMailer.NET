using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Smtp;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GraphMailer.Service.Infrastructure.Rules;

/// <summary>The outcome plus the message and envelope as the rules left them.</summary>
internal readonly record struct MessageRuleResult(
    MessageRuleOutcome Outcome,
    byte[] Eml,
    string EnvelopeFrom,
    IReadOnlyList<string> Recipients);

/// <summary>
/// Runs the configured rules over a received message.
///
/// <see cref="Run"/> is the whole engine: static, no DI, no IO. The ConfigTool's rule tester
/// calls exactly this method, which is the entire reason there is no second evaluation path to
/// keep in sync with the service.
///
/// The instance wrapper adds only what the SMTP session needs — reading the current options,
/// parsing and re-serialising the bytes, and logging.
/// </summary>
internal sealed class MessageRuleProcessor
{
    private readonly IOptionsMonitor<MessageRulesOptions> _options;
    private readonly IOptionsMonitor<SmtpOptions> _smtpOptions;
    private readonly IOptionsMonitor<SmtpAccessOptions> _access;
    private readonly ILogger<MessageRuleProcessor> _logger;

    public MessageRuleProcessor(
        IOptionsMonitor<MessageRulesOptions> options,
        IOptionsMonitor<SmtpOptions> smtpOptions,
        IOptionsMonitor<SmtpAccessOptions> access,
        ILogger<MessageRuleProcessor> logger)
    {
        _options = options;
        _smtpOptions = smtpOptions;
        _access = access;
        _logger = logger;
    }

    /// <summary>True when there is nothing to do — lets the caller skip the MIME parse entirely.</summary>
    public bool IsActive
    {
        get
        {
            var opts = _options.CurrentValue;
            return opts.Enabled && opts.Rules.Exists(r => r.Enabled);
        }
    }

    /// <summary>
    /// Applies the rule set to received bytes.
    ///
    /// Never throws: any unexpected failure is logged and the message is returned untouched with
    /// <see cref="RuleVerdict.Continue"/>. A bug in a rule must not become a mail outage — the
    /// same stance the malware scan takes towards a broken scanner.
    /// </summary>
    public MessageRuleResult Apply(
        byte[] emlBytes,
        string envelopeFrom,
        IReadOnlyList<string> recipients,
        RuleSessionFacts session,
        string messageId,
        CancellationToken ct)
    {
        // Read once: a config save between two rules would otherwise give this message a
        // half-old, half-new rule set.
        var options = _options.CurrentValue;

        if (!options.Enabled || options.Rules.Count == 0)
            return new MessageRuleResult(MessageRuleOutcome.Unchanged, emlBytes, envelopeFrom, recipients);

        try
        {
            var ctx = MessageRuleContext.Create(
                emlBytes, envelopeFrom, recipients, session, messageId, options.MaxBodyScanBytes);

            if (ctx.ParseFailed)
            {
                _logger.LogError(
                    "[MessageRules] {MessageId}: the message could not be parsed — every rule was skipped and the message is delivered unmodified",
                    messageId);
                return new MessageRuleResult(MessageRuleOutcome.Unchanged, emlBytes, envelopeFrom, recipients);
            }

            var smtp = _smtpOptions.CurrentValue;
            var access = _access.CurrentValue;
            var limits = new RulePolicyLimits
            {
                MaxRecipients = smtp.MaxRecipients,
                AllowedSenders = access.AllowedSenders,
                BlockedSenders = access.BlockedSenders,
            };

            var outcome = Run(options, ctx, limits);

            LogOutcome(outcome, ctx, messageId);

            var resultBytes = outcome.MessageChanged ? Serialise(ctx.Message, ct) : emlBytes;

            return new MessageRuleResult(
                outcome,
                resultBytes,
                ctx.EnvelopeFrom,
                outcome.EnvelopeChanged ? [.. ctx.EnvelopeRecipients] : recipients);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[MessageRules] Rule processing failed for {MessageId} — the message is delivered unmodified: {Error}",
                messageId, ex.Message);
            return new MessageRuleResult(MessageRuleOutcome.Unchanged, emlBytes, envelopeFrom, recipients);
        }
    }

    /// <summary>
    /// The engine. Evaluates the rules in array order and applies the actions of the ones that
    /// match, in Enforce mode.
    ///
    /// Audit changes nothing, but it <i>does</i> honour <c>StopProcessing</c> and it <i>does</i>
    /// stop at an action that would reject or discard. That makes an Audit run flow-identical to
    /// the Enforce run it predicts: if Audit ignored those, flipping one rule to Enforce would
    /// silently change which <i>later</i> rules ran, and an audit that cannot predict enforcement
    /// is worth nothing.
    /// </summary>
    /// <param name="explain">
    /// Record what became of <i>every</i> rule, not just the ones that matched. Off on the mail
    /// path, which has no use for it; the rule tester turns it on, because "my rule does nothing"
    /// cannot be answered from a list of the rules that fired.
    /// </param>
    internal static MessageRuleOutcome Run(
        MessageRulesOptions options, MessageRuleContext ctx, RulePolicyLimits limits, bool explain = false)
    {
        // The global switch is honoured here, not only in the caller, so every consumer of the
        // engine — including the ConfigTool's rule tester — sees what the service would really do.
        if (!options.Enabled || options.Rules.Count == 0)
            return MessageRuleOutcome.Unchanged;

        var matched = new List<MatchedRule>();
        var evaluated = new List<RuleEvaluation>();
        var actions = new List<RuleActionRecord>();
        var warnings = new List<string>();

        var originalFrom = ctx.EnvelopeFrom;
        var messageChanged = false;
        var envelopeChanged = false;

        var verdict = RuleVerdict.Continue;
        var smtpCode = MessageRuleDefaults.RejectCode;
        var smtpText = MessageRuleDefaults.RejectText;
        string? decidingRule = null;

        // A flag rather than a break, so the rules after a stop can still be reported as
        // "not reached" — which is one of the answers to "why did my rule do nothing".
        var stopped = false;

        foreach (var rule in options.Rules)
        {
            var name = string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name;

            if (stopped)
            {
                if (explain)
                    evaluated.Add(new RuleEvaluation(name, rule.Mode, RuleEvaluationStatus.NotReached,
                        "an earlier rule ended the run before this one was reached"));
                continue;
            }

            if (!rule.Enabled)
            {
                if (explain)
                    evaluated.Add(new RuleEvaluation(name, rule.Mode, RuleEvaluationStatus.Disabled,
                        "the rule is switched off"));
                continue;
            }

            if (!MessageRuleEvaluator.IsMatch(rule, ctx, options.RegexTimeoutMs))
            {
                // Re-evaluating the conditions costs a second pass, which is why it happens only
                // when an explanation was asked for.
                if (explain)
                    evaluated.Add(new RuleEvaluation(name, rule.Mode, RuleEvaluationStatus.NotMatched,
                        MessageRuleEvaluator.ExplainMismatch(rule, ctx, options.RegexTimeoutMs)));
                continue;
            }

            if (explain)
                evaluated.Add(new RuleEvaluation(name, rule.Mode, RuleEvaluationStatus.Matched));

            var enforcing = rule.Mode == MessageRuleMode.Enforce;
            var ruleName = name;
            var ruleOutcome = RuleOutcomes.Skipped;
            var terminated = false;

            foreach (var action in rule.Actions)
            {
                if (RuleActionSchema.IsTerminal(action.Type))
                {
                    ruleOutcome = action.Type == RuleActionType.Reject
                        ? RuleOutcomes.Rejected
                        : RuleOutcomes.Discarded;

                    actions.Add(new RuleActionRecord(
                        ruleName, action.Type, MessageRuleEvaluator.Describe(action), enforcing));

                    if (enforcing)
                    {
                        verdict = action.Type == RuleActionType.Reject ? RuleVerdict.Reject : RuleVerdict.Discard;
                        smtpCode = NormaliseCode(action.SmtpCode);
                        smtpText = SanitiseReplyText(action.Value);
                        decidingRule = ruleName;
                    }

                    terminated = true;
                    break;
                }

                if (RuleActionSchema.TouchesProtectedContent(action.Type)
                    && ctx.Protection != MimeProtectionKind.None)
                {
                    var reason = MimeProtection.Describe(ctx.Protection);
                    actions.Add(new RuleActionRecord(
                        ruleName, action.Type, MessageRuleEvaluator.Describe(action), false, reason));
                    warnings.Add($"rule '{ruleName}': {action.Type} skipped — {reason}");
                    continue;
                }

                if (!enforcing)
                {
                    actions.Add(new RuleActionRecord(
                        ruleName, action.Type, MessageRuleEvaluator.Describe(action), false));
                    ruleOutcome = RuleOutcomes.Modified;
                    continue;
                }

                var effect = MessageRuleActions.Apply(action, ctx);
                actions.Add(new RuleActionRecord(ruleName, action.Type, effect.Detail, true));

                if (effect.Changed) messageChanged = true;
                if (effect.EnvelopeChanged) envelopeChanged = true;
                if (effect.Changed || effect.EnvelopeChanged) ruleOutcome = RuleOutcomes.Modified;
                if (effect.Warning is { } actionWarning)
                    warnings.Add($"rule '{ruleName}': {actionWarning}");
            }

            matched.Add(new MatchedRule(ruleName, rule.Mode, ruleOutcome, rule.StopProcessing));

            if (terminated || rule.StopProcessing)
            {
                // Without an explanation there is nothing left to record, so stop walking.
                if (!explain) break;
                stopped = true;
            }
        }

        if (ctx.BodyTruncated)
            warnings.Add($"the body was truncated to {options.MaxBodyScanBytes} bytes for the content conditions");

        var fromChanged = !ctx.EnvelopeFrom.Equals(originalFrom, StringComparison.OrdinalIgnoreCase);

        // Post-conditions run only when the message is still on its way — a rejected or
        // discarded message has no envelope left to check.
        if (verdict == RuleVerdict.Continue)
        {
            var post = CheckPostConditions(ctx, limits, fromChanged, warnings);
            if (post is { } failure)
            {
                verdict = failure.Verdict;
                smtpCode = failure.SmtpCode;
                smtpText = failure.SmtpText;
                decidingRule = failure.DecidingRule ?? decidingRule;
            }
        }

        WarnAboutCustomHeaderCap(ctx, warnings);

        return new MessageRuleOutcome
        {
            Verdict = verdict,
            SmtpCode = smtpCode,
            SmtpText = smtpText,
            DecidingRule = decidingRule,
            Matched = matched,
            Evaluated = evaluated,
            Actions = actions,
            MessageChanged = messageChanged,
            EnvelopeChanged = envelopeChanged || fromChanged,
            EnvelopeFromChanged = fromChanged,
            Warnings = warnings,
        };
    }

    private readonly record struct PostConditionFailure(
        RuleVerdict Verdict, int SmtpCode, string SmtpText, string? DecidingRule);

    /// <summary>
    /// The checks a rewritten message has to survive before it may be queued.
    ///
    /// All three exist because the corresponding gate already ran, at MAIL FROM or RCPT TO,
    /// <i>before</i> the rules touched anything. Leaving them unchecked would let a rule walk
    /// straight past a policy the operator configured elsewhere.
    /// </summary>
    private static PostConditionFailure? CheckPostConditions(
        MessageRuleContext ctx, RulePolicyLimits limits, bool fromChanged, List<string> warnings)
    {
        // Nobody left to deliver to. Queueing it would only produce a Graph failure and an NDR
        // to a sender who did nothing wrong.
        if (ctx.EnvelopeRecipients.Count == 0)
        {
            warnings.Add("every recipient was removed — the message is discarded instead of queued");
            return new PostConditionFailure(RuleVerdict.Discard, 0, string.Empty, "(no recipients left)");
        }

        // SetFrom bypasses the sender policy, which ran at MAIL FROM against the original
        // address. Re-check it here; the tenant sender directory is deliberately not consulted
        // again, since that is an async Graph lookup and this runs inside DATA.
        if (fromChanged
            && !MailAddressFilter.IsAllowed(ctx.EnvelopeFrom, limits.AllowedSenders, limits.BlockedSenders))
        {
            var reason = MailAddressFilter.GetDenyReason(
                ctx.EnvelopeFrom, limits.AllowedSenders, limits.BlockedSenders);
            warnings.Add($"the rewritten sender '{ctx.EnvelopeFrom}' is not permitted: {reason}");
            return new PostConditionFailure(
                RuleVerdict.Reject, 550, "Sender address not permitted", "(sender policy)");
        }

        // Without this the message is accepted here and fails permanently in GraphApiClient
        // hours later, so the sender gets a late NDR instead of an immediate SMTP error.
        if (ctx.EnvelopeRecipients.Count > limits.MaxRecipients)
        {
            warnings.Add(
                $"{ctx.EnvelopeRecipients.Count} recipients after rule processing exceeds the limit of {limits.MaxRecipients}");
            return new PostConditionFailure(
                RuleVerdict.Reject, 554, "Too many recipients after message policy processing", "(recipient limit)");
        }

        return null;
    }

    /// <summary>
    /// Graph carries at most <c>GraphApiClient.MaxCustomHeaders</c> custom headers, and a message
    /// that exceeds it is rejected — the retry then drops <i>all</i> of them plus the Sender. The
    /// count depends on the incoming message, so no config-time check can catch this; it has to
    /// be counted here, on the message as the rules left it.
    /// </summary>
    private static void WarnAboutCustomHeaderCap(MessageRuleContext ctx, List<string> warnings)
    {
        var count = ctx.Message.Headers.Count(h =>
            h.Field.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
            && !h.Field.StartsWith("x-ms-exchange", StringComparison.OrdinalIgnoreCase)
            && !h.Field.Equals("x-priority", StringComparison.OrdinalIgnoreCase));

        if (count > GraphApiClient.MaxCustomHeaders)
            warnings.Add(
                $"the message now carries {count} custom 'x-' headers — Microsoft 365 accepts " +
                $"{GraphApiClient.MaxCustomHeaders}, and the retry after that rejection drops every " +
                "custom header and the Sender");
    }

    /// <summary>
    /// A reply code outside 4xx/5xx would make SmtpServer answer something that is not a
    /// rejection at all, so an out-of-range value falls back to the permanent default.
    /// </summary>
    internal static int NormaliseCode(int? code)
        => code is { } value && value >= 400 && value <= 599 ? value : MessageRuleDefaults.RejectCode;

    /// <summary>
    /// Makes an operator-authored reject text safe to send.
    ///
    /// This is a security requirement, not cosmetics: a CR or LF in an SMTP reply ends the line
    /// and lets the rest be read as further protocol responses. It is enforced here rather than
    /// only in the ConfigTool because a hand-edited <c>graphmailer.json</c> never passes through
    /// the UI.
    /// </summary>
    internal static string SanitiseReplyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return MessageRuleDefaults.RejectText;

        var cleaned = new string([.. text.Where(c => !char.IsControl(c))]).Trim();

        if (cleaned.Length == 0)
            return MessageRuleDefaults.RejectText;

        return cleaned.Length <= MessageRuleDefaults.MaxRejectTextLength
            ? cleaned
            : cleaned[..MessageRuleDefaults.MaxRejectTextLength];
    }

    /// <summary>
    /// Re-serialises the modified message. The line ending is pinned to CRLF: SMTP requires it,
    /// and relying on the platform default would make the output depend on where the service runs.
    /// </summary>
    private static byte[] Serialise(MimeMessage message, CancellationToken ct)
    {
        var format = FormatOptions.Default.Clone();
        format.NewLineFormat = NewLineFormat.Dos;

        using var stream = new MemoryStream();
        message.WriteTo(format, stream, ct);
        return stream.ToArray();
    }

    private void LogOutcome(MessageRuleOutcome outcome, MessageRuleContext ctx, string messageId)
    {
        foreach (var warning in outcome.Warnings)
            _logger.LogWarning("[MessageRules] {MessageId}: {Warning}", messageId, warning);

        if (outcome.Matched.Count == 0)
        {
            _logger.LogDebug("[MessageRules] {MessageId}: no rule matched", messageId);
            return;
        }

        foreach (var rule in outcome.Matched)
        {
            var applied = outcome.Actions
                .Where(a => a.RuleName == rule.Name && a.Applied)
                .Select(a => a.Detail)
                .ToList();

            if (rule.Mode == MessageRuleMode.Audit)
            {
                var wouldApply = outcome.Actions
                    .Where(a => a.RuleName == rule.Name)
                    .Select(a => a.Detail)
                    .ToList();

                _logger.LogInformation(
                    "[MessageRules] {MessageId}: rule {Rule} matched in AUDIT MODE — would have: {Actions}",
                    messageId, rule.Name, string.Join("; ", wouldApply));
                continue;
            }

            _logger.LogInformation(
                "[MessageRules] {MessageId}: rule {Rule} applied — {Actions}",
                messageId, rule.Name, applied.Count > 0 ? string.Join("; ", applied) : "no change");
        }

        if (outcome.EnvelopeChanged)
            _logger.LogInformation(
                "[MessageRules] {MessageId}: envelope is now {From} → {Recipients}",
                messageId, ctx.EnvelopeFrom, string.Join(", ", ctx.EnvelopeRecipients));
    }
}
