using GraphMailer.ConfigTool.Views;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Validation rule of the Backup page's password fields. The critical case: an
/// ENABLED schedule without a password must be an error — the service pauses
/// scheduled backups in that state with only a log warning, so without this rule
/// the operator could save a schedule that silently never produces a backup.
/// </summary>
public sealed class BackupPasswordRuleTests
{
    [Fact]
    public void EnabledSchedule_NoPassword_IsAnError()
    {
        var error = BackupPage.ValidatePasswordRule(scheduleEnabled: true, password: "", confirm: "");

        error.Should().NotBeNull("an enabled schedule without a password never runs — the operator must see this");
        error.Should().Contain("required for scheduled backups");
    }

    [Fact]
    public void DisabledSchedule_NoPassword_IsValid()
    {
        BackupPage.ValidatePasswordRule(scheduleEnabled: false, password: "", confirm: "")
            .Should().BeNull("without a schedule the password is only needed for manual backups, which enforce it themselves");
    }

    [Fact]
    public void EnabledSchedule_ValidPassword_IsValid()
    {
        BackupPage.ValidatePasswordRule(scheduleEnabled: true, password: "s3cure-pass", confirm: "s3cure-pass")
            .Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TooShortPassword_IsAnError_RegardlessOfSchedule(bool enabled)
    {
        BackupPage.ValidatePasswordRule(enabled, password: "short", confirm: "short")
            .Should().Contain("at least");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MismatchedConfirmation_IsAnError_RegardlessOfSchedule(bool enabled)
    {
        BackupPage.ValidatePasswordRule(enabled, password: "s3cure-pass", confirm: "s3cure-typo")
            .Should().Contain("do not match");
    }

    // ── Email backups ─────────────────────────────────────────────────────
    // The service silently skips the email step when the recipient list is empty,
    // and cannot send at all without the notification sender address (no fallback
    // account in Graph app-only auth) — both must be visible errors on the page.

    [Fact]
    public void EmailBackupsEnabled_NoRecipients_IsAnError()
    {
        BackupPage.ValidateEmailBackupRule(emailEnabled: true, recipientCount: 0, senderConfigured: true)
            .Should().Contain("at least one recipient");
    }

    [Fact]
    public void EmailBackupsEnabled_NoSender_IsAnError_PointingToTheNotificationsPage()
    {
        // The dependency is cross-page and otherwise invisible: emailed backups use
        // the sender address configured on the Notifications page.
        BackupPage.ValidateEmailBackupRule(emailEnabled: true, recipientCount: 1, senderConfigured: false)
            .Should().Contain("Notifications page");
    }

    [Fact]
    public void EmailBackupsEnabled_RecipientAndSenderPresent_IsValid()
    {
        BackupPage.ValidateEmailBackupRule(emailEnabled: true, recipientCount: 1, senderConfigured: true)
            .Should().BeNull();
    }

    [Fact]
    public void EmailBackupsDisabled_NothingElseMatters_IsValid()
    {
        BackupPage.ValidateEmailBackupRule(emailEnabled: false, recipientCount: 0, senderConfigured: false)
            .Should().BeNull();
    }
}
