using System.Text;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;
using static GraphMailer.Tests.Unit.Infrastructure.Rules.RuleTestFactory;

namespace GraphMailer.Tests.Unit.Infrastructure.Rules;

/// <summary>
/// The engine: evaluation order, the two modes, the verdicts, and the post-conditions a
/// rewritten message has to survive.
///
/// The audit cases are the heart of it. Audit applies nothing but still honours StopProcessing
/// and still stops at an action that would reject or discard — that is what makes an audit run
/// flow-identical to the enforce run it is supposed to predict. If audit ignored those, flipping
/// one rule to Enforce would silently change which <i>later</i> rules ran.
/// </summary>
public sealed class MessageRuleProcessorTests
{
    private static MessageRuleOutcome Run(
        MessageRulesOptions options, MessageRuleContext ctx, RulePolicyLimits? limits = null)
        => MessageRuleProcessor.Run(options, ctx, limits ?? RulePolicyLimits.None);

    private static RuleAction Prefix(string value = "[TAG] ")
        => new() { Type = RuleActionType.PrefixSubject, Value = value };

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private static MessageRuleProcessor Processor(
        MessageRulesOptions? options = null,
        SmtpOptions? smtp = null,
        SmtpAccessOptions? access = null)
        => new(Monitor(options ?? new MessageRulesOptions()),
               Monitor(smtp ?? new SmtpOptions()),
               Monitor(access ?? new SmtpAccessOptions()),
               NullLogger<MessageRuleProcessor>.Instance);

    // =========================================================================
    // Evaluation order and StopProcessing
    // =========================================================================

    [Fact]
    public void Run_AppliesRulesInArrayOrder()
    {
        var ctx = Context(TextMessage(subject: "Base"));
        var options = Options(
            Rule(name: "first", actions: [Prefix("A")]),
            Rule(name: "second", actions: [Prefix("B")]));

        var outcome = Run(options, ctx);

        ctx.Message.Subject.Should().Be("BABase", "the second rule prefixes what the first produced");
        outcome.Matched.Select(m => m.Name).Should().Equal("first", "second");
    }

    [Fact]
    public void Run_StopProcessing_PreventsLaterRules()
    {
        var ctx = Context(TextMessage(subject: "Base"));
        var options = Options(
            Rule(name: "first", stopProcessing: true, actions: [Prefix("A")]),
            Rule(name: "second", actions: [Prefix("B")]));

        var outcome = Run(options, ctx);

        ctx.Message.Subject.Should().Be("ABase");
        outcome.Matched.Should().ContainSingle().Which.Name.Should().Be("first");
    }

    [Fact]
    public void Run_StopProcessing_IsHonouredInAuditModeToo()
    {
        // Otherwise switching the first rule to Enforce would silently change whether the second
        // rule ran, and the audit would have predicted the wrong flow.
        var ctx = Context(TextMessage(subject: "Base"));
        var options = Options(
            Rule(name: "first", mode: MessageRuleMode.Audit, stopProcessing: true, actions: [Prefix("A")]),
            Rule(name: "second", actions: [Prefix("B")]));

        var outcome = Run(options, ctx);

        ctx.Message.Subject.Should().Be("Base", "audit changes nothing");
        outcome.Matched.Should().ContainSingle().Which.Name.Should().Be("first");
    }

    [Fact]
    public void Run_DisabledRule_IsSkipped()
    {
        var ctx = Context(TextMessage(subject: "Base"));
        var options = Options(Rule(enabled: false, actions: [Prefix("A")]));

        var outcome = Run(options, ctx);

        outcome.Matched.Should().BeEmpty();
        ctx.Message.Subject.Should().Be("Base");
    }

    [Fact]
    public void Run_GloballyDisabled_DoesNothing()
    {
        var ctx = Context(TextMessage(subject: "Base"));
        var options = new MessageRulesOptions { Enabled = false, Rules = [Rule(actions: [Prefix("A")])] };

        var outcome = Run(options, ctx);

        outcome.Matched.Should().BeEmpty();
        outcome.MessageChanged.Should().BeFalse();
        ctx.Message.Subject.Should().Be("Base");
    }

