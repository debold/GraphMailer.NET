using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Rules;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Input rules for the Message Rules page.
///
/// What these guard is the gap between what the tool accepts and what the service does: a
/// condition the page stores but the runtime can never match is invisible at delivery time —
/// the operator sees a configured rule and no effect, with nothing to say why. Every rule here
/// that has a runtime counterpart delegates to the service's own code rather than re-deriving it.
/// </summary>
public sealed class MessageRuleValidationTests
{
    private const int Timeout = 100;

    private static ConfigDocument.RuleActionEntry Action(
        RuleActionType type,
        string? value = null,
        string? headerName = null,
        string? recipient = null,
        string? match = null,
        string? attachmentMatch = null,
        int? smtpCode = null)
        => new()
        {
            Type = type.ToString(),
            Value = value,
            HeaderName = headerName,
            Recipient = recipient,
            Match = match,
            AttachmentMatch = attachmentMatch,
            SmtpCode = smtpCode,
        };

    // =========================================================================
    // Conditions
    // =========================================================================

    [Fact]
    public void ValidateCondition_ValidTextCondition_IsAccepted()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.Subject, RuleConditionOperator.Contains, "invoice", null, false, Timeout)
            .Should().BeNull();
    }

    [Fact]
    public void ValidateCondition_UnsupportedPair_IsRejected()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.RecipientCount, RuleConditionOperator.DomainIs, "@x.com", null, false, Timeout)
            .Should().NotBeNull();
    }

    [Fact]
    public void ValidateCondition_HeaderWithoutName_IsRejected()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.Header, RuleConditionOperator.Contains, "x", null, false, Timeout)
            .Should().Contain("header");
    }

    [Fact]
    public void ValidateCondition_MissingValue_IsRejected()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.Subject, RuleConditionOperator.Contains, "  ", null, false, Timeout)
            .Should().NotBeNull();
    }

    [Fact]
    public void ValidateCondition_OperatorWithoutValue_NeedsNone()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.Subject, RuleConditionOperator.IsEmpty, "", null, false, Timeout)
            .Should().BeNull();
    }

    [Fact]
    public void ValidateCondition_InvalidRegex_IsRejected()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.Subject, RuleConditionOperator.RegexMatches, "([unclosed", null, false, Timeout)
            .Should().Contain("regular expression");
    }

    [Fact]
    public void ValidateCondition_ValidRegex_IsAccepted()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.Subject, RuleConditionOperator.RegexMatches, @"^INV-\d+$", null, false, Timeout)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.0.0.0/8;192.168.0.0/16", true)]
    [InlineData("123", false)]
    [InlineData("10.0.0.0/99", false)]
    [InlineData("nonsense", false)]
    public void ValidateCondition_IpRange_MirrorsTheAddressRules(string value, bool valid)
    {
        // "123" is refused here even though IpFilterService would normalise it to 0.0.0.123 —
        // the page is where a typo can still be caught, and a rule matching an address nobody
        // meant is exactly the silent failure this check exists for.
        var problem = MessageRuleValidation.ValidateCondition(
            RuleConditionField.ClientIp, RuleConditionOperator.InIpRange, value, null, false, Timeout);

        (problem is null).Should().Be(valid);
    }

    [Fact]
    public void ValidateCondition_NumericFieldWithText_IsRejected()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.RecipientCount, RuleConditionOperator.GreaterThan, "many", null, false, Timeout)
            .Should().Contain("number");
    }

    [Fact]
    public void ValidateCondition_DomainWithoutAtSign_IsRejected()
    {
        MessageRuleValidation.ValidateCondition(
            RuleConditionField.EnvelopeFrom, RuleConditionOperator.DomainIs, "example.com", null, false, Timeout)
            .Should().Contain("@");
    }

    [Fact]
    public void OperatorsFor_MirrorsTheServiceSchema()
    {
        foreach (var field in Enum.GetValues<RuleConditionField>())
        {
            MessageRuleValidation.OperatorsFor(field)
                .Should().BeEquivalentTo(RuleConditionSchema.OperatorsFor(field));
        }
    }

    // =========================================================================
    // Actions
    // =========================================================================

    [Fact]
    public void ValidateAction_ValidAction_IsAccepted()
    {
        MessageRuleValidation.ValidateAction(
            RuleActionType.PrefixSubject, Action(RuleActionType.PrefixSubject, "[TAG] "))
            .Should().BeNull();
    }

    [Fact]
    public void ValidateAction_MissingRequiredValue_IsRejected()
    {
        MessageRuleValidation.ValidateAction(
            RuleActionType.PrefixSubject, Action(RuleActionType.PrefixSubject))
            .Should().NotBeNull();
    }

    [Fact]
    public void ValidateAction_HeaderActionWithoutName_IsRejected()
    {
        MessageRuleValidation.ValidateAction(
            RuleActionType.SetHeader, Action(RuleActionType.SetHeader, value: "yes"))
            .Should().Contain("header name");
    }

    [Fact]
    public void ValidateAction_RecipientActionWithoutList_IsRejected()
    {
        MessageRuleValidation.ValidateAction(
            RuleActionType.AddRecipient, Action(RuleActionType.AddRecipient, "a@example.com"))
            .Should().Contain("To, Cc or Bcc");
    }

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("first.last@sub.example.com", true)]
    [InlineData("not an address", false)]
    [InlineData("user@localhost", false)]
    [InlineData("Name <user@example.com>", false)]
    [InlineData("", false)]
    public void ValidateAction_Address_UsesTheSameRuleAsEveryOtherPage(string value, bool valid)
    {
        // Harmonised with EmailValidation, which the Notifications, Backup and SMTP-user pages
        // already use. A different rule here would mean an address one page accepts and another
        // refuses, for no reason the operator can see.
        var problem = MessageRuleValidation.ValidateAction(
            RuleActionType.AddRecipient, Action(RuleActionType.AddRecipient, value, recipient: "To"));

        (problem is null).Should().Be(valid);
        EmailValidation.IsValidRecipient(value).Should().Be(valid, "the page must not diverge from the shared rule");
    }

    [Theory]
    [InlineData(RuleActionType.SetFrom)]
    [InlineData(RuleActionType.SetReplyTo)]
    [InlineData(RuleActionType.ReplaceRecipient)]
    internal void ValidateAction_EveryAddressAction_SharesTheSameRule(RuleActionType type)
    {
        var good = Action(type, "user@example.com", recipient: "To", match: "old@example.com");
        var bad = Action(type, "user@localhost", recipient: "To", match: "old@example.com");

        MessageRuleValidation.ValidateAction(type, good).Should().BeNull();
        MessageRuleValidation.ValidateAction(type, bad).Should().NotBeNull();
    }

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("@example.com", true)]
    [InlineData("*@example.com", true)]
    [InlineData("invoice-*@example.com", true)]
    [InlineData("*", true)]
    [InlineData("a@example.com;@partner.test", true)]
    [InlineData("nonsense", false)]
    [InlineData("@not a domain", false)]
    [InlineData("", false)]
    public void ValidateAddressPattern_AcceptsTheShapesTheRuntimeMatches(string pattern, bool valid)
    {
        // The shape rules come from AddressPatternValidator — the Access Control lists use the
        // same ones — with the wildcards the rule engine additionally supports.
        var problem = MessageRuleValidation.ValidateAddressPattern(pattern);

        (problem is null).Should().Be(valid);
    }

    [Fact]
    public void ValidateAction_RemoveRecipient_ValidatesThePattern()
    {
        MessageRuleValidation.ValidateAction(
            RuleActionType.RemoveRecipient, Action(RuleActionType.RemoveRecipient, match: "nonsense"))
            .Should().NotBeNull();

        MessageRuleValidation.ValidateAction(
            RuleActionType.RemoveRecipient, Action(RuleActionType.RemoveRecipient, match: "@partner.test"))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(550, true)]
    [InlineData(554, true)]
    [InlineData(451, true)]
    [InlineData(250, false)]
    [InlineData(600, false)]
    public void ValidateAction_RejectCode_MustBeARejection(int code, bool valid)
    {
        var problem = MessageRuleValidation.ValidateAction(
            RuleActionType.Reject, Action(RuleActionType.Reject, "no", smtpCode: code));

        (problem is null).Should().Be(valid);
    }

    [Fact]
    public void ValidateAction_AttachmentSizeThatIsNotANumber_IsRejected()
    {
        MessageRuleValidation.ValidateAction(
            RuleActionType.RemoveAttachments,
            Action(RuleActionType.RemoveAttachments, "big", attachmentMatch: "MinSizeBytes"))
            .Should().Contain("bytes");
    }

    [Theory]
    [InlineData("High", true)]
    [InlineData("low", true)]
    [InlineData("Urgent", false)]
    public void ValidateAction_Importance_AcceptsOnlyTheKnownTokens(string value, bool valid)
    {
        var problem = MessageRuleValidation.ValidateAction(
            RuleActionType.SetImportance, Action(RuleActionType.SetImportance, value));

        (problem is null).Should().Be(valid);
    }

    // =========================================================================
    // Warnings
    // =========================================================================

    [Fact]
    public void DescribeActionWarning_UndeliverableHeader_IsFlagged()
    {
        MessageRuleValidation.DescribeActionWarning(
            RuleActionType.SetHeader, Action(RuleActionType.SetHeader, "x", headerName: "List-Unsubscribe"))
            .Should().Contain("not carried to Microsoft 365");
    }

    [Fact]
    public void DescribeActionWarning_CustomXHeader_IsNotFlagged()
    {
        MessageRuleValidation.DescribeActionWarning(
            RuleActionType.SetHeader, Action(RuleActionType.SetHeader, "x", headerName: "X-Relay-Policy"))
            .Should().BeNull();
    }

    [Fact]
    public void DescribeActionWarning_BodyActionWithoutHtml_ExplainsWhatIsDelivered()
    {
        MessageRuleValidation.DescribeActionWarning(
            RuleActionType.PrependBody, Action(RuleActionType.PrependBody, "banner"))
            .Should().Contain("only the HTML one is delivered");
    }

    [Fact]
    public void DescribeActionWarning_SetFrom_MentionsTheSenderChecks()
    {
        MessageRuleValidation.DescribeActionWarning(
            RuleActionType.SetFrom, Action(RuleActionType.SetFrom, "relay@example.com"))
            .Should().Contain("sending mailbox");
    }

    [Fact]
    public void DescribeActionWarning_Discard_SaysNothingIsDelivered()
    {
        MessageRuleValidation.DescribeActionWarning(
            RuleActionType.Discard, Action(RuleActionType.Discard))
            .Should().Contain("Nothing is delivered");
    }

    // =========================================================================
    // Rule set
    // =========================================================================

    [Fact]
    public void FindProblems_ReportsWhatTheServiceWouldReport()
    {
        var section = new ConfigDocument.MessageRulesSection
        {
            Enabled = true,
            Rules =
            [
                new ConfigDocument.MessageRuleEntry
                {
                    Name = "broken",
                    Conditions =
                    [
                        new() { Field = "Subject", Operator = "RegexMatches", Value = "([unclosed" },
                    ],
                    Actions = [new() { Type = "Discard" }],
                },
            ],
        };

        MessageRuleValidation.FindProblems(section).Should()
            .ContainSingle(p => p.IsError && p.Detail.Contains("regular expression"));
    }

    [Fact]
    public void IsDuplicateName_IsCaseInsensitiveAndIgnoresTheRuleBeingEdited()
    {
        var existing = new List<ConfigDocument.MessageRuleEntry>
        {
            new() { Name = "External disclaimer" },
            new() { Name = "Block macros" },
        };

        MessageRuleValidation.IsDuplicateName(existing, "external disclaimer").Should().BeTrue();
        MessageRuleValidation.IsDuplicateName(existing, "Something else").Should().BeFalse();
        MessageRuleValidation.IsDuplicateName(existing, "External disclaimer", existing[0]).Should().BeFalse(
            "editing a rule must not report the rule itself as a duplicate");
    }

    [Fact]
    public void IsDuplicateName_BlankName_IsNotADuplicate()
    {
        MessageRuleValidation.IsDuplicateName([new() { Name = "x" }], "   ").Should().BeFalse();
    }

    // =========================================================================
    // Document → options conversion
    // =========================================================================

    [Fact]
    public void ToOptions_UnknownEnumToken_FallsBackToTheSafeChoice()
    {
        // A hand-edited file with a typo must not silently start enforcing.
        var section = new ConfigDocument.MessageRulesSection
        {
            Rules = [new() { Name = "r", Mode = "Enfroce", Match = "Either", Actions = [new() { Type = "Discard" }] }],
        };

        var rule = MessageRuleModel.ToOptions(section).Rules.Should().ContainSingle().Subject;

        rule.Mode.Should().Be(MessageRuleMode.Audit);
        rule.Match.Should().Be(ConditionMatch.All);
    }

    [Fact]
    public void ToOptions_CarriesEveryFieldThroughUnchanged()
    {
        var section = new ConfigDocument.MessageRulesSection
        {
            Enabled = true,
            MaxBodyScanBytes = 2048,
            RegexTimeoutMs = 250,
            StoreDiscardedMessages = true,
            DiscardRecordRetentionDays = 30,
            Rules =
            [
                new()
                {
                    Name = "r",
                    Mode = "Enforce",
                    Match = "Any",
                    StopProcessing = true,
                    Conditions = [new() { Field = "ClientIp", Operator = "InIpRange", Value = "10.0.0.0/8", Negate = true }],
                    Actions = [new() { Type = "AddRecipient", Recipient = "Bcc", Value = "a@example.com" }],
                },
            ],
        };

        var options = MessageRuleModel.ToOptions(section);

        options.Enabled.Should().BeTrue();
        options.MaxBodyScanBytes.Should().Be(2048);
        options.RegexTimeoutMs.Should().Be(250);
        options.StoreDiscardedMessages.Should().BeTrue();
        options.DiscardRecordRetentionDays.Should().Be(30);

        var rule = options.Rules.Should().ContainSingle().Subject;
        rule.Mode.Should().Be(MessageRuleMode.Enforce);
        rule.Match.Should().Be(ConditionMatch.Any);
        rule.StopProcessing.Should().BeTrue();
        rule.Conditions.Should().ContainSingle().Which.Negate.Should().BeTrue();
        rule.Actions.Should().ContainSingle().Which.Recipient.Should().Be(RecipientKind.Bcc);
    }
}
