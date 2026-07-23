using FluentAssertions;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.Tests.Unit.Services.Advisor;

/// <summary>
/// Contract tests for the shared recommendation catalog. Every rule gets an open/done pair,
/// because a rule that never fires is invisible and a rule that always fires trains operators to
/// ignore the whole feature.
///
/// The baseline (<see cref="Ideal"/>) is an installation that satisfies every rule, so a test
/// switching one thing off asserts against exactly one open hint — and the rest land in Done.
/// </summary>
public sealed class RecommendationEngineTests
{
    /// <summary>An installation nothing should be recommended for.</summary>
    private static RecommendationInput Ideal => new()
    {
        GraphConfigured = true,
        GraphUsesClientSecret = false,
        GraphUsesCertificate = true,
        EnabledListenerCount = 2,
        HasTlsListener = true,
        PlaintextAuthListeners = [],
        SenderValidationEnabled = true,
        BackupEnabled = true,
        NdrEnabled = true,
        UpdateCheckEnabled = true,
        TelemetryEnabled = true,
        LogLevel = "Information",
        HasAdminNotificationRecipients = true,
    };

    /// <summary>Ids of the suggestions that currently ask for action.</summary>
    private static IReadOnlyList<string> IdsFor(RecommendationInput input)
        => [.. RecommendationEngine.Evaluate(input).Open.Select(r => r.Id)];

    /// <summary>Ids of the suggestions this installation already satisfies.</summary>
    private static IReadOnlyList<string> DoneIdsFor(RecommendationInput input)
        => [.. RecommendationEngine.Evaluate(input).Done.Select(r => r.Id)];

    [Fact]
    public void Evaluate_FullyConfiguredInstallation_HasNothingOpen()
        => RecommendationEngine.Evaluate(Ideal).Open.Should().BeEmpty();

    [Fact]
    public void Evaluate_FullyConfiguredInstallation_ReportsEveryRuleAsDone()
    {
        // The satisfied rules are kept rather than dropped, so the ConfigTool can show what was
        // suggested and already handled instead of an empty page.
        var summary = RecommendationEngine.Evaluate(Ideal);

        summary.Done.Select(r => r.Id).Should().BeEquivalentTo(
        [
            RecommendationIds.GraphClientCertificate,
            RecommendationIds.TlsListener,
            RecommendationIds.SenderValidation,
            RecommendationIds.ConfigBackup,
            RecommendationIds.Ndr,
            RecommendationIds.LogLevel,
            RecommendationIds.AdminNotifications,
            RecommendationIds.UpdateCheck,
            RecommendationIds.Telemetry,
        ]);
        summary.Done.Should().OnlyContain(r => r.State == RecommendationState.Done);
        summary.Dismissed.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_DoneHint_CarriesTheSatisfiedWordingNotTheArgumentForIt()
    {
        // "Without it, a message …" reads wrong for something already switched on.
        var done = RecommendationEngine.Evaluate(Ideal).Done
            .Single(r => r.Id == RecommendationIds.Telemetry);

        done.DoneSummary.Should().Contain("is on");
        done.DoneSummary.Should().NotBe(done.Detail);
    }

    // ── Security ─────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_GraphUsesClientSecret_RecommendsCertificate()
        => IdsFor(Ideal with { GraphUsesClientSecret = true, GraphUsesCertificate = false })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.GraphClientCertificate);

    [Fact]
    public void Evaluate_GraphUsesBothSecretAndCertificate_DoesNotRecommendCertificate()
        => IdsFor(Ideal with { GraphUsesClientSecret = true, GraphUsesCertificate = true })
            .Should().BeEmpty("the certificate already takes precedence over the secret");

    [Fact]
    public void Evaluate_GraphNotConfigured_OmitsGraphRulesEntirely()
    {
        // A fresh install has neither a tenant id nor an auth method — advising on which of the
        // two auth flavours to use, or on sender validation, is noise before setup is done. They
        // must not show up as "done" either: that would claim something that was never checked.
        var summary = RecommendationEngine.Evaluate(Ideal with
        {
            GraphConfigured = false,
            GraphUsesClientSecret = true,
            GraphUsesCertificate = false,
            SenderValidationEnabled = false,
        });

        summary.All.Select(r => r.Id).Should()
            .NotContain(RecommendationIds.GraphClientCertificate)
            .And.NotContain(RecommendationIds.SenderValidation);
    }