    // =========================================================================
    // Audit mode
    // =========================================================================

    [Fact]
    public void Run_AuditMode_RecordsEverythingAndChangesNothing()
    {
        var ctx = Context(TextMessage(subject: "Base"));
        var options = Options(Rule(mode: MessageRuleMode.Audit, actions: [Prefix("[TAG] ")]));

        var outcome = Run(options, ctx);

        ctx.Message.Subject.Should().Be("Base");
        outcome.MessageChanged.Should().BeFalse();
        outcome.Matched.Should().ContainSingle()
            .Which.Outcome.Should().Be(RuleOutcomes.Modified, "the rule would have changed the message");
        outcome.Actions.Should().ContainSingle().Which.Applied.Should().BeFalse();
    }

    [Fact]
    public void Run_AuditMode_StopsAtAWouldBeReject()
    {
        var ctx = Context(TextMessage(subject: "Base"));
        var options = Options(
            Rule(name: "would-reject", mode: MessageRuleMode.Audit,
                actions: [new RuleAction { Type = RuleActionType.Reject }]),
            Rule(name: "later", actions: [Prefix("B")]));

        var outcome = Run(options, ctx);

        outcome.Verdict.Should().Be(RuleVerdict.Continue, "audit never actually rejects");
        outcome.Matched.Should().ContainSingle().Which.Outcome.Should().Be(RuleOutcomes.Rejected);
        ctx.Message.Subject.Should().Be("Base", "the later rule did not run either");
    }

    // =========================================================================
    // Verdicts
    // =========================================================================

    [Fact]
    public void Run_Reject_CarriesTheConfiguredCodeAndText()
    {
        var ctx = Context();
        var options = Options(Rule(name: "block", actions:
        [
            new RuleAction { Type = RuleActionType.Reject, SmtpCode = 554, Value = "Not accepted here" },
        ]));

        var outcome = Run(options, ctx);

        outcome.Verdict.Should().Be(RuleVerdict.Reject);
        outcome.SmtpCode.Should().Be(554);
        outcome.SmtpText.Should().Be("Not accepted here");
        outcome.DecidingRule.Should().Be("block");
    }

    [Fact]
    public void Run_Reject_WithoutCode_UsesThePermanentDefault()
    {
        var ctx = Context();
        var options = Options(Rule(actions: [new RuleAction { Type = RuleActionType.Reject }]));

        var outcome = Run(options, ctx);

        outcome.SmtpCode.Should().Be(550);
        outcome.SmtpText.Should().Be("Message rejected by policy");
    }

    [Fact]
    public void Run_Discard_ReportsTheDiscardVerdict()
    {
        var ctx = Context();
        var options = Options(Rule(name: "sink", actions: [new RuleAction { Type = RuleActionType.Discard }]));

        var outcome = Run(options, ctx);

        outcome.Verdict.Should().Be(RuleVerdict.Discard);
        outcome.DecidingRule.Should().Be("sink");
        outcome.Matched.Should().ContainSingle().Which.Outcome.Should().Be(RuleOutcomes.Discarded);
    }

    [Fact]
    public void Run_TerminalAction_StopsLaterActionsInTheSameRule()
    {
        var ctx = Context(TextMessage(subject: "Base"));
        var options = Options(Rule(actions:
        [
            new RuleAction { Type = RuleActionType.Reject },
            Prefix("never"),
        ]));

        Run(options, ctx);

        ctx.Message.Subject.Should().Be("Base");
    }

    // =========================================================================
    // Reject text is attacker-visible and operator-authored
    // =========================================================================

    [Theory]
    [InlineData("ok\r\n250 injected", "ok250 injected")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("", "Message rejected by policy")]
    [InlineData("   ", "Message rejected by policy")]
    public void SanitiseReplyText_StripsControlCharactersAndFallsBack(string input, string expected)
    {
        MessageRuleProcessor.SanitiseReplyText(input).Should().Be(expected);
    }

