using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphMailer.Tests.Unit.Infrastructure.Validation;

/// <summary>
/// Startup validation of the rule section.
///
/// The dividing line: a setting that would make the engine unusable fails startup; a broken
/// individual rule is a warning, because the other rules still work and refusing to start over
/// one bad regular expression would take mail flow down for a policy detail.
/// </summary>
public sealed class MessageRulesOptionsValidatorTests
{
    private static MessageRulesOptionsValidator Sut()
        => new(NullLogger<MessageRulesOptionsValidator>.Instance);

    private static MessageRule Rule(params RuleAction[] actions)
        => new() { Name = "Test", Actions = [.. actions] };

    private static RuleAction Discard => new() { Type = RuleActionType.Discard };

    // =========================================================================
    // Defaults and the disabled case
    // =========================================================================

    [Fact]
    public void Validate_Defaults_Succeed()
    {
        Sut().Validate(null, new MessageRulesOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_Disabled_SucceedsEvenWithBrokenRules()
    {
        // Nothing is evaluated while the engine is off, so nothing can fail because of it.
        var options = new MessageRulesOptions
        {
            Enabled = false,
            MaxBodyScanBytes = -1,
            Rules = [Rule()],
        };

        Sut().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EnabledWithoutRules_Succeeds()
    {
        Sut().Validate(null, new MessageRulesOptions { Enabled = true }).Succeeded.Should().BeTrue();
    }

    // =========================================================================
    // Settings that make the engine unusable → startup failure
    // =========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxBodyScanBytes_Fails(long value)
    {
        var options = new MessageRulesOptions { Enabled = true, MaxBodyScanBytes = value };

        var result = Sut().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("MaxBodyScanBytes");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_NonPositiveRegexTimeout_Fails(int value)
    {
        var options = new MessageRulesOptions { Enabled = true, RegexTimeoutMs = value };

        var result = Sut().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("RegexTimeoutMs");
    }

    [Fact]
    public void Validate_NonPositiveDiscardRetention_Fails()
    {
        var options = new MessageRulesOptions { Enabled = true, DiscardRecordRetentionDays = 0 };

        var result = Sut().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("DiscardRecordRetentionDays");
    }

    // =========================================================================
    // Broken rules are warnings, not startup failures
    // =========================================================================

    [Fact]
    public void Validate_RuleWithInvalidRegex_StillSucceeds()
    {
        var options = new MessageRulesOptions
        {
            Enabled = true,
            Rules =
            [
                new MessageRule
                {
                    Name = "broken",
                    Conditions =
                    [
                        new RuleCondition
                        {
                            Field = RuleConditionField.Subject,
                            Operator = RuleConditionOperator.RegexMatches,
                            Value = "([unclosed",
                        },
                    ],
                    Actions = [Discard],
                },
            ],
        };

        Sut().Validate(null, options).Succeeded.Should().BeTrue(
            "one unusable rule must not take mail flow down");
    }

    [Fact]
    public void Validate_RuleWithoutActions_StillSucceeds()
    {
        var options = new MessageRulesOptions { Enabled = true, Rules = [Rule()] };

        Sut().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_RuleWithUndeliverableHeader_StillSucceeds()
    {
        var options = new MessageRulesOptions
        {
            Enabled = true,
            Rules =
            [
                Rule(new RuleAction
                {
                    Type = RuleActionType.SetHeader,
                    HeaderName = "List-Unsubscribe",
                    Value = "<mailto:x@example.com>",
                }),
            ],
        };

        Sut().Validate(null, options).Succeeded.Should().BeTrue(
            "the header simply will not reach the recipient — that is a warning, not a fault");
    }

    [Fact]
    public void Validate_EnforcingRules_Succeed()
    {
        var options = new MessageRulesOptions
        {
            Enabled = true,
            StoreDiscardedMessages = true,
            Rules =
            [
                new MessageRule { Name = "enforce", Mode = MessageRuleMode.Enforce, Actions = [Discard] },
            ],
        };

        Sut().Validate(null, options).Succeeded.Should().BeTrue();
    }
}
