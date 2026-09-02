using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GraphMailer.Tests.Unit.Infrastructure.Security;   // FakeTimeProvider
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

public sealed class SenderRouterTests
{
    private const string Relay = "relay@corp.com";

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    /// <summary>Directory stub: knows one mailbox and one group, nothing else.</summary>
    private static ITenantSenderDirectory Directory(
        string? mailboxAddress = null,
        string? mailboxId = null,
        string? groupAddress = null,
        string? groupId = null)
    {
        var directory = Substitute.For<ITenantSenderDirectory>();

        directory
            .TryResolveSender(Arg.Any<string>(), out Arg.Any<string>(), out Arg.Any<TenantRecipientKind>())
            .Returns(ci =>
            {
                var address = ci.ArgAt<string>(0);

                if (mailboxAddress is not null && address.Equals(mailboxAddress, StringComparison.OrdinalIgnoreCase))
                {
                    ci[1] = mailboxId ?? "mailbox-id";
                    ci[2] = TenantRecipientKind.Mailbox;
                    return true;
                }

                if (groupAddress is not null && address.Equals(groupAddress, StringComparison.OrdinalIgnoreCase))
                {
                    ci[1] = groupId ?? "group-id";
                    ci[2] = TenantRecipientKind.Group;
                    return true;
                }

                ci[1] = string.Empty;
                ci[2] = TenantRecipientKind.Mailbox;
                return false;
            });

        return directory;
    }

    private static SenderRouter CreateRouter(
        SenderRoutingOptions options,
        ITenantSenderDirectory? directory = null,
        TimeProvider? clock = null)
        => new(
            directory ?? Directory(),
            Monitor(options),
            Monitor(new SenderValidationOptions { Enabled = true }),
            NullLogger<SenderRouter>.Instance,
            clock);

    private static SenderRoutingOptions Enabled(params SenderRouteOptions[] routes) => new()
    {
        Enabled = true,
        RelayMailbox = Relay,
        Routes = [.. routes],
    };

    // =========================================================================
    // Feature disabled — behaves exactly as before the feature existed
    // =========================================================================

    [Fact]
    public void Resolve_RoutingDisabled_SendsDirectlyAsEnvelopeSender()
    {
        var sut = CreateRouter(new SenderRoutingOptions { Enabled = false, RelayMailbox = Relay });

        var route = sut.Resolve("list@corp.com");

        route.GraphUserKey.Should().Be("list@corp.com");
        route.IsRelay.Should().BeFalse();
        route.FallbackRelayMailbox.Should().BeNull();
    }

    [Fact]
    public void Resolve_RoutingDisabled_IgnoresConfiguredRoutes()
    {
        var options = new SenderRoutingOptions
        {
            Enabled = false,
            RelayMailbox = Relay,
            Routes = [new SenderRouteOptions { Sender = "pf@corp.com", Mailbox = "other@corp.com" }],
        };
        var sut = CreateRouter(options);

        sut.Resolve("pf@corp.com").GraphUserKey.Should().Be("pf@corp.com");
    }

    // =========================================================================
    // Explicit routes
    // =========================================================================

    [Fact]
    public void Resolve_ExplicitRoute_ExactAddress_RelaysThroughConfiguredMailbox()
    {
        var sut = CreateRouter(Enabled(
            new SenderRouteOptions { Sender = "pf@corp.com", Mailbox = "pfrelay@corp.com" }));

        var route = sut.Resolve("pf@corp.com");

        route.GraphUserKey.Should().Be("pfrelay@corp.com");
        route.IsRelay.Should().BeTrue();
        route.FallbackRelayMailbox.Should().BeNull("a relayed send has nowhere left to fall back to");
    }

    [Fact]
    public void Resolve_ExplicitRoute_DomainWildcard_RelaysThroughConfiguredMailbox()
    {
        var sut = CreateRouter(Enabled(
            new SenderRouteOptions { Sender = "@pf.corp.com", Mailbox = "pfrelay@corp.com" }));

        sut.Resolve("anything@pf.corp.com").GraphUserKey.Should().Be("pfrelay@corp.com");
    }

    [Fact]
    public void Resolve_ExplicitRoute_DoesNotMatchSubdomain()
    {
        var sut = CreateRouter(Enabled(
            new SenderRouteOptions { Sender = "@corp.com", Mailbox = "pfrelay@corp.com" }));

        sut.Resolve("spoof@evil.corp.com").GraphUserKey.Should().NotBe("pfrelay@corp.com");
    }

    [Fact]
    public void Resolve_ExplicitRoute_IncompleteEntry_IsIgnored()
    {
        var sut = CreateRouter(Enabled(
            new SenderRouteOptions { Sender = "pf@corp.com", Mailbox = "   " }));

        sut.Resolve("pf@corp.com").IsRelay.Should().BeFalse();
    }

    // =========================================================================
    // Directory hits
    // =========================================================================