    [Fact]
    public void SanitiseReplyText_OverlongText_IsCapped()
    {
        var text = new string('x', 500);

        MessageRuleProcessor.SanitiseReplyText(text).Should().HaveLength(200);
    }

    [Theory]
    [InlineData(null, 550)]
    [InlineData(250, 550)]
    [InlineData(399, 550)]
    [InlineData(600, 550)]
    [InlineData(451, 451)]
    [InlineData(554, 554)]
    public void NormaliseCode_KeepsOnlyRejectionCodes(int? configured, int expected)
    {
        MessageRuleProcessor.NormaliseCode(configured).Should().Be(expected);
    }

    // =========================================================================
    // Signed and encrypted mail
    // =========================================================================

    [Theory]
    [InlineData("signed", "application/pkcs7-signature")]
    [InlineData("signed", "application/pgp-signature")]
    [InlineData("encrypted", "application/pgp-encrypted")]
    public void Run_ProtectedMessage_SkipsBodyActionsButAppliesTheRest(string subtype, string protocol)
    {
        var ctx = Context(ProtectedMessage(subtype, protocol));
        var options = Options(Rule(name: "disclaimer", actions:
        [
            new RuleAction { Type = RuleActionType.PrependBody, Value = "BANNER" },
            Prefix("[EXT] "),
            new RuleAction { Type = RuleActionType.AddHeader, HeaderName = "X-Policy", Value = "external" },
        ]));

        var outcome = Run(options, ctx);

        ctx.Message.Subject.Should().StartWith("[EXT] ", "the subject is not covered by the signature");
        ctx.Message.Headers["X-Policy"].Should().Be("external");
        outcome.Actions.Should().Contain(a => a.Type == RuleActionType.PrependBody && !a.Applied);
        outcome.Warnings.Should().Contain(w => w.Contains("PrependBody") && w.Contains("skipped"));
    }

    [Fact]
    public void Run_ProtectedMessage_StillRejects()
    {
        // A rule that refuses the message must not be neutered by the message being signed.
        var ctx = Context(ProtectedMessage("signed", "application/pkcs7-signature"));
        var options = Options(Rule(actions: [new RuleAction { Type = RuleActionType.Reject }]));

        Run(options, ctx).Verdict.Should().Be(RuleVerdict.Reject);
    }

    [Fact]
    public void Run_ProtectedMessage_SkipsAttachmentRemoval()
    {
        var ctx = Context(ProtectedMessage("signed", "application/pkcs7-signature"));
        var options = Options(Rule(actions:
        [
            new RuleAction
            {
                Type = RuleActionType.RemoveAttachments,
                AttachmentMatch = AttachmentMatchMode.Extension,
                Value = ".p7s",
            },
        ]));

        var outcome = Run(options, ctx);

        outcome.Actions.Should().ContainSingle().Which.Applied.Should().BeFalse();
        outcome.Matched.Should().ContainSingle().Which.Outcome.Should().Be(RuleOutcomes.Skipped,
            "a rule that matched but could do nothing is not the same as a dead rule");
    }

    // =========================================================================
    // Post-conditions
    // =========================================================================

    [Fact]
    public void Run_EveryRecipientRemoved_DiscardsInsteadOfQueueing()
    {
        // Queueing a recipient-less message would only produce a Graph failure and an NDR to a
        // sender who did nothing wrong.
        var ctx = Context(recipients: ["rcpt@example.com"]);
        var options = Options(Rule(actions:
            [new RuleAction { Type = RuleActionType.RemoveRecipient, Match = "@example.com" }]));

        var outcome = Run(options, ctx);

        outcome.Verdict.Should().Be(RuleVerdict.Discard);
        outcome.Warnings.Should().Contain(w => w.Contains("every recipient was removed"));
    }

