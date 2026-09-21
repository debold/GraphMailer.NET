using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Tests for <see cref="GraphPermissions"/>, the shared definition of the Graph application
/// permissions GraphMailer needs.
///
/// Regression background: the required set used to be written out separately in the Entra setup
/// wizard and in the Graph monitor. 1.5.0 added Domain.Read.All and Group.Read.All, the ConfigTool
/// knew about neither, and installations upgraded from 1.4.x got an alert mail with a green
/// ConfigTool next to it. These tests pin the set itself, so a future permission cannot be added
/// to one consumer only.
/// </summary>
public sealed class GraphPermissionsTests
{
    private static string[] Names(IEnumerable<GraphAppRole> roles) => [.. roles.Select(r => r.Name)];

    // ── The required set ─────────────────────────────────────────────────────

    [Fact]
    public void Required_SenderValidationOff_IsDeliveryOnly()
    {
        var required = GraphPermissions.Required(new SenderValidationOptions { Enabled = false });

        Names(required).Should().BeEquivalentTo("Mail.Send", "Mail.ReadWrite");
    }

    [Fact]
    public void Required_SenderValidationOn_AddsUserAndDomainRead()
    {
        var required = GraphPermissions.Required(new SenderValidationOptions { Enabled = true });

        Names(required).Should().BeEquivalentTo(
            "Mail.Send", "Mail.ReadWrite", "User.Read.All", "Domain.Read.All");
    }

    /// <summary>Domain.Read.All is read on every directory sync, not only for the mailbox-less
    /// opt-in — the mail domains decide which of a mailbox's addresses may send at all.</summary>
    [Fact]
    public void Required_SenderValidationOnWithoutMailboxless_StillNeedsDomainRead()
    {
        var required = GraphPermissions.Required(
            new SenderValidationOptions { Enabled = true, AcceptMailboxlessSenders = false });

        Names(required).Should().Contain("Domain.Read.All");
        Names(required).Should().NotContain("Group.Read.All");
    }

    [Fact]
    public void Required_AcceptMailboxlessSenders_AddsGroupRead()
    {
        var required = GraphPermissions.Required(
            new SenderValidationOptions { Enabled = true, AcceptMailboxlessSenders = true });

        Names(required).Should().BeEquivalentTo(
            "Mail.Send", "Mail.ReadWrite", "User.Read.All", "Domain.Read.All", "Group.Read.All");
    }

    /// <summary>The opt-in alone must not pull in directory permissions — without validation the
    /// flag is never read, and reporting a gap would be a false alarm.</summary>
    [Fact]
    public void Required_MailboxlessWithoutValidation_IsDeliveryOnly()
    {
        var required = GraphPermissions.Required(
            new SenderValidationOptions { Enabled = false, AcceptMailboxlessSenders = true });

        Names(required).Should().BeEquivalentTo("Mail.Send", "Mail.ReadWrite");
    }

    // ── The wizard's superset ────────────────────────────────────────────────

    /// <summary>The wizard grants the full set regardless of configuration, so every permission
    /// any configuration can require must be in it — otherwise re-running it would not close the
    /// gap it is advertised to close.</summary>
    [Fact]
    public void All_CoversEveryRequirableRole()
    {
        var everything = GraphPermissions.Required(
            new SenderValidationOptions { Enabled = true, AcceptMailboxlessSenders = true });

        Names(GraphPermissions.All).Should().Contain(Names(everything));
    }

    [Fact]
    public void All_RoleIdsAreUniqueAndNonEmpty()
    {
        GraphPermissions.All.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.RoleId));
        GraphPermissions.All.Select(r => r.RoleId).Should().OnlyHaveUniqueItems();
    }

    // ── Missing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_AllGranted_IsEmpty()
    {
        var missing = GraphPermissions.Missing(
            ["Mail.Send", "Mail.ReadWrite"], new SenderValidationOptions { Enabled = false });

        missing.Should().BeEmpty();
    }

    [Fact]
    public void Missing_GapPresent_NamesOnlyTheAbsentRoles()
    {
        var missing = GraphPermissions.Missing(
            ["Mail.Send", "User.Read.All"], new SenderValidationOptions { Enabled = true });

        Names(missing).Should().BeEquivalentTo("Mail.ReadWrite", "Domain.Read.All");
    }

    /// <summary>Entra returns the roles claim in its own casing; a case-sensitive comparison
    /// would report a gap that is not there.</summary>
    [Fact]
    public void Missing_GrantedRolesInDifferentCase_CountAsGranted()
    {
        var missing = GraphPermissions.Missing(
            ["mail.send", "MAIL.READWRITE"], new SenderValidationOptions { Enabled = false });

        missing.Should().BeEmpty();
    }

    /// <summary>A permission granted but not required is not a problem to report.</summary>
    [Fact]
    public void Missing_ExtraRolesGranted_AreIgnored()
    {
        var missing = GraphPermissions.Missing(
            ["Mail.Send", "Mail.ReadWrite", "Directory.Read.All"],
            new SenderValidationOptions { Enabled = false });

        missing.Should().BeEmpty();
    }

    // ── Operator-facing formatting ───────────────────────────────────────────

    [Fact]
    public void DetailList_NamesEachRoleWithItsPurpose()
    {
        var detail = GraphPermissions.DetailList([GraphPermissions.MailReadWrite]);

        detail.Should().Be("Mail.ReadWrite (needed for attachments ≥ 3 MB)");
    }

    [Fact]
    public void NameList_JoinsBareRoleNames()
    {
        var names = GraphPermissions.NameList(
            [GraphPermissions.MailSend, GraphPermissions.DomainReadAll]);

        names.Should().Be("Mail.Send, Domain.Read.All");
    }
}
