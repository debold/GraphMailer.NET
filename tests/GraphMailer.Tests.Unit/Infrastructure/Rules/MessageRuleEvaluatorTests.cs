using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Rules;
using MimeKit;
using static GraphMailer.Tests.Unit.Infrastructure.Rules.RuleTestFactory;

namespace GraphMailer.Tests.Unit.Infrastructure.Rules;

/// <summary>
/// Condition matching: the field/operator matrix, negation over multi-valued fields, and the
/// guards that keep a broken condition from becoming a mail outage.
///
/// The negation cases matter most. Multi-valued fields match existentially and Negate is applied
/// afterwards, so "no recipient at example.com" is expressible — the reading operators expect.
/// Applying Negate per value instead would silently mean "at least one recipient elsewhere",
/// which is a completely different policy.
/// </summary>
public sealed class MessageRuleEvaluatorTests
{
    private const int Timeout = 100;

    private static bool Match(RuleCondition condition, MessageRuleContext ctx)
        => MessageRuleEvaluator.Matches(condition, ctx, Timeout);

    // =========================================================================
    // Text and address operators
    // =========================================================================

    [Theory]
    [InlineData(RuleConditionOperator.Equals, "Quarterly report", true)]
    [InlineData(RuleConditionOperator.Equals, "quarterly report", true)]   // case-insensitive by default
    [InlineData(RuleConditionOperator.Equals, "Quarterly", false)]
    [InlineData(RuleConditionOperator.Contains, "terly rep", true)]
    [InlineData(RuleConditionOperator.StartsWith, "Quarter", true)]
    [InlineData(RuleConditionOperator.EndsWith, "report", true)]
    [InlineData(RuleConditionOperator.Matches, "Quarterly*", true)]
    [InlineData(RuleConditionOperator.Matches, "*report", true)]
    [InlineData(RuleConditionOperator.Matches, "Q?arterly report", true)]
    [InlineData(RuleConditionOperator.Matches, "Monthly*", false)]
    [InlineData(RuleConditionOperator.RegexMatches, @"^Quarterly\s+report$", true)]
    [InlineData(RuleConditionOperator.RegexMatches, @"^Monthly", false)]
    public void Matches_SubjectOperators_BehaveAsDocumented(
        RuleConditionOperator op, string value, bool expected)
    {
        var ctx = Context(TextMessage(subject: "Quarterly report"));

        Match(Condition(RuleConditionField.Subject, op, value), ctx).Should().Be(expected);
    }

    [Fact]
    public void Matches_CaseSensitive_DistinguishesCase()
    {
        var ctx = Context(TextMessage(subject: "Invoice"));

        Match(Condition(RuleConditionField.Subject, RuleConditionOperator.Equals, "invoice"), ctx)
            .Should().BeTrue("comparison is case-insensitive by default");
        Match(Condition(RuleConditionField.Subject, RuleConditionOperator.Equals, "invoice", caseSensitive: true), ctx)
            .Should().BeFalse();
    }

    [Fact]
    public void Matches_Wildcard_SemicolonSeparatedAlternatives_MatchAnyOne()
    {
        var ctx = Context(TextMessage(subject: "RE: order 4711"));

        Match(Condition(RuleConditionField.Subject, RuleConditionOperator.Matches, "FW:*;RE:*"), ctx)
            .Should().BeTrue();
    }

    [Fact]
    public void Matches_DomainIs_DoesNotMatchSubdomains()
    {
        // Same semantics as the sender/recipient allow lists — an entry for a domain must not
        // quietly cover every host beneath it, which is how sender spoofing gets through.
        var atDomain = Context(TextMessage(from: "a@example.com"));
        var atSubdomain = Context(TextMessage(from: "a@sub.example.com"));

        var condition = Condition(RuleConditionField.HeaderFrom, RuleConditionOperator.DomainIs, "@example.com");

        Match(condition, atDomain).Should().BeTrue();
        Match(condition, atSubdomain).Should().BeFalse();
    }