    [Fact]
    public void Run_TooManyRecipientsAfterRules_RejectsAtData()
    {
        // Without this the message is accepted here and fails permanently in GraphApiClient
        // hours later, so the sender gets a late NDR instead of an immediate SMTP error.
        var ctx = Context(recipients: ["a@example.com"]);
        var options = Options(Rule(actions:
        [
            new RuleAction { Type = RuleActionType.AddRecipient, Recipient = RecipientKind.Bcc, Value = "b@example.com" },
            new RuleAction { Type = RuleActionType.AddRecipient, Recipient = RecipientKind.Bcc, Value = "c@example.com" },
        ]));

        var outcome = Run(options, ctx, new RulePolicyLimits { MaxRecipients = 2 });

        outcome.Verdict.Should().Be(RuleVerdict.Reject);
        outcome.SmtpCode.Should().Be(554);
        outcome.SmtpText.Should().Contain("Too many recipients");
    }

    [Fact]
    public void Run_SetFromToABlockedSender_IsRejected()
    {
        // The sender policy ran at MAIL FROM against the original address. A rule that rewrites
        // the sender would otherwise walk straight past it.
        var ctx = Context(TextMessage(from: "ok@example.com"), envelopeFrom: "ok@example.com");
        var options = Options(Rule(name: "rewrite", actions:
            [new RuleAction { Type = RuleActionType.SetFrom, Value = "spoofed@blocked.test" }]));
        var limits = new RulePolicyLimits { BlockedSenders = ["@blocked.test"] };

        var outcome = Run(options, ctx, limits);

        outcome.Verdict.Should().Be(RuleVerdict.Reject);
        outcome.SmtpCode.Should().Be(550);
        outcome.SmtpText.Should().Be("Sender address not permitted");
        outcome.Warnings.Should().Contain(w => w.Contains("spoofed@blocked.test"));
    }

    [Fact]
    public void Run_SetFromToAPermittedSender_Proceeds()
    {
        var ctx = Context(TextMessage(from: "ok@example.com"), envelopeFrom: "ok@example.com");
        var options = Options(Rule(actions:
            [new RuleAction { Type = RuleActionType.SetFrom, Value = "relay@example.com" }]));
        var limits = new RulePolicyLimits { AllowedSenders = ["@example.com"] };

        var outcome = Run(options, ctx, limits);

        outcome.Verdict.Should().Be(RuleVerdict.Continue);
        outcome.EnvelopeFromChanged.Should().BeTrue();
        ctx.EnvelopeFrom.Should().Be("relay@example.com");
    }

    [Fact]
    public void Run_TooManyCustomHeaders_Warns()
    {
        // The count depends on the incoming message, so no config-time check can catch it —
        // and exceeding it makes Graph drop every custom header plus the Sender.
        var ctx = Context();
        var options = Options(Rule(actions:
        [
            new RuleAction { Type = RuleActionType.AddHeader, HeaderName = "X-A", Value = "1" },
            new RuleAction { Type = RuleActionType.AddHeader, HeaderName = "X-B", Value = "2" },
            new RuleAction { Type = RuleActionType.AddHeader, HeaderName = "X-C", Value = "3" },
            new RuleAction { Type = RuleActionType.AddHeader, HeaderName = "X-D", Value = "4" },
            new RuleAction { Type = RuleActionType.AddHeader, HeaderName = "X-E", Value = "5" },
            new RuleAction { Type = RuleActionType.AddHeader, HeaderName = "X-F", Value = "6" },
        ]));

        var outcome = Run(options, ctx);

        outcome.Warnings.Should().Contain(w => w.Contains("custom 'x-' headers"));
    }

    // =========================================================================
    // Apply: bytes in, bytes out
    // =========================================================================

