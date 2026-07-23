using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Cross-page sender rule: Graph app-only auth needs an explicit sender mailbox — there
/// is no fallback account. Without a sender, the service silently skips admin
/// notifications, NDRs, scheduled reports and emailed backups (log-only warnings), so a
/// configuration in that state must not be saveable.
/// </summary>
public sealed class SenderAddressRuleTests
{
    [Fact]
    public void NoSender_WithAdminNotificationsEnabled_IsAnError()
    {
        var error = SenderAddressRule.Validate(
            sender: "", adminNotificationsEnabled: true, ndrEnabled: false, reportEnabled: false, backupEmailEnabled: false);

        error.Should().Contain("admin notifications");
    }

    [Fact]
    public void DocumentOverload_MasterSwitchOff_LetsTheSenderBeCleared()
    {
        // Regression: the rule used to key off the recipient list alone, so an operator who had
        // switched everything off still could not clear the sender without first emptying the list.
        var doc = new ConfigDocument
        {
            Notification = new()
            {
                NotifFrom = null,
                NotifEnabled = false,
                RecipientAddresses = ["ops@corp.com"],
            },
            Ndr = new() { NdrEnabled = false },
            Backup = new() { EmailEnabled = false },
        };

        SenderAddressRule.Validate(doc).Should().BeNull();
    }

    [Fact]
    public void DocumentOverload_MasterSwitchOnWithRecipients_StillRequiresASender()
    {
        var doc = new ConfigDocument
        {
            Notification = new()
            {
                NotifFrom = null,
                NotifEnabled = true,
                RecipientAddresses = ["ops@corp.com"],
            },
        };

        SenderAddressRule.Validate(doc).Should().Contain("admin notifications");
    }

    [Fact]
    public void DocumentOverload_MasterSwitchOnWithoutRecipients_DoesNotRequireASender()
    {
        // Nothing can be sent without a recipient, so the master switch alone must not block a save.
        var doc = new ConfigDocument
        {
            Notification = new() { NotifFrom = null, NotifEnabled = true, RecipientAddresses = [] },
        };

        SenderAddressRule.Validate(doc).Should().BeNull();
    }

    [Fact]
    public void NoSender_WithNdrEnabled_IsAnError()
    {
        SenderAddressRule.Validate("", adminNotificationsEnabled: false, ndrEnabled: true, reportEnabled: false, backupEmailEnabled: false)
            .Should().Contain("non-delivery reports");
    }

    [Fact]
    public void NoSender_WithReportEnabled_IsAnError()
    {
        SenderAddressRule.Validate(null, adminNotificationsEnabled: false, ndrEnabled: false, reportEnabled: true, backupEmailEnabled: false)
            .Should().Contain("scheduled reports");
    }

    [Fact]
    public void NoSender_WithEmailedBackups_IsAnError()
    {
        SenderAddressRule.Validate("  ", adminNotificationsEnabled: false, ndrEnabled: false, reportEnabled: false, backupEmailEnabled: true)
            .Should().Contain("emailed backups");
    }

    [Fact]
    public void NoSender_AllDependentsListed()
    {
        var error = SenderAddressRule.Validate("", true, true, true, true);

        error.Should().Contain("admin notifications")
            .And.Contain("non-delivery reports")
            .And.Contain("scheduled reports")
            .And.Contain("emailed backups");
    }

    [Fact]
    public void NoSender_NothingDependsOnIt_IsValid()
    {
        // Fresh install: no recipients, NDR/report/backup-email off — no sender needed.
        SenderAddressRule.Validate("", adminNotificationsEnabled: false, ndrEnabled: false, reportEnabled: false, backupEmailEnabled: false)
            .Should().BeNull();
    }

    [Fact]
    public void InvalidSenderFormat_IsAnError_EvenWithoutDependents()
    {
        SenderAddressRule.Validate("not-an-email", false, false, false, false)
            .Should().Contain("not a valid email address");
    }

    [Fact]
    public void ValidSender_WithAllDependents_IsValid()
    {
        SenderAddressRule.Validate("relay@contoso.com", true, true, true, true)
            .Should().BeNull();
    }

    [Fact]
    public void DocumentOverload_CoversTheCrossPageBackupEmailDependency()
    {
        // The emailed-backups toggle lives on the Backup page — the ConfigDocument
        // overload must pick it up so the save-time gate sees it.
        var doc = new ConfigDocument
        {
            Notification = new() { NotifFrom = null, RecipientAddresses = [] },
            Backup = new() { EmailEnabled = true },
        };

        SenderAddressRule.Validate(doc).Should().Contain("emailed backups");
    }
}