    [Fact]
    public void Matches_Exists_And_IsEmpty_AskAboutTheFieldNotAValue()
    {
        var withSubject = Context(TextMessage(subject: "Something"));
        var withoutSubject = Context(TextMessage(subject: ""));

        Match(Condition(RuleConditionField.Subject, RuleConditionOperator.Exists), withSubject).Should().BeTrue();
        Match(Condition(RuleConditionField.Subject, RuleConditionOperator.Exists), withoutSubject).Should().BeFalse();
        Match(Condition(RuleConditionField.Subject, RuleConditionOperator.IsEmpty), withoutSubject).Should().BeTrue();
        Match(Condition(RuleConditionField.Subject, RuleConditionOperator.IsEmpty), withSubject).Should().BeFalse();
    }

    // =========================================================================
    // Multi-valued fields and negation
    // =========================================================================

    [Fact]
    public void Matches_MultiValuedField_IsExistential()
    {
        var ctx = Context(recipients: ["a@example.com", "b@partner.test"]);

        Match(Condition(RuleConditionField.EnvelopeRecipient, RuleConditionOperator.DomainIs, "@partner.test"), ctx)
            .Should().BeTrue("one recipient is enough");
    }

    [Fact]
    public void Matches_NegatedMultiValuedField_MeansNoValueMatches()
    {
        var mixed = Context(recipients: ["a@example.com", "b@partner.test"]);
        var internalOnly = Context(recipients: ["a@example.com", "b@example.com"]);

        var noPartner = Condition(
            RuleConditionField.EnvelopeRecipient, RuleConditionOperator.DomainIs, "@partner.test", negate: true);

        Match(noPartner, mixed).Should().BeFalse("one recipient is at the domain");
        Match(noPartner, internalOnly).Should().BeTrue("no recipient is at the domain");
    }

    [Fact]
    public void Matches_HeaderField_ChecksEveryOccurrence()
    {
        var message = TextMessage();
        message.Headers.Add("X-Tag", "alpha");
        message.Headers.Add("X-Tag", "beta");
        var ctx = Context(message);

        Match(Condition(RuleConditionField.Header, RuleConditionOperator.Equals, "beta", headerName: "X-Tag"), ctx)
            .Should().BeTrue();
        Match(Condition(RuleConditionField.Header, RuleConditionOperator.Equals, "gamma", headerName: "X-Tag"), ctx)
            .Should().BeFalse();
    }

    [Fact]
    public void Matches_HeaderField_WithoutHeaderName_NeverMatches()
    {
        var ctx = Context();

        Match(Condition(RuleConditionField.Header, RuleConditionOperator.Exists), ctx).Should().BeFalse();
    }

    // =========================================================================
    // Numbers, booleans, IP ranges
    // =========================================================================

    [Theory]
    [InlineData(RuleConditionOperator.GreaterThan, "1", true)]
    [InlineData(RuleConditionOperator.GreaterThan, "2", false)]
    [InlineData(RuleConditionOperator.LessThan, "3", true)]
    [InlineData(RuleConditionOperator.Equals, "2", true)]
    public void Matches_RecipientCount_ComparesNumerically(
        RuleConditionOperator op, string value, bool expected)
    {
        var ctx = Context(recipients: ["a@example.com", "b@example.com"]);

        Match(Condition(RuleConditionField.RecipientCount, op, value), ctx).Should().Be(expected);
    }

    [Fact]
    public void Matches_NumericFieldWithNonNumericValue_NeverMatches()
    {
        var ctx = Context(recipients: ["a@example.com"]);

        Match(Condition(RuleConditionField.RecipientCount, RuleConditionOperator.GreaterThan, "many"), ctx)
            .Should().BeFalse();
    }

    [Fact]
    public void Matches_BooleanFields_UseIsTrue()
    {
        var authenticated = Context(session: Session(authenticated: true, tls: true));
        var anonymous = Context(session: Session());

        Match(Condition(RuleConditionField.Authenticated, RuleConditionOperator.IsTrue), authenticated).Should().BeTrue();
        Match(Condition(RuleConditionField.Authenticated, RuleConditionOperator.IsTrue), anonymous).Should().BeFalse();
        Match(Condition(RuleConditionField.Tls, RuleConditionOperator.IsTrue), authenticated).Should().BeTrue();

        // Negation is the way to express "not authenticated".
        Match(Condition(RuleConditionField.Authenticated, RuleConditionOperator.IsTrue, negate: true), anonymous)
            .Should().BeTrue();
    }

