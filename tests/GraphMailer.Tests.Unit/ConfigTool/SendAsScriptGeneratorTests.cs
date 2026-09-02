using GraphMailer.ConfigTool.Services;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// The generated Exchange Online PowerShell script is the only way GraphMailer can hand the
/// SendAs permission to an admin — Graph can neither grant nor read it. So the script has to be
/// correct on its own: it is copied out of the tool and run by hand against a live tenant, where
/// recipient types that cannot be listed, inconsistent objects and mail users pointing outside the
/// accepted domains are all normal.
/// </summary>
public sealed class SendAsScriptGeneratorTests
{
    private static readonly DateTime Generated = new(2026, 8, 31, 14, 30, 0, DateTimeKind.Utc);

    private static string All(string relay = "relay@corp.com")
        => SendAsScriptGenerator.GenerateForAllObjects(relay, "1.5.0.1090", Generated);

    private static string Only(params string[] objects)
        => SendAsScriptGenerator.GenerateForObjects(
            "relay@corp.com", "1.5.0.1090", Generated, objects);

    // =========================================================================
    // Shared scaffolding
    // =========================================================================

    [Fact]
    public void Generate_SetsTheRelayMailboxAsTheTrustee()
        => All("svc-relay@corp.com").Should().Contain("$Relay = 'svc-relay@corp.com'");

    [Fact]
    public void Generate_GrantsSendAsAndNothingElse()
    {
        var script = All();

        script.Should().Contain("-AccessRights SendAs");
        script.Should().NotContain("FullAccess", "SendAs alone is enough when sending via /users/{relay}");
    }

    [Fact]
    public void Generate_ChecksTheExistingPermissionFirst_SoItStaysReRunnable()
    {
        // Add-RecipientPermission errors out when the right is already there.
        var script = All();

        script.Should().Contain("Get-RecipientPermission -Identity $Recipient.Identity -Trustee $Relay");
        script.Should().Contain("if ($existing)");
    }

    [Fact]
    public void Generate_SkipsRecipientsOutsideTheAcceptedDomains()
    {
        // Exchange refuses those and says so per object; most mail users are in this group.
        var script = All();

        script.Should().Contain("Get-AcceptedDomain");
        script.Should().Contain("$Accepted -notcontains $domain");
        script.Should().Contain("is not an accepted domain");
    }

    [Fact]
    public void Generate_ChecksTheExternalAddressToo_NotJustThePrimaryOne()
    {
        // A mail user can have an accepted primary address while the external address it forwards
        // to is outside the tenant — Exchange then rejects the grant over that second address, and
        // checking only the primary one turns a clean skip into a failure.
        var script = All();

        script.Should().Contain("$Recipient.PrimarySmtpAddress, $Recipient.ExternalEmailAddress");
    }

    [Fact]
    public void Generate_StripsTheProxyAddressPrefixBeforeReadingTheDomain()
        // ExternalEmailAddress comes back as "SMTP:user@example.com".
        => All().Should().Contain("(($text -split ':')[-1] -split '@')[-1]");

    [Fact]
    public void Generate_SilencesExchangesOwnWarnings()
    {
        // Exchange repeats its object-consistency complaint on the warning stream, verbatim next
        // to the failure the script already reports itself.
        var script = All();

        script.Should().Contain("-ErrorAction Stop -WarningAction SilentlyContinue | Out-Null");

        // Every enumeration, whatever the column padding between cmdlet and switches.
        foreach (var line in script.Split('\n').Where(l => l.Contains("-ResultSize Unlimited")))
            line.Should().Contain("-ErrorAction Stop -WarningAction SilentlyContinue");
    }

    [Fact]
    public void Generate_SurvivesOneInconsistentObject()
    {
        // A group Exchange reports as corrupt must not stop the remaining ones.
        All().Should().Contain("Write-Warning \"failed $name <$address>");
    }

    [Fact]
    public void Generate_SurvivesARecipientTypeThatCannotBeListed()
    {
        // Get-MailPublicFolder fails outright where public folders live on-premises.
        var script = All();

        script.Should().Contain("could not be listed");
        script.Should().Contain("-ErrorAction Stop");
    }

