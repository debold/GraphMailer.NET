using GraphMailer.Tests.Unit;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.ODataErrors;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GraphMailer.Tests.Unit.Services;

public sealed class TenantSenderDirectoryTests
{
    private static readonly TenantUser Alice = new(
        Id: "user-id-alice",
        UserPrincipalName: "alice@corp.com",
        AccountEnabled: true,
        SmtpAddresses: ["alice@corp.com", "alias.alice@corp.com"]);

    private static readonly TenantUser SharedBox = new(
        Id: "user-id-shared",
        UserPrincipalName: "shared@corp.com",
        AccountEnabled: false,   // shared mailboxes have sign-in disabled
        SmtpAddresses: ["shared@corp.com"]);

    // =========================================================================
    // Helpers
    // =========================================================================

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private static GraphApiOptions ConfiguredGraph() => new()
    {
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "s3cr3t",
    };

    private static TenantSenderDirectory CreateDirectory(
        IGraphDirectoryGateway? gateway = null,
        SenderValidationOptions? options = null,
        GraphApiOptions? graphOptions = null,
        IAdminNotificationService? notify = null)
        => new(
            gateway ?? Substitute.For<IGraphDirectoryGateway>(),
            Monitor(options ?? new SenderValidationOptions { Enabled = true }),
            Monitor(graphOptions ?? ConfiguredGraph()),
            notify ?? Substitute.For<IAdminNotificationService>(),
            NullLogger<TenantSenderDirectory>.Instance);

    // =========================================================================
    // Null reverse path
    // =========================================================================

    [Fact]
    public async Task ValidateAsync_NullReversePath_AlwaysValid_NoGatewayCall()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        var sut = CreateDirectory(gateway);

        var result = await sut.ValidateAsync("@");