    [Fact]
    public void Matches_ClientIp_InIpRange_UsesCidr()
    {
        var inside = Context(session: Session(clientIp: "10.20.5.7"));
        var outside = Context(session: Session(clientIp: "192.168.1.1"));

        var condition = Condition(RuleConditionField.ClientIp, RuleConditionOperator.InIpRange, "10.20.0.0/16");

        Match(condition, inside).Should().BeTrue();
        Match(condition, outside).Should().BeFalse();
    }

    [Fact]
    public void Matches_ClientIp_InIpRange_AcceptsSeveralRanges()
    {
        var ctx = Context(session: Session(clientIp: "172.16.4.4"));

        Match(Condition(RuleConditionField.ClientIp, RuleConditionOperator.InIpRange, "10.0.0.0/8;172.16.0.0/12"), ctx)
            .Should().BeTrue();
    }

    [Fact]
    public void Matches_IsSignedAndIsEncrypted_ClassifyProtectedMail()
    {
        var signed = Context(ProtectedMessage("signed", "application/pkcs7-signature"));
        var encrypted = Context(ProtectedMessage("encrypted", "application/pgp-encrypted"));
        var plain = Context(TextMessage());

        Match(Condition(RuleConditionField.IsSigned, RuleConditionOperator.IsTrue), signed).Should().BeTrue();
        Match(Condition(RuleConditionField.IsEncrypted, RuleConditionOperator.IsTrue), encrypted).Should().BeTrue();
        Match(Condition(RuleConditionField.IsSigned, RuleConditionOperator.IsTrue), plain).Should().BeFalse();
        Match(Condition(RuleConditionField.IsEncrypted, RuleConditionOperator.IsTrue), plain).Should().BeFalse();
    }

    // =========================================================================
    // Attachments and importance
    // =========================================================================