    [Fact]
    public void Apply_NoRuleMatches_ReturnsTheSameByteArrayInstance()
    {
        var eml = Serialise(TextMessage());
        var processor = Processor(Options(Rule(
            conditions: [Condition(RuleConditionField.Subject, RuleConditionOperator.Equals, "no such subject")],
            actions: [Prefix()])));

        var result = processor.Apply(eml, "s@example.com", ["r@example.com"], Session(), "id", default);

        result.Eml.Should().BeSameAs(eml, "an unchanged message must not be re-serialised");
        result.Outcome.Verdict.Should().Be(RuleVerdict.Continue);
    }

    [Fact]
    public void Apply_RulesDisabled_ReturnsTheSameByteArrayInstance()
    {
        var eml = Serialise(TextMessage());
        var processor = Processor();

        var result = processor.Apply(eml, "s@example.com", ["r@example.com"], Session(), "id", default);

        result.Eml.Should().BeSameAs(eml);
    }

    [Fact]
    public void Apply_MessageChanged_ReSerialisesWithCrLfAndRoundTrips()
    {
        var eml = Serialise(TextMessage(subject: "Base"));
        var processor = Processor(Options(Rule(actions: [Prefix("[TAG] ")])));

        var result = processor.Apply(eml, "s@example.com", ["r@example.com"], Session(), "id", default);

        result.Eml.Should().NotBeSameAs(eml);
        var text = Encoding.UTF8.GetString(result.Eml);
        text.Should().Contain("\r\n").And.NotMatchRegex("(?<!\r)\n");

        MimeMessage.Load(new MemoryStream(result.Eml)).Subject.Should().Be("[TAG] Base");
    }

    [Fact]
    public void Apply_EnvelopeChanged_ReturnsTheNewRecipientList()
    {
        var eml = Serialise(TextMessage());
        var processor = Processor(Options(Rule(actions:
            [new RuleAction { Type = RuleActionType.AddRecipient, Recipient = RecipientKind.Bcc, Value = "archive@example.com" }])));

        var result = processor.Apply(eml, "s@example.com", ["r@example.com"], Session(), "id", default);

        result.Recipients.Should().BeEquivalentTo(["r@example.com", "archive@example.com"]);
    }

    [Fact]
    public void Apply_UnparsableMessage_SkipsEveryRuleAndReturnsItUntouched()
    {
        // Fail open: a message the parser cannot make sense of is delivered as it arrived,
        // rather than being refused because the rule engine could not look at it.
        var garbage = Encoding.ASCII.GetBytes(new string('\0', 64));
        var processor = Processor(Options(Rule(actions: [new RuleAction { Type = RuleActionType.Reject }])));

        var result = processor.Apply(garbage, "s@example.com", ["r@example.com"], Session(), "id", default);

        result.Outcome.Verdict.Should().Be(RuleVerdict.Continue);
        result.Eml.Should().BeSameAs(garbage);
    }

    [Fact]
    public void Apply_ProcessorFailure_DeliversTheMessageUnmodified()
    {
        // A bug in the rule engine must never become a mail outage. The options monitor throwing
        // stands in for any unexpected failure inside Apply.
        var eml = Serialise(TextMessage());
        var options = Substitute.For<IOptionsMonitor<MessageRulesOptions>>();
        options.CurrentValue.Returns(Options(Rule(actions: [Prefix()])));

        var smtp = Substitute.For<IOptionsMonitor<SmtpOptions>>();
        smtp.CurrentValue.Returns(_ => throw new InvalidOperationException("boom"));

        var processor = new MessageRuleProcessor(
            options, smtp, Monitor(new SmtpAccessOptions()), NullLogger<MessageRuleProcessor>.Instance);

        var result = processor.Apply(eml, "s@example.com", ["r@example.com"], Session(), "id", default);

        result.Outcome.Verdict.Should().Be(RuleVerdict.Continue);
        result.Eml.Should().BeSameAs(eml);
        result.Recipients.Should().BeEquivalentTo(["r@example.com"]);
    }