    [Fact]
    public void Generate_SuppressesTheCmdletOutput_SoTheReadbackIsTheOnlyTable()
        => All().Should().Contain("-Confirm:$false -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null");

    [Fact]
    public void Generate_EndsWithAReadbackOfWhatIsActuallyGranted()
    {
        // Graph cannot report SendAs, so this listing is the operator's only confirmation.
        All().TrimEnd().Should().EndWith(
            "Get-RecipientPermission -Trustee $Relay | Format-Table Identity, Trustee, AccessRights");
    }

    [Fact]
    public void Generate_ReportsASummary()
        => All().Should().Contain("granted $script:Granted, already in place $script:Existing");

    [Fact]
    public void Generate_MentionsTheReplicationDelay()
        => All().Should().Contain("up to an hour");

    [Fact]
    public void Generate_StampsVersionAndTimestamp()
    {
        var script = All();

        script.Should().Contain("2026-08-31 14:30 UTC");
        script.Should().Contain("1.5.0.1090");
    }

    // =========================================================================
    // All-objects variant
    // =========================================================================

    [Fact]
    public void GenerateForAllObjects_CoversAllThreeSenderTypes()
    {
        var script = All();

        script.Should().Contain("Get-DistributionGroup");
        script.Should().Contain("Get-MailPublicFolder");
        script.Should().Contain("Get-MailUser");
    }

    [Fact]
    public void GenerateForAllObjects_CoversEveryGroupTypeExchangeSplitsApart()
    {
        // Graph reports every mail-enabled group in one collection, so the sender directory
        // recognises all of them; Exchange splits them across three cmdlets. Get-DistributionGroup
        // does not return Microsoft 365 groups — the kind behind a Team — so leaving it at that
        // would accept such a sender and then fail to deliver it.
        var script = All();

        script.Should().Contain("Get-UnifiedGroup");
        script.Should().Contain("Get-DynamicDistributionGroup");
    }

    [Fact]
    public void GenerateForAllObjects_HasNoHardCodedObjectList()
        // The point of this variant is that objects created later are picked up on a re-run.
        => All().Should().NotContain("$Objects");

    // =========================================================================
    // Selected-objects variant
    // =========================================================================

    [Fact]
    public void GenerateForObjects_ListsExactlyTheGivenObjects()
    {
        var script = Only("sales@corp.com", "archive-pf@corp.com");

        script.Should().Contain("'sales@corp.com'");
        script.Should().Contain("'archive-pf@corp.com'");
    }

    [Fact]
    public void GenerateForObjects_ResolvesEachEntryAndReportsUnknownOnes()
    {
        var script = Only("sales@corp.com");

        script.Should().Contain("Get-Recipient -Identity $entry");
        script.Should().Contain("Write-Warning \"not found: $entry\"");
    }

    [Fact]
    public void GenerateForObjects_DoesNotEnumerateTheWholeTenant()
        => Only("sales@corp.com").Should().NotContain("Get-DistributionGroup");

    [Fact]
    public void GenerateForObjects_TrimsBlankAndDuplicateEntries()
    {
        var script = Only("sales@corp.com", "  ", "SALES@corp.com", "");

        script.Should().Contain("'sales@corp.com'");
        script.Should().NotContain("'SALES@corp.com'");
        script.Should().NotContain("''");
    }

    [Fact]
    public void GenerateForObjects_EscapesQuotesInAnAddress()
        // A quote would otherwise break out of the PowerShell string literal.
        => Only("o'brien@corp.com").Should().Contain("'o''brien@corp.com'");

    [Fact]
    public void GenerateForAllObjects_EscapesQuotesInTheRelayMailbox()
        => All("o'relay@corp.com").Should().Contain("$Relay = 'o''relay@corp.com'");

    // =========================================================================
    // The script is run by hand — a syntax error would only surface at the prompt
    // =========================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Generate_BracesAndParenthesesAreBalanced(bool allObjects)
    {
        var script = allObjects ? All() : Only("sales@corp.com", "archive-pf@corp.com");

        Balance(script, '{', '}').Should().Be(0);
        Balance(script, '(', ')').Should().Be(0);
    }

    private static int Balance(string text, char open, char close)
        => text.Count(c => c == open) - text.Count(c => c == close);
}