    [Fact]
    public void Evaluate_PlaintextListenerAcceptsAuth_RecommendsTls()
        => IdsFor(Ideal with { PlaintextAuthListeners = ["SMTP (port 25)"] })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.TlsListener);

    [Fact]
    public void Evaluate_PlaintextListenerWithoutAuth_DoesNotRecommendTls()
        // A plain listener with AuthMode "None" never sees a password, so there is nothing to
        // protect — the old rule fired on any plaintext listener and nagged such relays forever.
        => IdsFor(Ideal with { HasTlsListener = false, PlaintextAuthListeners = [] })
            .Should().BeEmpty();

    [Fact]
    public void Evaluate_PlaintextAuthListener_NamesTheAffectedListeners()
    {
        // The operator has to know which listener to change; "some listener" is not actionable.
        var detail = RecommendationEngine.Evaluate(
                Ideal with { PlaintextAuthListeners = ["SMTP (port 25)", "Legacy (port 2525)"] })
            .Open.Single(r => r.Id == RecommendationIds.TlsListener).Detail;

        detail.Should().Contain("SMTP (port 25)").And.Contain("Legacy (port 2525)");
    }

    [Fact]
    public void Evaluate_TlsRule_IsRatedHighBecauseCredentialsAreExposed()
        => RecommendationEngine.Evaluate(Ideal with { PlaintextAuthListeners = ["SMTP (port 25)"] })
            .Open.Single().Severity.Should().Be(RecommendationSeverity.High);

    [Fact]
    public void Evaluate_NoEnabledListener_OmitsTheTlsRuleEntirely()
        => RecommendationEngine.Evaluate(Ideal with
            {
                EnabledListenerCount = 0, HasTlsListener = false, PlaintextAuthListeners = [],
            })
            .All.Select(r => r.Id).Should().NotContain(RecommendationIds.TlsListener,
                "an install without any listener has a different problem, and calling it done would be a lie");

    // ── Reliability ──────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_SenderValidationOff_RecommendsSenderValidation()
        => IdsFor(Ideal with { SenderValidationEnabled = false })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.SenderValidation);

    [Fact]
    public void Evaluate_BackupOff_RecommendsBackup()
        => IdsFor(Ideal with { BackupEnabled = false })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.ConfigBackup);

    [Fact]
    public void Evaluate_NdrOff_RecommendsNdr()
        => IdsFor(Ideal with { NdrEnabled = false })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.Ndr);

    // ── Operations ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Verbose")]
    [InlineData("Debug")]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Fatal")]
    public void Evaluate_LogLevelOtherThanInformation_RecommendsInformation(string level)
        => IdsFor(Ideal with { LogLevel = level })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.LogLevel);

    [Fact]
    public void Evaluate_LogLevelInformationInAnyCasing_DoesNotRecommend()
        => IdsFor(Ideal with { LogLevel = "information" }).Should().BeEmpty();

    [Fact]
    public void Evaluate_VerboseLogLevel_ExplainsTheNoiseNotTheBlindSpot()
    {
        var detail = RecommendationEngine.Evaluate(Ideal with { LogLevel = "Debug" })
            .Open.Single(r => r.Id == RecommendationIds.LogLevel).Detail;

        detail.Should().Contain("Debug").And.Contain("fills the log directory");
    }

    [Fact]
    public void Evaluate_HighLogLevel_ExplainsTheBlindSpotNotTheNoise()
    {
        var detail = RecommendationEngine.Evaluate(Ideal with { LogLevel = "Error" })
            .Open.Single(r => r.Id == RecommendationIds.LogLevel).Detail;

        detail.Should().Contain("Error").And.Contain("never written");
    }

    [Fact]
    public void Evaluate_NoAdminRecipients_RecommendsAddingOne()
        => IdsFor(Ideal with { HasAdminNotificationRecipients = false })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.AdminNotifications);

    [Fact]
    public void Evaluate_UpdateCheckOff_RecommendsUpdateCheck()
        => IdsFor(Ideal with { UpdateCheckEnabled = false })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.UpdateCheck);

    [Fact]
    public void Evaluate_TelemetryOff_RecommendsTelemetry()
        => IdsFor(Ideal with { TelemetryEnabled = false })
            .Should().ContainSingle().Which.Should().Be(RecommendationIds.Telemetry);

    // ── Ordering & metadata ──────────────────────────────────────────────────

    [Fact]
    public void Evaluate_MultipleHints_SortsBySeverityFirst()
    {
        var open = RecommendationEngine.Evaluate(Ideal with
        {
            GraphUsesClientSecret = true,   // High
            GraphUsesCertificate = false,
            BackupEnabled = false,          // Medium
            LogLevel = "Debug",             // Low
            TelemetryEnabled = false,       // Low
        }).Open;

        open.Select(r => r.Severity).Should().BeInAscendingOrder("High sorts before Medium before Low");
        open.First().Severity.Should().Be(RecommendationSeverity.High);
        open.Last().Severity.Should().Be(RecommendationSeverity.Low);
    }

    [Fact]
    public void Evaluate_SameSeverity_FallsBackToCategoryOrder()
    {
        var open = RecommendationEngine.Evaluate(Ideal with
        {
            SenderValidationEnabled = false,        // Medium · Reliability
            HasAdminNotificationRecipients = false, // Medium · Operations
        }).Open;

        open.Select(r => r.Id).Should().Equal(
            RecommendationIds.SenderValidation, RecommendationIds.AdminNotifications);
    }

    [Fact]
    public void Evaluate_EveryHint_ExplainsWhyItMatters()
    {
        // The severity rating has to be justified in words, not just asserted by a chip.
        var open = RecommendationEngine.Evaluate(new RecommendationInput
        {
            GraphConfigured = true,
            GraphUsesClientSecret = true,
            EnabledListenerCount = 1,
            PlaintextAuthListeners = ["SMTP (port 25)"],
            LogLevel = "Debug",
        }).Open;

        open.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Impact));
        open.Should().OnlyContain(r => r.Impact != r.Detail);
    }

    [Fact]
    public void Evaluate_EveryHint_CarriesATargetPageAndHelpPage()
    {
        // A hint the operator cannot act on is worse than no hint: both shortcuts must be filled.
        var open = RecommendationEngine.Evaluate(new RecommendationInput
        {
            GraphConfigured = true,
            GraphUsesClientSecret = true,
            EnabledListenerCount = 1,
            PlaintextAuthListeners = ["SMTP (port 25)"],
            LogLevel = "Debug",
        }).Open;

        open.Should().HaveCount(9, "every rule in the catalog should fire for a fully unconfigured install");
        open.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.TargetPageName));
        open.Should().OnlyContain(r => r.HelpPage.EndsWith(".html"));
        open.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.DoneSummary));
        open.Select(r => r.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Evaluate_EveryRelevantRule_LandsInExactlyOneSection()
    {
        var summary = RecommendationEngine.Evaluate(
            Ideal with { BackupEnabled = false, TelemetryEnabled = false },
            [RecommendationIds.Telemetry]);

        summary.All.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        summary.Open.Should().ContainSingle().Which.Id.Should().Be(RecommendationIds.ConfigBackup);
        summary.Dismissed.Should().ContainSingle().Which.Id.Should().Be(RecommendationIds.Telemetry);
        summary.Done.Should().HaveCount(7);
    }

    // ── Dismissal ────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_DismissedId_MovesTheHintOutOfTheOpenList()
    {
        var input = Ideal with { BackupEnabled = false, TelemetryEnabled = false };

        var summary = RecommendationEngine.Evaluate(input, [RecommendationIds.Telemetry]);

        summary.Open.Select(r => r.Id).Should().Equal(RecommendationIds.ConfigBackup);
        summary.Dismissed.Select(r => r.Id).Should().Equal(RecommendationIds.Telemetry);
        summary.Dismissed.Should().OnlyContain(r => r.State == RecommendationState.Dismissed);
    }

    [Fact]
    public void Evaluate_DismissedIdThatIsAlreadySatisfied_StaysInTheHiddenSection()
    {
        // Hiding wins over "done" so the hidden section is exactly the persisted list — that keeps
        // it the one place to review and undo a dismissal.
        var summary = RecommendationEngine.Evaluate(Ideal, [RecommendationIds.Telemetry]);

        summary.Dismissed.Select(r => r.Id).Should().Equal(RecommendationIds.Telemetry);
        summary.Done.Select(r => r.Id).Should().NotContain(RecommendationIds.Telemetry);
    }

    [Fact]
    public void Evaluate_UnknownDismissedId_IsIgnored()
    {
        // A config written by a newer build (or a rule that was removed) must never break evaluation.
        var summary = RecommendationEngine.Evaluate(
            Ideal with { BackupEnabled = false }, ["no-such-rule", ""]);

        summary.Open.Select(r => r.Id).Should().Equal(RecommendationIds.ConfigBackup);
        summary.Dismissed.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_NullDismissedList_ShowsEverythingAsOpenOrDone()
        => RecommendationEngine.Evaluate(Ideal with { BackupEnabled = false }, null)
            .Open.Should().ContainSingle();

    [Fact]
    public void Evaluate_DismissedIdInDifferentCasing_StillMatches()
        => RecommendationEngine.Evaluate(Ideal with { BackupEnabled = false }, ["CONFIG-BACKUP"])
            .Open.Should().BeEmpty();

    // ── ConfigTool adapter ───────────────────────────────────────────────────

    [Fact]
    public void FromConfigDocument_DefaultDocument_MapsTheOffSwitches()
    {
        // A default ConfigDocument is a brand-new install: no Graph, no listeners, everything off.
        var input = RecommendationInput.FromConfigDocument(new ConfigDocument());

        input.GraphConfigured.Should().BeFalse();
        input.EnabledListenerCount.Should().Be(0);
        input.SenderValidationEnabled.Should().BeFalse();
        input.BackupEnabled.Should().BeFalse();
        input.NdrEnabled.Should().BeFalse();
        input.UpdateCheckEnabled.Should().BeFalse();
        input.TelemetryEnabled.Should().BeFalse();
        input.HasAdminNotificationRecipients.Should().BeFalse();
        input.LogLevel.Should().Be("Information");
    }

    [Fact]
    public void FromConfigDocument_ConfiguredDocument_MatchesTheServiceSideSnapshot()
    {
        var doc = new ConfigDocument();
        doc.GraphApi.TenantId = "tenant";
        doc.GraphApi.ClientId = "client";
        doc.GraphApi.ClientCertificateThumbprint = "AABB";
        // The plain listener takes no credentials, so it is not a credential-exposure problem.
        doc.Servers.Add(new ConfigDocument.ServerEntry { Enabled = true, Name = "SMTP", Port = 25, Mode = "Plain", AuthMode = "None" });
        doc.Servers.Add(new ConfigDocument.ServerEntry { Enabled = true, Name = "Submission", Port = 587, Mode = "StartTls", AuthMode = "Required" });
        doc.SenderValidation.SvEnabled = true;
        doc.Backup.BackupEnabled = true;
        doc.Ndr.NdrEnabled = true;
        doc.Monitoring.UpdateCheckEnabled = true;
        doc.Monitoring.TelemetryEnabled = true;
        doc.Notification.RecipientAddresses.Add("ops@corp.com");

        var input = RecommendationInput.FromConfigDocument(doc);

        input.GraphConfigured.Should().BeTrue();
        input.GraphUsesCertificate.Should().BeTrue();
        input.GraphUsesClientSecret.Should().BeFalse();
        input.EnabledListenerCount.Should().Be(2);
        input.HasTlsListener.Should().BeTrue();
        RecommendationEngine.Evaluate(input).Open.Should().BeEmpty();
        DoneIdsFor(input).Should().HaveCount(9);
    }

    [Fact]
    public void FromConfigDocument_DisabledTlsListener_DoesNotCountAsTlsCoverage()
    {
        // A listener that exists but is switched off protects nothing.
        var doc = new ConfigDocument();
        doc.Servers.Add(new ConfigDocument.ServerEntry { Enabled = true, Name = "SMTP", Port = 25, Mode = "Plain", AuthMode = "Optional" });
        doc.Servers.Add(new ConfigDocument.ServerEntry { Enabled = false, Name = "SMTPS", Port = 465, Mode = "Ssl", AuthMode = "Optional" });

        var input = RecommendationInput.FromConfigDocument(doc);

        input.EnabledListenerCount.Should().Be(1);
        input.HasTlsListener.Should().BeFalse();
        input.PlaintextAuthListeners.Should().Equal("SMTP (port 25)");
        IdsFor(input).Should().Contain(RecommendationIds.TlsListener);
    }

    [Theory]
    [InlineData("Optional")]
    [InlineData("Required")]
    public void FromConfigDocument_PlainListenerAcceptingAuth_IsFlagged(string authMode)
    {
        var doc = new ConfigDocument();
        doc.Servers.Add(new ConfigDocument.ServerEntry
            { Enabled = true, Name = "Submission", Port = 587, Mode = "Plain", AuthMode = authMode });

        RecommendationInput.FromConfigDocument(doc).PlaintextAuthListeners
            .Should().Equal("Submission (port 587)");
    }

    [Fact]
    public void FromConfigDocument_PlainListenerWithoutAuth_IsNotFlagged()
    {
        // The default port-25 relay listener takes no credentials — TLS there is a choice, not a
        // credential-exposure problem, so it must not raise the High-severity hint.
        var doc = new ConfigDocument();
        doc.Servers.Add(new ConfigDocument.ServerEntry
            { Enabled = true, Name = "SMTP (Plain)", Port = 25, Mode = "Plain", AuthMode = "None" });

        RecommendationInput.FromConfigDocument(doc).PlaintextAuthListeners.Should().BeEmpty();
    }

    [Fact]
    public void FromConfigDocument_TlsListenerWithAuth_IsNotFlagged()
    {
        var doc = new ConfigDocument();
        doc.Servers.Add(new ConfigDocument.ServerEntry
            { Enabled = true, Name = "Submission", Port = 587, Mode = "StartTls", AuthMode = "Required" });

        RecommendationInput.FromConfigDocument(doc).PlaintextAuthListeners.Should().BeEmpty();
    }
}