    [Fact]
    public void IsActive_ReflectsWhetherAnythingWouldRun()
    {
        Processor().IsActive.Should().BeFalse("no rules configured");
        Processor(new MessageRulesOptions { Enabled = false, Rules = [Rule()] }).IsActive.Should().BeFalse();
        Processor(new MessageRulesOptions { Enabled = true, Rules = [Rule(enabled: false)] }).IsActive.Should().BeFalse();
        Processor(Options(Rule())).IsActive.Should().BeTrue();
    }

    // =========================================================================
    // Repeated runs against the same source
    // =========================================================================

    [Fact]
    public void Run_TwiceOverTheSameBytes_ProducesTheSameResult()
    {
        // The reported defect: the rule tester held a parsed message, the first run mutated it,
        // and the second silently saw a different (or empty) message — an attachment rule then
        // stopped matching and looked broken. Re-parsing from the bytes is what makes a repeated
        // run answer the same question.
        var eml = Serialise(WithAttachments(null, ("macro.docm", "application/msword", 512)));

        var options = Options(Rule(name: "strip", actions:
        [
            new RuleAction
            {
                Type = RuleActionType.RemoveAttachments,
                AttachmentMatch = AttachmentMatchMode.Extension,
                Value = ".docm",
            },
        ]));

        static MessageRuleContext Fresh(byte[] bytes) =>
            MessageRuleContext.Create(bytes, "sender@example.com", ["rcpt@example.com"], Session());

        var first = Run(options, Fresh(eml));
        var second = Run(options, Fresh(eml));

        first.Matched.Should().ContainSingle();
        second.Matched.Should().ContainSingle();
        first.Actions[0].Detail.Should().Be(second.Actions[0].Detail,
            "a second run over the same bytes must remove the same attachment");
        second.MessageChanged.Should().BeTrue();
    }

    [Fact]
    public void Run_AttachmentRemoval_MatchesAMessageBuiltLikeTheTesterBuildsOne()
    {
        // Guards the seam between the tester's sample message and the engine: an attachment the
        // tester creates has to be one the splitter — and therefore a rule — actually sees.
        var message = WithAttachments(
            new MimeKit.TextPart("plain") { Text = "body" },
            ("macro.docm", "application/msword", 512));

        var ctx = ContextFromBytes(message);

        var outcome = Run(Options(Rule(actions:
        [
            new RuleAction
            {
                Type = RuleActionType.RemoveAttachments,
                AttachmentMatch = AttachmentMatchMode.Extension,
                Value = ".docm",
            },
        ])), ctx);

        outcome.MessageChanged.Should().BeTrue();
        MessageRuleEvaluator.AttachmentNames(ctx).Should().BeEmpty();
    }

    // =========================================================================
    // Explaining a run
    // =========================================================================

    [Fact]
    public void Run_WithoutExplain_RecordsNothingExtra()
    {
        // The mail path has no use for it, so it must not pay for it.
        var options = Options(Rule(name: "never", conditions:
            [Condition(RuleConditionField.Subject, RuleConditionOperator.Equals, "nope")],
            actions: [Prefix()]));

        Run(options, Context()).Evaluated.Should().BeEmpty();
    }

    [Fact]
    public void Run_Explain_NamesTheConditionThatDidNotMatch()
    {
        // The reported situation: a rule is configured, the tester shows nothing about it, and
        // there is no way to tell whether the rule is wrong or the message simply does not fit.
        var options = Options(Rule(name: "strip xml", conditions:
        [
            Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, ".xml"),
        ], actions: [Prefix()]));

        var outcome = MessageRuleProcessor.Run(
            options, Context(WithAttachments(null, ("report.pdf", "application/pdf", 128))),
            RulePolicyLimits.None, explain: true);

