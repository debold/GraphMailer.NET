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
}