    [Fact]
    public void Matches_AttachmentExtension_ChecksEveryAttachment()
    {
        var ctx = Context(WithAttachments(null, ("report.pdf", "application/pdf", 512), ("macro.docm", "application/msword", 256)));

        Match(Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, ".docm"), ctx)
            .Should().BeTrue();
        Match(Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, ".exe"), ctx)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("xml", true)]
    [InlineData(".xml", true)]
    [InlineData("XML", true)]
    [InlineData("docx", false)]
    [InlineData(".docx", false)]
    public void Matches_AttachmentExtension_AcceptsBothSpellings(string value, bool expected)
    {
        // Reported defect: the RemoveAttachments action accepts an extension with or without the
        // leading dot, the condition only accepted the dotted form. An operator who writes "xml"
        // in both places got a rule that looked configured and silently never fired.
        var ctx = Context(WithAttachments(null, ("test.xml", "application/xml", 256)));

        Match(Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, value), ctx)
            .Should().Be(expected);
    }

    [Fact]
    public void Matches_AttachmentExtension_BothSpellings_WorkWithEveryOperator()
    {
        var ctx = Context(WithAttachments(null, ("test.xml", "application/xml", 256)));

        Match(Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Matches, "xml;xsd"), ctx)
            .Should().BeTrue();
        Match(Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.RegexMatches, "^xml$"), ctx)
            .Should().BeTrue();
        Match(Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.EndsWith, "ml"), ctx)
            .Should().BeTrue();
    }

    [Fact]
    public void Matches_AttachmentExtension_FileWithoutOne_YieldsNoValue()
    {
        var ctx = Context(WithAttachments(null, ("README", "text/plain", 64)));

        Match(Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Exists), ctx)
            .Should().BeFalse();
    }

    [Fact]
    public void Matches_AttachmentName_SupportsWildcards()
    {
        var ctx = Context(WithAttachments(null, ("invoice-4711.pdf", "application/pdf", 512)));

        Match(Condition(RuleConditionField.AttachmentName, RuleConditionOperator.Matches, "invoice-*.pdf"), ctx)
            .Should().BeTrue();
    }

    [Fact]
    public void Matches_AttachmentCount_CountsSplitterAttachments()
    {
        var ctx = Context(WithAttachments(null, ("a.pdf", "application/pdf", 16), ("b.pdf", "application/pdf", 16)));

        Match(Condition(RuleConditionField.AttachmentCount, RuleConditionOperator.Equals, "2"), ctx).Should().BeTrue();
    }

    [Fact]
    public void Matches_Importance_ResolvesTheDeliveredValue()
    {
        var high = TextMessage();
        high.Importance = MessageImportance.High;

        var viaXPriority = TextMessage();
        viaXPriority.Headers.Add("X-Priority", "1");

        Match(Condition(RuleConditionField.Importance, RuleConditionOperator.Equals, "High"), Context(high))
            .Should().BeTrue();
        Match(Condition(RuleConditionField.Importance, RuleConditionOperator.Equals, "High"), Context(viaXPriority))
            .Should().BeTrue("X-Priority is the legacy signal and maps to the same delivered value");
        Match(Condition(RuleConditionField.Importance, RuleConditionOperator.Equals, "Normal"), Context(TextMessage()))
            .Should().BeTrue();
    }

    // =========================================================================
    // Guards: nothing here may throw or stall
    // =========================================================================

    [Fact]
    public void Matches_InvalidRegex_DoesNotMatchAndDoesNotThrow()
    {
        var ctx = Context(TextMessage(subject: "anything"));
        var condition = Condition(RuleConditionField.Subject, RuleConditionOperator.RegexMatches, "([unclosed");

        var act = () => Match(condition, ctx);

        act.Should().NotThrow();
        Match(condition, ctx).Should().BeFalse();
    }

    [Fact]
    public void Matches_CatastrophicPattern_CompletesWithoutStalling()
    {
        // The classic nested-quantifier ReDoS. NonBacktracking handles it in linear time; a
        // pattern that falls back to the backtracking engine is bounded by the match timeout.
        // Either way this must return, not hang.
        var subject = new string('a', 40) + "!";
        var ctx = Context(TextMessage(subject: subject));
        var condition = Condition(RuleConditionField.Subject, RuleConditionOperator.RegexMatches, "^(a+)+$");

        var start = DateTime.UtcNow;
        var result = Match(condition, ctx);
        var elapsed = DateTime.UtcNow - start;

        result.Should().BeFalse();
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Matches_UnsupportedFieldOperatorPair_NeverMatches()
    {
        // DomainIs is meaningless on a numeric field; the schema does not define the pair.
        var ctx = Context(recipients: ["a@example.com"]);

        Match(Condition(RuleConditionField.RecipientCount, RuleConditionOperator.DomainIs, "@example.com"), ctx)
            .Should().BeFalse();
    }

    [Fact]
    public void Matches_BodyCondition_TruncatesRatherThanSkipsOversizedBodies()
    {
        // Truncation gives prefix semantics. Skipping would make the condition false, and a
        // negated condition would then fire on every large message — a silent policy inversion.
        var body = new string('x', 500) + "NEEDLE";
        var ctx = Context(TextMessage(body: body), maxBodyScanBytes: 100);

        Match(Condition(RuleConditionField.BodyText, RuleConditionOperator.Contains, "NEEDLE"), ctx)
            .Should().BeFalse("the needle is past the cap");
        Match(Condition(RuleConditionField.BodyText, RuleConditionOperator.Contains, "xxx"), ctx)
            .Should().BeTrue("the prefix is still compared");
        ctx.BodyTruncated.Should().BeTrue();
    }

    // =========================================================================
    // IsMatch: rule-level combination
    // =========================================================================

    [Fact]
    public void IsMatch_DisabledRule_NeverMatches()
    {
        var rule = Rule(enabled: false);
        var ctx = Context();

        MessageRuleEvaluator.IsMatch(rule, ctx, Timeout).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_NoConditions_MatchesEveryMessage()
    {
        var rule = Rule();
        var ctx = Context();

        MessageRuleEvaluator.IsMatch(rule, ctx, Timeout).Should().BeTrue(
            "an empty condition list is a deliberate 'apply to all'");
    }

    [Fact]
    public void IsMatch_MatchAll_RequiresEveryCondition()
    {
        var ctx = Context(TextMessage(subject: "Invoice 4711"));
        var rule = Rule(match: ConditionMatch.All, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Invoice"),
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Credit note"),
        ]);

        MessageRuleEvaluator.IsMatch(rule, ctx, Timeout).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_MatchAny_RequiresOneCondition()
    {
        var ctx = Context(TextMessage(subject: "Invoice 4711"));
        var rule = Rule(match: ConditionMatch.Any, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Invoice"),
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Credit note"),
        ]);

        MessageRuleEvaluator.IsMatch(rule, ctx, Timeout).Should().BeTrue();
    }

    // =========================================================================
    // FindProblems
    // =========================================================================

    [Fact]
    public void FindProblems_ValidRuleSet_ReportsNothing()
    {
        var options = Options(Rule(
            conditions: [Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "x")],
            actions: [new RuleAction { Type = RuleActionType.PrefixSubject, Value = "[TAG] " }]));

        MessageRuleEvaluator.FindProblems(options).Should().BeEmpty();
    }

    [Fact]
    public void FindProblems_InvalidRegex_IsReportedAsAnError()
    {
        var options = Options(Rule(
            conditions: [Condition(RuleConditionField.Subject, RuleConditionOperator.RegexMatches, "([unclosed")],
            actions: [new RuleAction { Type = RuleActionType.Discard }]));

        MessageRuleEvaluator.FindProblems(options).Should()
            .ContainSingle(p => p.IsError && p.Detail.Contains("regular expression"));
    }

    [Fact]
    public void FindProblems_UnsupportedFieldOperatorPair_IsReportedAsAnError()
    {
        var options = Options(Rule(
            conditions: [Condition(RuleConditionField.RecipientCount, RuleConditionOperator.DomainIs, "@x.com")],
            actions: [new RuleAction { Type = RuleActionType.Discard }]));

        MessageRuleEvaluator.FindProblems(options).Should()
            .ContainSingle(p => p.IsError && p.Detail.Contains("not a valid combination"));
    }

    [Fact]
    public void FindProblems_ActionMissingRequiredParameter_IsReportedAsAnError()
    {
        var options = Options(Rule(actions: [new RuleAction { Type = RuleActionType.SetHeader, Value = "yes" }]));

        MessageRuleEvaluator.FindProblems(options).Should()
            .Contain(p => p.IsError && p.Detail.Contains("header name"));
    }

    [Fact]
    public void FindProblems_RuleWithoutActions_IsReportedAsAnError()
    {
        var options = Options(Rule());

        MessageRuleEvaluator.FindProblems(options).Should()
            .ContainSingle(p => p.IsError && p.Detail.Contains("no actions"));
    }

    [Fact]
    public void FindProblems_RejectCodeOutOfRange_IsReportedAsAnError()
    {
        var options = Options(Rule(actions:
            [new RuleAction { Type = RuleActionType.Reject, SmtpCode = 250, Value = "nope" }]));

        MessageRuleEvaluator.FindProblems(options).Should()
            .ContainSingle(p => p.IsError && p.Detail.Contains("400–599"));
    }

    [Fact]
    public void FindProblems_DuplicateRuleNames_AreReportedAsAWarning()
    {
        var action = new RuleAction { Type = RuleActionType.Discard };
        var options = Options(Rule(name: "Same", actions: [action]), Rule(name: "Same", actions: [action]));

        MessageRuleEvaluator.FindProblems(options).Should()
            .ContainSingle(p => !p.IsError && p.Detail.Contains("already uses this name"));
    }

    [Fact]
    public void FindProblems_InvalidCidr_IsReportedAsAnError()
    {
        // "nonsense" and an out-of-range prefix cannot match any client, and an IP condition
        // that matches nothing is invisible at runtime — the operator sees a configured rule
        // and no effect. Note that a bare "123" is NOT invalid here: IpFilterService normalises
        // it to 0.0.0.123/32, the same as everywhere else in the service.
        var options = Options(Rule(
            conditions: [Condition(RuleConditionField.ClientIp, RuleConditionOperator.InIpRange, "nonsense;10.0.0.0/99")],
            actions: [new RuleAction { Type = RuleActionType.Discard }]));

        MessageRuleEvaluator.FindProblems(options).Should()
            .ContainSingle(p => p.IsError && p.Detail.Contains("IP or CIDR"));
    }

    // =========================================================================
    // Header delivery warnings
    // =========================================================================

    [Theory]
    [InlineData("X-Custom-Tag", false)]
    [InlineData("x-lower-case", false)]
    [InlineData("List-Unsubscribe", true)]
    [InlineData("Auto-Submitted", true)]
    [InlineData("X-MS-Exchange-Organization-AuthAs", true)]
    [InlineData("X-Priority", true)]
    public void DescribeHeaderDeliveryWarning_FlagsHeadersGraphDoesNotCarry(string header, bool expectWarning)
    {
        var warning = MessageRuleEvaluator.DescribeHeaderDeliveryWarning(header);

        (warning is not null).Should().Be(expectWarning);
    }

    // =========================================================================
    // Combining conditions — All / Any across different field types
    // =========================================================================

    private static bool IsMatch(MessageRule rule, MessageRuleContext ctx)
        => MessageRuleEvaluator.IsMatch(rule, ctx, Timeout);

    [Fact]
    public void IsMatch_MatchAll_AcrossDifferentFieldTypes_RequiresEveryOne()
    {
        // Text, IP range, attachment and boolean in one rule — the realistic shape of a policy
        // ("mail from outside the ERP network carrying a PDF"), and the case where a per-type
        // bug in one evaluator branch would otherwise hide behind three passing ones.
        var ctx = RichContext();

        var allTrue = Rule(match: ConditionMatch.All, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Quarterly"),
            Condition(RuleConditionField.ClientIp, RuleConditionOperator.InIpRange, "10.20.0.0/16"),
            Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, ".pdf"),
            Condition(RuleConditionField.Tls, RuleConditionOperator.IsTrue),
        ]);

        var oneFalse = Rule(match: ConditionMatch.All, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Quarterly"),
            Condition(RuleConditionField.ClientIp, RuleConditionOperator.InIpRange, "10.20.0.0/16"),
            Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, ".exe"),
            Condition(RuleConditionField.Tls, RuleConditionOperator.IsTrue),
        ]);

        IsMatch(allTrue, ctx).Should().BeTrue();
        IsMatch(oneFalse, RichContext()).Should().BeFalse("a single false condition defeats All");
    }

    [Fact]
    public void IsMatch_MatchAny_AcrossDifferentFieldTypes_NeedsOnlyOne()
    {
        var onlyLastTrue = Rule(match: ConditionMatch.Any, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Monthly"),
            Condition(RuleConditionField.ClientIp, RuleConditionOperator.InIpRange, "192.168.0.0/16"),
            Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, ".pdf"),
        ]);

        var noneTrue = Rule(match: ConditionMatch.Any, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Monthly"),
            Condition(RuleConditionField.ClientIp, RuleConditionOperator.InIpRange, "192.168.0.0/16"),
            Condition(RuleConditionField.AttachmentExtension, RuleConditionOperator.Equals, ".exe"),
        ]);

        IsMatch(onlyLastTrue, RichContext()).Should().BeTrue();
        IsMatch(noneTrue, RichContext()).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_MatchAll_WithANegatedCondition_CombinesCorrectly()
    {
        // "Quarterly report, but not to a partner" — negation has to compose with the other
        // conditions rather than inverting the rule as a whole.
        var ctx = RichContext();   // recipients include extra@partner.test

        var rule = Rule(match: ConditionMatch.All, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Quarterly"),
            Condition(RuleConditionField.EnvelopeRecipient, RuleConditionOperator.DomainIs, "@partner.test", negate: true),
        ]);

        IsMatch(rule, ctx).Should().BeFalse("a partner recipient is present, so the negated condition is false");

        var internalOnly = Context(RichMessage(), recipients: ["rcpt@example.com"]);
        IsMatch(rule, internalOnly).Should().BeTrue("with no partner recipient both conditions hold");
    }

    [Fact]
    public void IsMatch_MatchAny_WithANegatedCondition_CanBeTheOnlyReason()
    {
        var rule = Rule(match: ConditionMatch.Any, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Monthly"),
            Condition(RuleConditionField.Authenticated, RuleConditionOperator.IsTrue, negate: true),
        ]);

        var authenticated = RichContext();
        var anonymous = MessageRuleContext.Create(
            Serialise(RichMessage()), "sender@example.com", ["rcpt@example.com"], Session());

        IsMatch(rule, authenticated).Should().BeFalse("neither condition holds for an authenticated session");
        IsMatch(rule, anonymous).Should().BeTrue("the negated condition alone satisfies Any");
    }

    [Fact]
    public void IsMatch_TwoNegatedMultiValuedConditions_MeanNeitherSetMatches()
    {
        // Both are existential-then-negated, so All over them reads "no recipient at either
        // domain" — the composition that makes an allow-list-style rule expressible.
        var rule = Rule(match: ConditionMatch.All, conditions:
        [
            Condition(RuleConditionField.EnvelopeRecipient, RuleConditionOperator.DomainIs, "@partner.test", negate: true),
            Condition(RuleConditionField.EnvelopeRecipient, RuleConditionOperator.DomainIs, "@other.test", negate: true),
        ]);

        IsMatch(rule, Context(recipients: ["a@example.com"])).Should().BeTrue();
        IsMatch(rule, Context(recipients: ["a@example.com", "b@partner.test"])).Should().BeFalse();
        IsMatch(rule, Context(recipients: ["a@example.com", "b@other.test"])).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_MatchAll_MiddleConditionFalse_DoesNotMatch()
    {
        // Guards against an evaluator that stops at the first condition or only checks the last.
        var rule = Rule(match: ConditionMatch.All, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Quarterly"),
            Condition(RuleConditionField.AuthUser, RuleConditionOperator.Equals, "nobody"),
            Condition(RuleConditionField.ListenerPort, RuleConditionOperator.Equals, "587"),
        ]);

        IsMatch(rule, RichContext()).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_MatchAny_MiddleConditionTrue_Matches()
    {
        var rule = Rule(match: ConditionMatch.Any, conditions:
        [
            Condition(RuleConditionField.Subject, RuleConditionOperator.Contains, "Monthly"),
            Condition(RuleConditionField.ListenerPort, RuleConditionOperator.Equals, "587"),
            Condition(RuleConditionField.AuthUser, RuleConditionOperator.Equals, "nobody"),
        ]);

        IsMatch(rule, RichContext()).Should().BeTrue();
    }

    [Fact]
    public void IsMatch_ConditionsMixingBodyAndEnvelope_ComposeAcrossTheParseBoundary()
    {
        // Envelope facts need no parse; body content does. A rule combining both must evaluate
        // consistently regardless of which side is checked first.
        var rule = Rule(match: ConditionMatch.All, conditions:
        [
            Condition(RuleConditionField.EnvelopeFrom, RuleConditionOperator.DomainIs, "@example.com"),
            Condition(RuleConditionField.BodyHtml, RuleConditionOperator.Contains, "the html body"),
            Condition(RuleConditionField.MessageSizeBytes, RuleConditionOperator.GreaterThan, "100"),
        ]);

        IsMatch(rule, RichContext()).Should().BeTrue();
    }

    [Fact]
    public void IsMatch_SingleFalseCondition_IsEnoughToSkipTheRule()
    {
        IsMatch(
            Rule(conditions: [Condition(RuleConditionField.Subject, RuleConditionOperator.Equals, "nope")]),
            RichContext())
            .Should().BeFalse();
    }
}