        var evaluation = outcome.Evaluated.Should().ContainSingle().Subject;
        evaluation.Status.Should().Be(RuleEvaluationStatus.NotMatched);
        evaluation.Reason.Should().Contain("AttachmentExtension").And.Contain(".xml");
    }

    [Fact]
    public void Run_Explain_MatchingRuleIsReportedAsMatched()
    {
        var options = Options(Rule(name: "strip xml", conditions:
        [
            Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, ".xml"),
        ], actions: [Prefix()]));

        var outcome = MessageRuleProcessor.Run(
            options, Context(WithAttachments(null, ("data.xml", "application/xml", 128))),
            RulePolicyLimits.None, explain: true);

        outcome.Evaluated.Should().ContainSingle()
            .Which.Status.Should().Be(RuleEvaluationStatus.Matched);
    }

    [Fact]
    public void Run_Explain_DisabledRuleSaysSo()
    {
        var options = Options(Rule(name: "off", enabled: false, actions: [Prefix()]));

        var outcome = MessageRuleProcessor.Run(options, Context(), RulePolicyLimits.None, explain: true);

        var evaluation = outcome.Evaluated.Should().ContainSingle().Subject;
        evaluation.Status.Should().Be(RuleEvaluationStatus.Disabled);
        evaluation.Reason.Should().Contain("switched off");
    }

    [Fact]
    public void Run_Explain_RuleAfterAStopIsReportedAsNotReached()
    {
        // The other answer to "why did my rule do nothing" — it never ran at all.
        var options = Options(
            Rule(name: "first", stopProcessing: true, actions: [Prefix("A")]),
            Rule(name: "second", actions: [Prefix("B")]));

        var outcome = MessageRuleProcessor.Run(
            options, Context(TextMessage(subject: "Base")), RulePolicyLimits.None, explain: true);

        outcome.Evaluated.Should().HaveCount(2);
        outcome.Evaluated[0].Status.Should().Be(RuleEvaluationStatus.Matched);
        outcome.Evaluated[1].Status.Should().Be(RuleEvaluationStatus.NotReached);
        outcome.Evaluated[1].Reason.Should().Contain("ended the run");
    }

    [Fact]
    public void Run_Explain_DoesNotChangeWhatTheRulesDo()
    {
        // The explanation is reporting, never behaviour: the same run with and without it must
        // produce the same message.
        var options = Options(
            Rule(name: "first", stopProcessing: true, actions: [Prefix("A")]),
            Rule(name: "second", actions: [Prefix("B")]));

        var plain = Context(TextMessage(subject: "Base"));
        var explained = Context(TextMessage(subject: "Base"));

        var plainOutcome = MessageRuleProcessor.Run(options, plain, RulePolicyLimits.None);
        var explainedOutcome = MessageRuleProcessor.Run(options, explained, RulePolicyLimits.None, explain: true);

        plain.Message.Subject.Should().Be(explained.Message.Subject);
        plainOutcome.Matched.Should().BeEquivalentTo(explainedOutcome.Matched);
        plainOutcome.Verdict.Should().Be(explainedOutcome.Verdict);
    }

    [Fact]
    public void Run_Explain_MatchAny_SaysNoneOfTheConditionsMatched()
    {
        var options = Options(Rule(name: "any", match: ConditionMatch.Any, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "alpha"),
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "beta"),
        ], actions: [Prefix()]));

        var outcome = MessageRuleProcessor.Run(
            options, Context(TextMessage(subject: "gamma")), RulePolicyLimits.None, explain: true);

        outcome.Evaluated.Should().ContainSingle()
            .Which.Reason.Should().Contain("none of the conditions matched");
    }

    [Fact]
    public void Run_Explain_ReportsEveryRuleInOrder()
    {
        var options = Options(
            Rule(name: "matches", actions: [Prefix("A")]),
            Rule(name: "does not", conditions:
                [Condition(RuleConditionField.Subject, RuleConditionOperator.Equals, "nope")],
                actions: [Prefix("B")]),
            Rule(name: "disabled", enabled: false, actions: [Prefix("C")]));

        var outcome = MessageRuleProcessor.Run(
            options, Context(TextMessage(subject: "Base")), RulePolicyLimits.None, explain: true);

        outcome.Evaluated.Select(r => r.Name).Should().Equal("matches", "does not", "disabled");
    }
}