    [Fact]
    public void Resolve_KnownMailbox_SendsDirectlyAsObjectId()
    {
        var sut = CreateRouter(
            Enabled(),
            Directory(mailboxAddress: "alice@corp.com", mailboxId: "id-alice"));

        var route = sut.Resolve("alice@corp.com");

        route.GraphUserKey.Should().Be("id-alice");
        route.IsRelay.Should().BeFalse();
        route.FallbackRelayMailbox.Should().Be(Relay, "a direct send can still turn out to have no mailbox");
    }

    [Fact]
    public void Resolve_KnownGroup_IsRelayed_NeverUsesTheGroupObjectId()
    {
        var sut = CreateRouter(
            Enabled(),
            Directory(groupAddress: "list@corp.com", groupId: "id-list"));

        var route = sut.Resolve("list@corp.com");

        // /users/{groupId}/sendMail answers "Group Shard is used in non-Groups URI".
        route.GraphUserKey.Should().Be(Relay);
        route.GraphUserKey.Should().NotBe("id-list");
        route.IsRelay.Should().BeTrue();
    }

    [Fact]
    public void Resolve_KnownGroup_WithoutRelayMailbox_FallsBackToEnvelopeSender()
    {
        var options = new SenderRoutingOptions { Enabled = true, RelayMailbox = "" };
        var sut = CreateRouter(options, Directory(groupAddress: "list@corp.com", groupId: "id-list"));

        var route = sut.Resolve("list@corp.com");

        route.GraphUserKey.Should().Be("list@corp.com");
        route.IsRelay.Should().BeFalse();
    }

    // =========================================================================
    // Unresolved senders
    // =========================================================================

    [Fact]
    public void Resolve_UnknownSender_CarriesRelayAsFallback()
    {
        var sut = CreateRouter(Enabled());

        var route = sut.Resolve("pf@corp.com");

        route.GraphUserKey.Should().Be("pf@corp.com");
        route.IsRelay.Should().BeFalse();
        route.FallbackRelayMailbox.Should().Be(Relay);
    }

    // =========================================================================
    // Learned "no mailbox" markers
    // =========================================================================

    [Fact]
    public void MarkMailboxUnavailable_NextResolve_RelaysWithoutTryingDirectlyAgain()
    {
        var sut = CreateRouter(Enabled());

        sut.MarkMailboxUnavailable("pf@corp.com");
        var route = sut.Resolve("pf@corp.com");

        route.GraphUserKey.Should().Be(Relay);
        route.IsRelay.Should().BeTrue();
    }

    [Fact]
    public void MarkMailboxUnavailable_IsCaseInsensitive()
    {
        var sut = CreateRouter(Enabled());

        sut.MarkMailboxUnavailable("PF@corp.com");

        sut.Resolve("pf@CORP.com").IsRelay.Should().BeTrue();
    }

    [Fact]
    public void MarkMailboxUnavailable_AfterTtlExpires_SendsDirectlyAgain()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));
        var options = new SenderRoutingOptions
        {
            Enabled = true,
            RelayMailbox = Relay,
            RelayCacheMinutes = 30,
        };
        var sut = CreateRouter(options, clock: clock);

        sut.MarkMailboxUnavailable("pf@corp.com");
        sut.Resolve("pf@corp.com").IsRelay.Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(31));

        // Expired: the sender may have been given a mailbox in the meantime, so try direct again.
        var route = sut.Resolve("pf@corp.com");
        route.IsRelay.Should().BeFalse();
        route.FallbackRelayMailbox.Should().Be(Relay);
    }

    [Fact]
    public void MarkMailboxUnavailable_RoutingDisabled_RemembersNothing()
    {
        var sut = CreateRouter(new SenderRoutingOptions { Enabled = false, RelayMailbox = Relay });

        sut.MarkMailboxUnavailable("pf@corp.com");

        sut.Resolve("pf@corp.com").IsRelay.Should().BeFalse();
    }

    // =========================================================================
    // HasExplicitRoute — used by sender validation
    // =========================================================================

    [Fact]
    public void HasExplicitRoute_MatchingRoute_IsTrue()
    {
        var sut = CreateRouter(Enabled(
            new SenderRouteOptions { Sender = "@pf.corp.com", Mailbox = "pfrelay@corp.com" }));

        sut.HasExplicitRoute("reports@pf.corp.com").Should().BeTrue();
        sut.HasExplicitRoute("reports@corp.com").Should().BeFalse();
    }

    [Fact]
    public void HasExplicitRoute_RoutingDisabled_IsFalse()
    {
        var options = new SenderRoutingOptions
        {
            Enabled = false,
            Routes = [new SenderRouteOptions { Sender = "pf@corp.com", Mailbox = "pfrelay@corp.com" }],
        };

        CreateRouter(options).HasExplicitRoute("pf@corp.com").Should().BeFalse();
    }
}
