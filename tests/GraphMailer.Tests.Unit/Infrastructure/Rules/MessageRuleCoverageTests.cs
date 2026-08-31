using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Rules;
using static GraphMailer.Tests.Unit.Infrastructure.Rules.RuleTestFactory;

namespace GraphMailer.Tests.Unit.Infrastructure.Rules;

/// <summary>
/// Exhaustiveness: every condition field and every action type is exercised, driven by the enums
/// themselves.
///
/// The individual behaviour of a field or an action is covered in
/// <see cref="MessageRuleEvaluatorTests"/> and <see cref="MessageRuleActionsTests"/>. What these
/// add is the guarantee that the set stays complete — adding a value to either enum without a
/// test case fails here with a message saying so, instead of shipping a field an operator can
/// configure and nobody has ever run.
/// </summary>
public sealed class MessageRuleCoverageTests
{
    private const int Timeout = 100;

    // =========================================================================
    // Condition fields
    // =========================================================================

    /// <summary>A condition that should match, and one on the same field that should not.</summary>
    private readonly record struct FieldCase(
        RuleCondition Matching,
        RuleCondition NonMatching,
        Func<MessageRuleContext>? MatchContext = null,
        Func<MessageRuleContext>? NonMatchContext = null);

    public static IEnumerable<object[]> EveryConditionField()
        => Enum.GetValues<RuleConditionField>().Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(EveryConditionField))]
    public void EveryConditionField_MatchesAndFailsAsExpected(RuleConditionField field)
    {
        var c = CaseFor(field);

        var matchCtx = (c.MatchContext ?? RichContext)();
        var nonMatchCtx = (c.NonMatchContext ?? RichContext)();

        MessageRuleEvaluator.Matches(c.Matching, matchCtx, Timeout)
            .Should().BeTrue($"{field} should match {MessageRuleEvaluator.Describe(c.Matching)}");

        MessageRuleEvaluator.Matches(c.NonMatching, nonMatchCtx, Timeout)
            .Should().BeFalse($"{field} should not match {MessageRuleEvaluator.Describe(c.NonMatching)}");
    }

    /// <summary>
    /// The case table. The <c>default</c> arm is the point of the whole file: a new field lands
    /// here and fails loudly rather than silently going untested.
    /// </summary>
    private static FieldCase CaseFor(RuleConditionField field) => field switch
    {
        RuleConditionField.EnvelopeFrom => new(
            Condition(field, RuleConditionOperator.Equals, "sender@example.com"),
            Condition(field, RuleConditionOperator.Equals, "someone-else@example.com")),

        RuleConditionField.EnvelopeRecipient => new(
            Condition(field, RuleConditionOperator.DomainIs, "@partner.test"),
            Condition(field, RuleConditionOperator.DomainIs, "@nowhere.test")),

        RuleConditionField.RecipientCount => new(
            Condition(field, RuleConditionOperator.Equals, "2"),
            Condition(field, RuleConditionOperator.Equals, "5")),

        RuleConditionField.MessageSizeBytes => new(
            Condition(field, RuleConditionOperator.GreaterThan, "100"),
            Condition(field, RuleConditionOperator.GreaterThan, "99999999")),

        RuleConditionField.ClientIp => new(
            Condition(field, RuleConditionOperator.InIpRange, "10.20.0.0/16"),
            Condition(field, RuleConditionOperator.InIpRange, "192.168.0.0/16")),

        RuleConditionField.AuthUser => new(
            Condition(field, RuleConditionOperator.Equals, "relay-user"),
            Condition(field, RuleConditionOperator.Equals, "another-user")),

        RuleConditionField.ListenerPort => new(
            Condition(field, RuleConditionOperator.Equals, "587"),
            Condition(field, RuleConditionOperator.Equals, "25")),

        // A boolean field is false for this context only when the condition is inverted; the
        // session it runs against has both flags set.
        RuleConditionField.Authenticated or RuleConditionField.Tls => new(
            Condition(field, RuleConditionOperator.IsTrue),
            Condition(field, RuleConditionOperator.IsTrue, negate: true)),

        RuleConditionField.HeaderFrom => new(
            Condition(field, RuleConditionOperator.Equals, "sender@example.com"),
            Condition(field, RuleConditionOperator.Equals, "nobody@example.com")),

        RuleConditionField.HeaderTo => new(
            Condition(field, RuleConditionOperator.Equals, "rcpt@example.com"),
            Condition(field, RuleConditionOperator.Equals, "nobody@example.com")),

        RuleConditionField.HeaderCc => new(
            Condition(field, RuleConditionOperator.Equals, "cc@example.com"),
            Condition(field, RuleConditionOperator.Equals, "nobody@example.com")),

        RuleConditionField.HeaderReplyTo => new(
            Condition(field, RuleConditionOperator.Equals, "support@example.com"),
            Condition(field, RuleConditionOperator.Equals, "nobody@example.com")),

        RuleConditionField.Subject => new(
            Condition(field, RuleConditionOperator.Contains, "Quarterly"),
            Condition(field, RuleConditionOperator.Contains, "Monthly")),

        RuleConditionField.BodyText => new(
            Condition(field, RuleConditionOperator.Contains, "the plain body"),
            Condition(field, RuleConditionOperator.Contains, "not in the body")),

        RuleConditionField.BodyHtml => new(
            Condition(field, RuleConditionOperator.Contains, "the html body"),
            Condition(field, RuleConditionOperator.Contains, "not in the body")),

        RuleConditionField.Header => new(
            Condition(field, RuleConditionOperator.Equals, "erp", headerName: "X-Origin"),
            Condition(field, RuleConditionOperator.Equals, "crm", headerName: "X-Origin")),

        RuleConditionField.AttachmentName => new(
            Condition(field, RuleConditionOperator.Equals, "report.pdf"),
            Condition(field, RuleConditionOperator.Equals, "invoice.pdf")),

        RuleConditionField.AttachmentExtension => new(
            Condition(field, RuleConditionOperator.Equals, ".pdf"),
            Condition(field, RuleConditionOperator.Equals, ".exe")),

        RuleConditionField.AttachmentCount => new(
            Condition(field, RuleConditionOperator.Equals, "1"),
            Condition(field, RuleConditionOperator.Equals, "7")),

        RuleConditionField.AttachmentSizeBytes => new(
            Condition(field, RuleConditionOperator.GreaterThan, "100"),
            Condition(field, RuleConditionOperator.GreaterThan, "9999999")),

        RuleConditionField.Importance => new(
            Condition(field, RuleConditionOperator.Equals, "High"),
            Condition(field, RuleConditionOperator.Equals, "Low")),

        // Protection needs its own message; the rich one is neither signed nor encrypted, which
        // is exactly the non-matching case.
        RuleConditionField.IsSigned => new(
            Condition(field, RuleConditionOperator.IsTrue),
            Condition(field, RuleConditionOperator.IsTrue),
            MatchContext: () => Context(ProtectedMessage("signed", "application/pkcs7-signature"))),

        RuleConditionField.IsEncrypted => new(
            Condition(field, RuleConditionOperator.IsTrue),
            Condition(field, RuleConditionOperator.IsTrue),
            MatchContext: () => Context(ProtectedMessage("encrypted", "application/pgp-encrypted"))),

        _ => throw new NotSupportedException(
            $"No test case for RuleConditionField.{field}. Add one here when adding a field — "
            + "a field nobody has ever evaluated is a field an operator can configure and never see work."),
    };

    [Fact]
    public void EveryConditionField_IsSupportedByAtLeastOneOperator()
    {
        // A field with no legal operator could be selected in the UI and would never match.
        foreach (var field in Enum.GetValues<RuleConditionField>())
            RuleConditionSchema.OperatorsFor(field).Should().NotBeEmpty($"{field} needs operators");
    }

    [Fact]
    public void EveryOperator_IsLegalForAtLeastOneField()
    {
        // The other direction: an operator no field accepts is dead code in the enum, and would
        // sit in a drop-down that never appears.
        foreach (var op in Enum.GetValues<RuleConditionOperator>())
        {
            Enum.GetValues<RuleConditionField>()
                .Any(f => RuleConditionSchema.IsSupported(f, op))
                .Should().BeTrue($"{op} is not usable with any field");
        }
    }

    // =========================================================================
    // Action types
    // =========================================================================

    public static IEnumerable<object[]> EveryActionType()
        => Enum.GetValues<RuleActionType>().Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(EveryActionType))]
    public void EveryActionType_HasAnEffect(RuleActionType type)
    {
        var action = ActionFor(type);

        if (RuleActionSchema.IsTerminal(type))
        {
            // Reject and Discard are decided by the processor, not applied to the message.
            var outcome = MessageRuleProcessor.Run(
                Options(Rule(actions: [action])), RichContext(), RulePolicyLimits.None);

            outcome.Verdict.Should().NotBe(RuleVerdict.Continue, $"{type} decides the message's fate");
            return;
        }

        var ctx = RichContext();
        var effect = MessageRuleActions.Apply(action, ctx);

        (effect.Changed || effect.EnvelopeChanged).Should().BeTrue(
            $"{type} should change something — it reported '{effect.Detail}'"
            + (effect.Warning is null ? string.Empty : $" (warning: {effect.Warning})"));
    }

    [Theory]
    [MemberData(nameof(EveryActionType))]
    public void EveryActionType_IsAcceptedByTheValidator(RuleActionType type)
    {
        // The action the coverage test exercises must also be one an operator could configure —
        // otherwise the test proves the runtime works on something the tool would refuse to save.
        var options = Options(Rule(actions: [ActionFor(type)]));

        MessageRuleEvaluator.FindProblems(options)
            .Where(p => p.IsError)
            .Should().BeEmpty($"the sample {type} action should be valid");
    }

    [Theory]
    [MemberData(nameof(EveryActionType))]
    public void EveryActionType_HasAReadableDescription(RuleActionType type)
    {
        // The description is what the rule grid and the log show; an action falling through to
        // its bare enum name means the grid says nothing useful about it.
        var description = MessageRuleEvaluator.Describe(ActionFor(type));

        description.Should().NotBeNullOrWhiteSpace();

        // Discard is the exception: it carries no parameters, so its name is the whole story.
        if (type is not RuleActionType.Discard)
            description.Should().NotBe(type.ToString(), $"{type} needs a description of its own");
    }

    /// <summary>
    /// One usable example per action type. The <c>default</c> arm guards the enum the same way
    /// the condition table does.
    /// </summary>
    private static RuleAction ActionFor(RuleActionType type) => type switch
    {
        RuleActionType.Reject => new() { Type = type, Value = "not accepted", SmtpCode = 550 },
        RuleActionType.Discard => new() { Type = type },

        RuleActionType.AddRecipient => new() { Type = type, Recipient = RecipientKind.Bcc, Value = "archive@example.com" },
        RuleActionType.RemoveRecipient => new() { Type = type, Match = "@partner.test" },
        RuleActionType.ReplaceRecipient => new() { Type = type, Match = "rcpt@example.com", Value = "new@example.com" },

        RuleActionType.SetSubject => new() { Type = type, Value = "Replaced subject" },
        RuleActionType.PrefixSubject => new() { Type = type, Value = "[EXTERNAL] " },
        RuleActionType.SuffixSubject => new() { Type = type, Value = " (unverified)" },

        RuleActionType.PrependBody => new() { Type = type, Value = "BANNER" },
        RuleActionType.AppendBody => new() { Type = type, Value = "DISCLAIMER" },

        RuleActionType.SetHeader => new() { Type = type, HeaderName = "X-Origin", Value = "relay" },
        RuleActionType.AddHeader => new() { Type = type, HeaderName = "X-Policy", Value = "external" },
        RuleActionType.RemoveHeader => new() { Type = type, HeaderName = "X-Origin" },

        RuleActionType.RemoveAttachments => new()
        {
            Type = type,
            AttachmentMatch = AttachmentMatchMode.Extension,
            Value = ".pdf",
        },

        RuleActionType.SetImportance => new() { Type = type, Value = "Low" },
        RuleActionType.SetFrom => new() { Type = type, Value = "relay@example.com" },
        RuleActionType.SetReplyTo => new() { Type = type, Value = "support@example.com" },

        _ => throw new NotSupportedException(
            $"No test case for RuleActionType.{type}. Add one here when adding an action — "
            + "an action nobody has ever applied is an action an operator can configure and never see work."),
    };

    [Fact]
    public void EveryActionType_DeclaresItsParameters()
    {
        // An action whose schema entry is missing would silently accept no parameters, and the
        // editor would show an empty form for it.
        foreach (var type in Enum.GetValues<RuleActionType>())
        {
            if (type is RuleActionType.Discard) continue;   // genuinely takes nothing

            RuleActionSchema.Used(type).Should().NotBe(RuleActionParam.None,
                $"{type} should declare the properties it uses");
        }
    }
}
