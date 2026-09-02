using GraphMailer.Service.Services;
using Microsoft.Graph.Models;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Mapping of a Graph user object into the directory entry the sender validation works with.
/// The guest rule is security-relevant: a B2B guest carries its home organisation's address, and
/// letting that through would put a foreign domain into the derived mail-domain set — which the
/// MAIL FROM check then treats as one of ours.
/// </summary>
public sealed class GraphDirectoryGatewayTests
{
    private static User Guest() => new()
    {
        Id = "id-guest",
        UserType = "Guest",
        DisplayName = "admin-InJaeger",
        UserPrincipalName = "admin-InJaeger_partner.org#EXT#@contoso.onmicrosoft.com",
        Mail = "admin-InJaeger@partner.org",
        ProxyAddresses = ["SMTP:admin-InJaeger@partner.org"],
    };

    private static User Member() => new()
    {
        Id = "id-anna",
        UserType = "Member",
        DisplayName = "Anna Beck",
        UserPrincipalName = "anna@corp.com",
        Mail = "anna@corp.com",
        ProxyAddresses = ["SMTP:anna@corp.com", "smtp:a.beck@corp.com", "sip:anna@corp.com"],
    };

    [Fact]
    public void MapUser_Guest_IsDropped()
        => GraphDirectoryGateway.MapUser(Guest()).Should().BeNull(
            "a guest has no mailbox here and its external address would pollute the mail domains");

    [Fact]
    public void MapUser_Member_IsKept()
        => GraphDirectoryGateway.MapUser(Member()).Should().NotBeNull();

    [Fact]
    public void MapUser_GuestFlag_IsCaseInsensitive()
    {
        var guest = Guest();
        guest.UserType = "guest";

        GraphDirectoryGateway.MapUser(guest).Should().BeNull();
    }

    [Fact]
    public void MapUser_UserTypeAbsent_IsKept()
    {
        // Some directory objects (shared mailboxes, synced accounts) report no userType at all.
        var user = Member();
        user.UserType = null;

        GraphDirectoryGateway.MapUser(user).Should().NotBeNull();
    }

    [Fact]
    public void MapUser_FlattensMailAndSmtpProxyAddresses()
    {
        var mapped = GraphDirectoryGateway.MapUser(Member())!;

        mapped.SmtpAddresses.Should().BeEquivalentTo(["anna@corp.com", "a.beck@corp.com"]);
        mapped.SmtpAddresses.Should().NotContain(a => a.StartsWith("sip:"),
            "non-SMTP proxy schemes are not sender addresses");
    }

    [Fact]
    public void MapUser_UpnInANonMailDomain_IsNotASenderAddress()
    {
        // An account synced from on-premises AD commonly keeps the internal AD suffix as its
        // sign-in name. That suffix is not a mail domain — nothing can be sent from it, and
        // accepting it at MAIL FROM would only defer the rejection to Microsoft 365.
        var synced = new User
        {
            Id = "id-sync",
            UserType = "Member",
            UserPrincipalName = "user1@ad.corp.com",
            Mail = "user1@corp.com",
            ProxyAddresses = ["SMTP:user1@corp.com"],
        };

        var mapped = GraphDirectoryGateway.MapUser(synced)!;

        mapped.SmtpAddresses.Should().BeEquivalentTo(["user1@corp.com"]);
        mapped.UserPrincipalName.Should().Be("user1@ad.corp.com", "it is still the sign-in name");
    }

    [Fact]
    public void MapUser_PrimaryAddressIsMail_NotTheUpn()
    {
        var synced = new User
        {
            Id = "id-sync",
            UserType = "Member",
            UserPrincipalName = "user1@ad.corp.com",
            Mail = "user1@corp.com",
            ProxyAddresses = ["SMTP:user1@corp.com", "smtp:alias@corp.com"],
        };

        GraphDirectoryGateway.MapUser(synced)!.PrimaryOrFirstAddress().Should().Be("user1@corp.com");
    }

    [Fact]
    public void MapUser_CarriesDisplayNameAndKind()
    {
        var mapped = GraphDirectoryGateway.MapUser(Member())!;

        mapped.DisplayName.Should().Be("Anna Beck");
        mapped.Kind.Should().Be(TenantRecipientKind.Mailbox);
    }

    [Fact]
    public void MapUser_WithoutId_IsDropped()
    {
        var user = Member();
        user.Id = null;

        GraphDirectoryGateway.MapUser(user).Should().BeNull();
    }

    [Fact]
    public void MapUser_NoMailAndNoProxyAddresses_IsDropped()
    {
        // Admin accounts and licence placeholders look like this. They own no mailbox, so sending
        // as them can never work — a clean 550 at MAIL FROM beats a relay attempt that fails.
        var placeholder = new User
        {
            Id = "id-adm",
            UserType = "Member",
            DisplayName = "ADM-Engel",
            UserPrincipalName = "adm-eng@contoso.onmicrosoft.com",
        };

        GraphDirectoryGateway.MapUser(placeholder).Should().BeNull();
    }

    [Fact]
    public void MapUser_MailButNoProxyAddresses_IsKept()
    {
        var user = new User
        {
            Id = "id-x",
            UserType = "Member",
            UserPrincipalName = "x@corp.com",
            Mail = "x@corp.com",
        };

        GraphDirectoryGateway.MapUser(user).Should().NotBeNull();
    }

    [Fact]
    public void MapUser_ProxyAddressesButNoMail_IsKept()
    {
        // Shared and room mailboxes show up this way.
        var shared = new User
        {
            Id = "id-s",
            UserType = "Member",
            UserPrincipalName = "myshared@corp.com",
            ProxyAddresses = ["SMTP:shared@corp.com", "smtp:myshared@corp.com"],
        };

        // Both come from proxyAddresses here; the UPN happens to match one of them.
        GraphDirectoryGateway.MapUser(shared)!.SmtpAddresses
            .Should().BeEquivalentTo(["myshared@corp.com", "shared@corp.com"]);
    }

    [Fact]
    public void MapUser_OnlyNonSmtpProxyAddresses_IsDropped()
    {
        var user = new User
        {
            Id = "id-sip",
            UserType = "Member",
            UserPrincipalName = "u@corp.com",
            ProxyAddresses = ["sip:u@corp.com", "x500:/o=…"],
        };

        GraphDirectoryGateway.MapUser(user).Should().BeNull(
            "a SIP or X500 address is not a mail address");
    }
}