        result.Should().Be(SenderLookupResult.Valid);
        await gateway.DidNotReceiveWithAnyArgs().FindBySmtpAddressAsync(default!, default);
    }

    // =========================================================================
    // Full sync + positive cache
    // =========================================================================

    [Fact]
    public async Task ValidateAsync_AfterRefresh_UpnAndAliasAreValid()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns([Alice, SharedBox]);
        var sut = CreateDirectory(gateway);

        await sut.RefreshAsync();

        (await sut.ValidateAsync("alice@corp.com")).Should().Be(SenderLookupResult.Valid);
        (await sut.ValidateAsync("ALIAS.ALICE@corp.com")).Should().Be(SenderLookupResult.Valid,
            "aliases must match case-insensitively");
        await gateway.DidNotReceiveWithAnyArgs().FindBySmtpAddressAsync(default!, default);
    }

    [Fact]
    public async Task ValidateAsync_SharedMailbox_AccountDisabled_IsValid()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([SharedBox]);
        var sut = CreateDirectory(gateway);

        await sut.RefreshAsync();

        (await sut.ValidateAsync("shared@corp.com")).Should().Be(SenderLookupResult.Valid);
    }

    [Fact]
    public async Task RefreshAsync_Success_ReportsCounts()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice, SharedBox]);
        var sut = CreateDirectory(gateway);

        var result = await sut.RefreshAsync();

        result.Success.Should().BeTrue();
        result.UserCount.Should().Be(2);
        result.AddressCount.Should().Be(3, "Alice has 2 addresses, the shared mailbox 1");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_GatewayThrows_ReportsFailureWithError()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Graph down"));
        var sut = CreateDirectory(gateway);

        var result = await sut.RefreshAsync();

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Graph down");
    }

    [Fact]
    public async Task RefreshAsync_ReplacesCache_RemovedUserNoLongerResolves()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        IReadOnlyList<TenantUser> firstSync = [Alice];
        IReadOnlyList<TenantUser> secondSync = [];
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(firstSync, secondSync);
        var sut = CreateDirectory(gateway);

        await sut.RefreshAsync();
        sut.TryResolveGraphUserKey("alice@corp.com", out _).Should().BeTrue();

        await sut.RefreshAsync();   // second sync: tenant returns no users
        sut.TryResolveGraphUserKey("alice@corp.com", out _).Should().BeFalse();
    }

    // =========================================================================
    // On-demand lookup
    // =========================================================================

    [Fact]
    public async Task ValidateAsync_CacheMiss_OnDemandHit_PopulatesCache()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.FindBySmtpAddressAsync("alias.alice@corp.com", Arg.Any<CancellationToken>())
            .Returns(Alice);
        var sut = CreateDirectory(gateway);

        (await sut.ValidateAsync("alias.alice@corp.com")).Should().Be(SenderLookupResult.Valid);

        // second call must come from the cache — and the UPN is now cached too
        (await sut.ValidateAsync("alias.alice@corp.com")).Should().Be(SenderLookupResult.Valid);
        (await sut.ValidateAsync("alice@corp.com")).Should().Be(SenderLookupResult.Valid);
        await gateway.ReceivedWithAnyArgs(1).FindBySmtpAddressAsync(default!, default);
    }

    [Fact]
    public async Task ValidateAsync_UnknownSender_NegativeCached()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TenantUser?)null);
        var sut = CreateDirectory(gateway);

        (await sut.ValidateAsync("ghost@corp.com")).Should().Be(SenderLookupResult.Unknown);
        (await sut.ValidateAsync("ghost@corp.com")).Should().Be(SenderLookupResult.Unknown);

        await gateway.ReceivedWithAnyArgs(1).FindBySmtpAddressAsync(default!, default);
    }

    [Fact]
    public async Task ValidateAsync_GraphNotConfigured_Indeterminate_NoGatewayCall()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        var sut = CreateDirectory(gateway, graphOptions: new GraphApiOptions());

        (await sut.ValidateAsync("anyone@corp.com")).Should().Be(SenderLookupResult.Indeterminate);
        await gateway.DidNotReceiveWithAnyArgs().FindBySmtpAddressAsync(default!, default);
    }

    // =========================================================================
    // Failure semantics
    // =========================================================================

    [Fact]
    public async Task ValidateAsync_GatewayThrows_Indeterminate_SingleAdminNotification()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Graph down"));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateDirectory(gateway, notify: notify);

        (await sut.ValidateAsync("a@corp.com")).Should().Be(SenderLookupResult.Indeterminate);
        (await sut.ValidateAsync("b@corp.com")).Should().Be(SenderLookupResult.Indeterminate);

        await notify.ReceivedWithAnyArgs(1).NotifyGraphApiErrorAsync(default!, default);
    }

    [Fact]
    public async Task ValidateAsync_GraphRecoversAfterOutage_NotifiesAgainOnNextOutage()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down"));
        var notify = Substitute.For<IAdminNotificationService>();
        var sut = CreateDirectory(gateway, notify: notify);

        await sut.ValidateAsync("a@corp.com");                       // outage 1 → notify

        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Alice);                                          // recovery resets the flag
        await sut.ValidateAsync("alice@corp.com");

        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down again"));
        await sut.ValidateAsync("b@corp.com");                       // outage 2 → notify again

        await notify.ReceivedWithAnyArgs(2).NotifyGraphApiErrorAsync(default!, default);
    }

    // =========================================================================
    // TryResolveGraphUserKey
    // =========================================================================

    [Fact]
    public async Task TryResolveGraphUserKey_Alias_ReturnsObjectId()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        var sut = CreateDirectory(gateway);

        await sut.RefreshAsync();

        sut.TryResolveGraphUserKey("alias.alice@corp.com", out var key).Should().BeTrue();
        key.Should().Be("user-id-alice");
    }

    [Fact]
    public async Task TryResolveGraphUserKey_FeatureDisabled_ReturnsFalse()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        var sut = CreateDirectory(gateway, options: new SenderValidationOptions { Enabled = false });

        await sut.RefreshAsync();

        sut.TryResolveGraphUserKey("alice@corp.com", out _).Should().BeFalse(
            "with the feature disabled the send path must behave exactly as before");
    }

    // =========================================================================
    // Mail-enabled groups (SenderValidation.IncludeGroups)
    // =========================================================================

    private static readonly TenantUser SalesList = new(
        Id: "group-id-sales",
        UserPrincipalName: "sales@corp.com",
        AccountEnabled: true,
        SmtpAddresses: ["sales@corp.com", "vertrieb@corp.com"],
        Kind: TenantRecipientKind.Group);

    private static SenderValidationOptions WithGroups() =>
        new() { Enabled = true, AcceptMailboxlessSenders = true };

    [Fact]
    public async Task RefreshAsync_IncludeGroupsDisabled_DoesNotQueryGroups()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        var sut = CreateDirectory(gateway);

        await sut.RefreshAsync();

        await gateway.DidNotReceiveWithAnyArgs().GetAllMailEnabledGroupsAsync(default);
    }

    [Fact]
    public async Task RefreshAsync_IncludeGroupsEnabled_GroupAddressesValidate()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([SalesList]);
        var sut = CreateDirectory(gateway, options: WithGroups());

        await sut.RefreshAsync();

        (await sut.ValidateAsync("sales@corp.com")).Should().Be(SenderLookupResult.Valid);
        (await sut.ValidateAsync("vertrieb@corp.com")).Should().Be(SenderLookupResult.Valid);
    }

    [Fact]
    public async Task RefreshAsync_IncludeGroupsEnabled_ReportsGroupCount()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([SalesList]);
        var sut = CreateDirectory(gateway, options: WithGroups());

        var result = await sut.RefreshAsync();

        result.GroupCount.Should().Be(1);
        result.AddressCount.Should().Be(4, "Alice has 2 addresses, the group 2");
    }

    [Fact]
    public async Task TryResolveGraphUserKey_GroupSender_ReturnsFalse()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([SalesList]);
        var sut = CreateDirectory(gateway, options: WithGroups());
        await sut.RefreshAsync();

        // A group object id is not a valid sendMail user key — /users/{groupId}/sendMail
        // answers "Group Shard is used in non-Groups URI".
        sut.TryResolveGraphUserKey("sales@corp.com", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryResolveSender_GroupSender_ReportsTheGroupKind()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([SalesList]);
        var sut = CreateDirectory(gateway, options: WithGroups());
        await sut.RefreshAsync();

        sut.TryResolveSender("sales@corp.com", out var key, out var kind).Should().BeTrue();
        key.Should().Be("group-id-sales");
        kind.Should().Be(TenantRecipientKind.Group);
    }

    [Fact]
    public async Task RefreshAsync_AddressOnBothAUserAndAGroup_KeepsTheMailbox()
    {
        // Only a mailbox has a usable sendMail user key, so it is the better answer.
        var shared = new TenantUser("user-id", "sales@corp.com", true, ["sales@corp.com"]);
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([shared]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([SalesList]);
        var sut = CreateDirectory(gateway, options: WithGroups());

        await sut.RefreshAsync();

        sut.TryResolveGraphUserKey("sales@corp.com", out var key).Should().BeTrue();
        key.Should().Be("user-id");
    }

    [Fact]
    public async Task ValidateAsync_UnknownAddress_QueriesGroupsOnlyWhenIncludeGroupsIsOn()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((TenantUser?)null);
        var sut = CreateDirectory(gateway);

        await sut.ValidateAsync("sales@corp.com");

        await gateway.DidNotReceiveWithAnyArgs().FindGroupBySmtpAddressAsync(default!, default);
    }

    [Fact]
    public async Task ValidateAsync_UserMiss_FallsBackToTheGroupLookup()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((TenantUser?)null);
        gateway.FindGroupBySmtpAddressAsync("sales@corp.com", Arg.Any<CancellationToken>())
               .Returns(SalesList);
        var sut = CreateDirectory(gateway, options: WithGroups());

        var result = await sut.ValidateAsync("sales@corp.com");

        result.Should().Be(SenderLookupResult.Valid);
    }

    // =========================================================================
    // Tenant mail domains, derived from the directory itself
    // =========================================================================

    private static SenderValidationOptions WithDomains() =>
        new() { Enabled = true, AcceptMailboxlessSenders = true };

    /// <summary>Gateway reporting the given verified tenant mail domains. Every lookup misses.</summary>
    private static IGraphDirectoryGateway GatewayWithDomains(params string[] domains)
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.GetVerifiedMailDomainsAsync(Arg.Any<CancellationToken>()).Returns(domains);
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((TenantUser?)null);
        gateway.FindGroupBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((TenantUser?)null);
        return gateway;
    }

    [Fact]
    public void ToDomainSuffixes_AddsTheLeadingAt()
        => TenantSenderDirectory.ToDomainSuffixes(["corp.com", "corp.de"])
            .Should().BeEquivalentTo(["@corp.com", "@corp.de"]);

    [Fact]
    public void ToDomainSuffixes_KeepsTheCoexistenceDomain()
        // The one that matters most in a hybrid tenant, and the one no UPN ever carries.
        => TenantSenderDirectory.ToDomainSuffixes(["contoso.mail.onmicrosoft.com"])
            .Should().Equal(["@contoso.mail.onmicrosoft.com"]);

    [Fact]
    public void ToDomainSuffixes_IgnoresBlanksAndDeduplicates()
        => TenantSenderDirectory.ToDomainSuffixes(["corp.com", "  ", "CORP.COM", ""])
            .Should().ContainSingle();

    [Fact]
    public async Task RefreshAsync_AcceptMailboxlessDisabled_StillQueriesDomains()
    {
        // The domain list is no longer only about letting public folders through: it decides
        // which of a mailbox's synced addresses can send at all, so it is always needed.
        // Whether an unknown address in such a domain is *accepted* stays with the option,
        // and that decision sits in SmtpMailboxFilter.
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetVerifiedMailDomainsAsync(Arg.Any<CancellationToken>()).Returns(["corp.com"]);
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((TenantUser?)null);
        var sut = CreateDirectory(gateway);

        var result = await sut.RefreshAsync();

        await gateway.ReceivedWithAnyArgs().GetVerifiedMailDomainsAsync(default);
        result.DomainCount.Should().Be(1);
        result.Warning.Should().BeNull();
    }

    // =========================================================================
    // Pruning addresses to the tenant's mail domains
    // =========================================================================

    /// <summary>
    /// What directory synchronisation produces: the on-premises AD suffix is copied into
    /// proxyAddresses verbatim, next to the addresses that really work.
    /// </summary>
    private static TenantUser SyncedFromOnPrem => new(
        "id-u1", "user1@ad.corp.com", true,
        ["user1@corp.com", "user1@ad.corp.com", "user1@contoso.onmicrosoft.com"],
        DisplayName: "User1");

    [Fact]
    public void PruneToMailDomains_DropsAddressesOutsideTheMailDomains()
    {
        var pruned = TenantSenderDirectory.PruneToMailDomains(
            [SyncedFromOnPrem], ["@corp.com", "@contoso.onmicrosoft.com"]);

        pruned.Should().ContainSingle().Which.SmtpAddresses
            .Should().BeEquivalentTo(["user1@corp.com", "user1@contoso.onmicrosoft.com"]);
    }

    [Fact]
    public void PruneToMailDomains_SubdomainIsNotTheParentDomain()
        // "@corp.com" must not swallow "@ad.corp.com" — same semantics as an "@domain" filter entry.
        => TenantSenderDirectory.PruneToMailDomains([SyncedFromOnPrem], ["@corp.com"])
            .Should().ContainSingle().Which.SmtpAddresses
            .Should().Equal(["user1@corp.com"]);

    [Fact]
    public void PruneToMailDomains_RecipientLeftWithNoAddress_IsDropped()
    {
        var onlyOnPrem = new TenantUser("id-x", "x@ad.corp.com", true, ["x@ad.corp.com"]);

        TenantSenderDirectory.PruneToMailDomains([onlyOnPrem], ["@corp.com"]).Should().BeEmpty();
    }

    [Fact]
    public void PruneToMailDomains_NoDomainsKnown_PrunesNothing()
        // A missing Domain.Read.All must not empty the directory and reject every sender.
        => TenantSenderDirectory.PruneToMailDomains([SyncedFromOnPrem], [])
            .Should().ContainSingle().Which.SmtpAddresses.Should().HaveCount(3);

    [Fact]
    public void PruneToMailDomains_MatchIsCaseInsensitive()
        => TenantSenderDirectory.PruneToMailDomains(
                [new TenantUser("id-y", "y@CORP.com", true, ["y@CORP.com"])], ["@corp.com"])
            .Should().ContainSingle();

    [Fact]
    public async Task RefreshAsync_OnPremAddress_IsNotAValidSender()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([SyncedFromOnPrem]);
        gateway.GetVerifiedMailDomainsAsync(Arg.Any<CancellationToken>()).Returns(["corp.com"]);
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((TenantUser?)null);
        var sut = CreateDirectory(gateway);

        await sut.RefreshAsync();

        (await sut.ValidateAsync("user1@corp.com")).Should().Be(SenderLookupResult.Valid);
        (await sut.ValidateAsync("user1@ad.corp.com")).Should().Be(SenderLookupResult.Unknown);
    }

    [Fact]
    public async Task ValidateAsync_OnDemandHitOutsideTheMailDomains_IsNotAccepted()
    {
        // The on-demand lookup matches proxyAddresses, so it finds the object by the very
        // address the sync prunes. It has to agree with the sync rather than re-admit it.
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.GetVerifiedMailDomainsAsync(Arg.Any<CancellationToken>()).Returns(["corp.com"]);
        gateway.FindBySmtpAddressAsync("user1@ad.corp.com", Arg.Any<CancellationToken>())
               .Returns(SyncedFromOnPrem);
        var sut = CreateDirectory(gateway);
        await sut.RefreshAsync();

        (await sut.ValidateAsync("user1@ad.corp.com")).Should().Be(SenderLookupResult.Unknown);
    }

    [Fact]
    public async Task ValidateAsync_OnDemandHit_CachesOnlyTheUsableAddresses()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.GetVerifiedMailDomainsAsync(Arg.Any<CancellationToken>()).Returns(["corp.com"]);
        gateway.FindBySmtpAddressAsync("user1@corp.com", Arg.Any<CancellationToken>())
               .Returns(SyncedFromOnPrem);
        var sut = CreateDirectory(gateway);
        await sut.RefreshAsync();

        (await sut.ValidateAsync("user1@corp.com")).Should().Be(SenderLookupResult.Valid);

        sut.Recipients().Should().ContainSingle().Which.SmtpAddresses
            .Should().Equal(["user1@corp.com"]);
    }

    [Fact]
    public async Task ValidateAsync_UnknownAddressInAVerifiedDomain_ReturnsKnownDomain()
    {
        // This is what a mail-enabled public folder looks like: a real recipient Graph
        // cannot enumerate, sitting in one of our own domains.
        var sut = CreateDirectory(GatewayWithDomains("corp.com"), options: WithDomains());
        await sut.RefreshAsync();

        var result = await sut.ValidateAsync("archive-pf@corp.com");

        result.Should().Be(SenderLookupResult.KnownDomain);
    }

    [Fact]
    public async Task ValidateAsync_UnknownAddressInAForeignDomain_StaysUnknown()
    {
        var sut = CreateDirectory(GatewayWithDomains("corp.com"), options: WithDomains());
        await sut.RefreshAsync();

        var result = await sut.ValidateAsync("spoof@evil.com");

        result.Should().Be(SenderLookupResult.Unknown);
    }

    [Fact]
    public async Task ValidateAsync_SubdomainOfAVerifiedDomain_StaysUnknown()
    {
        var sut = CreateDirectory(GatewayWithDomains("corp.com"), options: WithDomains());
        await sut.RefreshAsync();

        (await sut.ValidateAsync("spoof@sub.corp.com")).Should().Be(SenderLookupResult.Unknown);
    }

    [Fact]
    public async Task ValidateAsync_NegativeCachedAddressInAVerifiedDomain_StillReportsKnownDomain()
    {
        var gateway = GatewayWithDomains("corp.com");
        var sut = CreateDirectory(gateway, options: WithDomains());
        await sut.RefreshAsync();

        (await sut.ValidateAsync("archive-pf@corp.com")).Should().Be(SenderLookupResult.KnownDomain);

        // Second call is served from the negative cache and must reach the same verdict.
        (await sut.ValidateAsync("archive-pf@corp.com")).Should().Be(SenderLookupResult.KnownDomain);
        await gateway.Received(1).FindBySmtpAddressAsync("archive-pf@corp.com", Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // Missing add-on permission — the sync must degrade, not collapse
    // =========================================================================

    private static ODataError Forbidden() => new()
    {
        ResponseStatusCode = 403,
        Error = new MainError { Code = "Authorization_RequestDenied", Message = "denied" },
    };

    [Fact]
    public async Task RefreshAsync_GroupPermissionMissing_StillSyncsTheUserDirectory()
    {
        // Switching the option on without re-running the Entra setup must not take sender
        // validation down with it — every sender would suddenly be unknown.
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden());
        var sut = CreateDirectory(gateway, options: WithGroups());

        var result = await sut.RefreshAsync();

        result.Success.Should().BeTrue("the user directory synced normally");
        result.UserCount.Should().Be(1);
        (await sut.ValidateAsync("alice@corp.com")).Should().Be(SenderLookupResult.Valid);
    }

    [Fact]
    public async Task RefreshAsync_GroupPermissionMissing_ReportsTheMissingPermission()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden());
        var sut = CreateDirectory(gateway, options: WithGroups());

        var result = await sut.RefreshAsync();

        result.Warning.Should().Contain("Group.Read.All");
        result.Warning.Should().Contain("Entra setup", "the operator needs to be told how to fix it");
    }

    [Fact]
    public async Task RefreshAsync_GroupPermissionMissing_LogsErrorNamingThePermission()
    {
        // The operator has to grant this themselves and nothing else surfaces it.
        var logger = new FakeLogger<TenantSenderDirectory>();
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden());

        var sut = new TenantSenderDirectory(
            gateway,
            Monitor(WithGroups()),
            Monitor(ConfiguredGraph()),
            Substitute.For<IAdminNotificationService>(),
            logger);

        await sut.RefreshAsync();

        logger.HasEntry(LogLevel.Error, "Group.Read.All").Should().BeTrue();
    }

    [Fact]
    public async Task RefreshAsync_GroupPermissionMissing_KeepsPreviouslyKnownGroups()
    {
        // Groups that were already synced must not turn into unknown senders because a later
        // sync could not re-read them.
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>())
               .Returns(_ => [SalesList], _ => throw Forbidden());
        var sut = CreateDirectory(gateway, options: WithGroups());

        await sut.RefreshAsync();                                     // first sync: groups load
        (await sut.ValidateAsync("sales@corp.com")).Should().Be(SenderLookupResult.Valid);

        var result = await sut.RefreshAsync();                        // second sync: permission gone

        result.Success.Should().BeTrue();
        result.GroupCount.Should().Be(1, "the known groups are carried over");
        (await sut.ValidateAsync("sales@corp.com")).Should().Be(SenderLookupResult.Valid);
    }
    [Fact]
    public async Task RefreshAsync_UserPermissionMissing_StillFailsTheWholeSync()
    {
        // User.Read.All is the hard requirement — without it there is nothing to validate against,
        // and pretending the sync worked would mark every sender unknown.
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden());
        var sut = CreateDirectory(gateway, options: WithGroups());

        var result = await sut.RefreshAsync();

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_AllPermissionsPresent_ReportsNoWarning()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([SalesList]);
        var sut = CreateDirectory(gateway,
            options: new SenderValidationOptions
            {
                Enabled = true,
                AcceptMailboxlessSenders = true,
            });

        var result = await sut.RefreshAsync();

        result.Warning.Should().BeNull();
    }

    // =========================================================================
    // Recipients() — feeds the ConfigTool's read-only directory viewer
    // =========================================================================

    [Fact]
    public async Task Recipients_ListsEachRecipientOnce_NotOncePerAlias()
    {
        // The cache is keyed by address, so Alice's two addresses are two entries in it.
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice, SharedBox]);
        var sut = CreateDirectory(gateway);

        await sut.RefreshAsync();

        sut.Recipients().Should().HaveCount(2);
    }

    [Fact]
    public async Task Recipients_IncludesGroupsWithTheirKind()
    {
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([Alice]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([SalesList]);
        var sut = CreateDirectory(gateway, options: WithGroups());

        await sut.RefreshAsync();

        sut.Recipients().Should().Contain(r => r.Kind == TenantRecipientKind.Group);
    }

    [Fact]
    public void Recipients_BeforeTheFirstSync_IsEmpty()
        => CreateDirectory().Recipients().Should().BeEmpty();

    [Fact]
    public async Task RefreshAsync_DomainPermissionMissing_KeepsThePreviousDomainList()
    {
        // Dropping the list would start rejecting public-folder senders that worked a minute ago.
        var gateway = Substitute.For<IGraphDirectoryGateway>();
        gateway.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.GetAllMailEnabledGroupsAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.FindBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((TenantUser?)null);
        gateway.FindGroupBySmtpAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((TenantUser?)null);
        IReadOnlyList<string> domains = ["corp.com"];
        gateway.GetVerifiedMailDomainsAsync(Arg.Any<CancellationToken>())
               .Returns(_ => domains, _ => throw Forbidden());
        var sut = CreateDirectory(gateway, options: WithDomains());

        await sut.RefreshAsync();
        var result = await sut.RefreshAsync();

        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("Domain.Read.All");
        (await sut.ValidateAsync("archive-pf@corp.com")).Should().Be(SenderLookupResult.KnownDomain);
    }
}
