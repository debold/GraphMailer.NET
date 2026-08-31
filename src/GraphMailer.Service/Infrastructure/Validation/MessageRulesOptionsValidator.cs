using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Infrastructure.Validation;

/// <summary>
/// Validates the message rules at startup.
///
/// Only settings that would make the engine unusable fail startup. A broken <i>rule</i> is a
/// warning instead: the other rules still work, and refusing to start over one bad regular
/// expression would take mail flow down for a policy detail.
///
/// The point of the warnings is that every problem reported here is otherwise silent. A rule
/// with an invalid pattern, an impossible field/operator pair or a missing parameter simply
/// never matches — the operator sees a configured rule and no effect, with nothing to say why.
/// The ConfigTool catches these while they are typed, but a hand-edited or migrated
/// <c>graphmailer.json</c> never passes through it, so the service checks for itself.
///
/// Rules:
///   • MaxBodyScanBytes ≤ 0 → startup failure (no content condition could ever match)
///   • RegexTimeoutMs ≤ 0 → startup failure (every regular expression would time out)
///   • DiscardRecordRetentionDays ≤ 0 → startup failure (evidence deleted as soon as it is written)
///   • Any rule problem → warning naming the rule
///   • Rules in Enforce mode → restated at startup, they rewrite or refuse production mail
/// </summary>
internal sealed class MessageRulesOptionsValidator : IValidateOptions<MessageRulesOptions>
{
    private readonly ILogger<MessageRulesOptionsValidator> _logger;

    public MessageRulesOptionsValidator(ILogger<MessageRulesOptionsValidator> logger)
        => _logger = logger;

    public ValidateOptionsResult Validate(string? name, MessageRulesOptions options)
    {
        if (!options.Enabled)
        {
            if (options.Rules.Count > 0)
                _logger.LogInformation(
                    "[MessageRules] {Count} rule(s) are configured but the rule engine is switched off — no message is inspected.",
                    options.Rules.Count);
            return ValidateOptionsResult.Success;
        }

        if (options.MaxBodyScanBytes <= 0)
            return ValidateOptionsResult.Fail(
                $"MessageRules.MaxBodyScanBytes must be a positive value (configured: {options.MaxBodyScanBytes}). " +
                "No body or content condition could ever match.");

        if (options.RegexTimeoutMs <= 0)
            return ValidateOptionsResult.Fail(
                $"MessageRules.RegexTimeoutMs must be a positive value (configured: {options.RegexTimeoutMs}). " +
                "Every regular expression condition would time out immediately.");

        if (options.DiscardRecordRetentionDays <= 0)
            return ValidateOptionsResult.Fail(
                $"MessageRules.DiscardRecordRetentionDays must be a positive value (configured: {options.DiscardRecordRetentionDays}). " +
                "The record of a discarded message would be deleted as soon as it is written.");

        if (options.Rules.Count == 0)
        {
            _logger.LogInformation("[MessageRules] The rule engine is enabled but no rules are configured.");
            return ValidateOptionsResult.Success;
        }

        foreach (var problem in MessageRuleEvaluator.FindProblems(options))
        {
            if (problem.IsError)
                _logger.LogWarning(
                    "[MessageRules] Rule '{Rule}' cannot work as written: {Detail}",
                    problem.RuleName, problem.Detail);
            else
                _logger.LogWarning(
                    "[MessageRules] Rule '{Rule}': {Detail}",
                    problem.RuleName, problem.Detail);
        }

        var enabled = options.Rules.Count(r => r.Enabled);
        var enforcing = options.Rules.Count(r => r.Enabled && r.Mode == MessageRuleMode.Enforce);

        // Restated at every startup on purpose: a rule that rewrites or refuses mail is a
        // standing change to what the relay does, and it should never be something an operator
        // has to open the ConfigTool to remember.
        _logger.LogInformation(
            "[MessageRules] {Enabled} of {Total} rule(s) active, {Enforcing} in Enforce mode.",
            enabled, options.Rules.Count, enforcing);

        if (options.StoreDiscardedMessages)
            _logger.LogInformation(
                "[MessageRules] Discarded messages are stored in full under mail\\blocked\\ for {Days} day(s).",
                options.DiscardRecordRetentionDays);

        return ValidateOptionsResult.Success;
    }
}
