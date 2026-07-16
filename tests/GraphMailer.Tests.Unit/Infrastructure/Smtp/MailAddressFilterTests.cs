using GraphMailer.Service.Infrastructure.Smtp;

namespace GraphMailer.Tests.Unit.Infrastructure.Smtp;

public sealed class MailAddressFilterTests
{
    // =========================================================================
    // IsAllowed – empty lists → allow everything
    // =========================================================================

    [Fact]
    public void IsAllowed_EmptyBothLists_AllowsAnyAddress()
    {
        MailAddressFilter.IsAllowed("user@example.com", [], []).Should().BeTrue();
        MailAddressFilter.IsAllowed("other@domain.org", [], []).Should().BeTrue();
    }

    // =========================================================================
    // IsAllowed – allow list only
    // =========================================================================

    [Fact]
    public void IsAllowed_AllowList_ExactMatch_Allowed()
    {
        MailAddressFilter.IsAllowed("alice@example.com", ["alice@example.com"], [])
            .Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_AllowList_NoMatch_Denied()
    {
        MailAddressFilter.IsAllowed("other@example.com", ["alice@example.com"], [])
            .Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_AllowList_DomainWildcard_Match_Allowed()
    {
        MailAddressFilter.IsAllowed("user@example.com", ["@example.com"], [])
            .Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_AllowList_DomainWildcard_SubdomainNotMatched_Denied()
    {
        // @example.com should NOT match user@sub.example.com
        MailAddressFilter.IsAllowed("user@sub.example.com", ["@example.com"], [])
            .Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_AllowList_DomainWildcard_OtherDomain_Denied()
    {
        MailAddressFilter.IsAllowed("user@other.com", ["@example.com"], [])
            .Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_AllowList_MultipleEntries_FirstMatches_Allowed()
    {
        MailAddressFilter.IsAllowed(
            "alice@example.com",
            ["bob@example.com", "alice@example.com", "carol@example.com"],
            []).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_AllowList_MultipleEntries_NoneMatch_Denied()
    {
        MailAddressFilter.IsAllowed(
            "unknown@example.com",
            ["alice@example.com", "bob@example.com"],
            []).Should().BeFalse();
    }

    // =========================================================================
    // IsAllowed – block list only
    // =========================================================================

    [Fact]
    public void IsAllowed_BlockList_ExactMatch_Denied()
    {
        MailAddressFilter.IsAllowed("spammer@evil.com", [], ["spammer@evil.com"])
            .Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_BlockList_NoMatch_Allowed()
    {
        MailAddressFilter.IsAllowed("user@example.com", [], ["spammer@evil.com"])
            .Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_BlockList_DomainWildcard_Match_Denied()
    {
        MailAddressFilter.IsAllowed("anyone@blocked.com", [], ["@blocked.com"])
            .Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_BlockList_DomainWildcard_OtherDomain_Allowed()
    {
        MailAddressFilter.IsAllowed("user@safe.com", [], ["@blocked.com"])
            .Should().BeTrue();
    }

    // =========================================================================
    // IsAllowed – both lists: block list wins
    // =========================================================================

    [Fact]
    public void IsAllowed_AddressInBothLists_BlockListWins_Denied()
    {
        MailAddressFilter.IsAllowed(
            "user@example.com",
            ["user@example.com"],      // allow
            ["user@example.com"])      // block – takes precedence
            .Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_DomainInBothLists_BlockListWins_Denied()
    {
        MailAddressFilter.IsAllowed(
            "user@example.com",
            ["@example.com"],          // allow
            ["@example.com"])          // block – takes precedence
            .Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_AllowedByDomain_BlockedByExactAddress_Denied()
    {
        MailAddressFilter.IsAllowed(
            "spammer@example.com",
            ["@example.com"],          // whole domain allowed
            ["spammer@example.com"])   // but this specific address is blocked
            .Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_BlockedByDomain_AllowedByExactAddress_Denied()
    {
        // Block list wins even when the exact address is in the allow list
        MailAddressFilter.IsAllowed(
            "user@blocked.com",
            ["user@blocked.com"],      // specific address allowed
            ["@blocked.com"])          // but entire domain is blocked
            .Should().BeFalse();
    }

    // =========================================================================
    // Case-insensitivity
    // =========================================================================

    [Fact]
    public void IsAllowed_ExactMatch_CaseInsensitive_Allowed()
    {
        MailAddressFilter.IsAllowed("User@EXAMPLE.COM", ["user@example.com"], [])
            .Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_DomainWildcard_CaseInsensitive_Allowed()
    {
        MailAddressFilter.IsAllowed("User@EXAMPLE.COM", ["@example.com"], [])
            .Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_BlockList_CaseInsensitive_Denied()
    {
        MailAddressFilter.IsAllowed("SPAMMER@EVIL.COM", [], ["spammer@evil.com"])
            .Should().BeFalse();
    }

    // =========================================================================
    // Null reverse path edge case: MAIL FROM:<> → address = "@"
    // =========================================================================

    [Fact]
    public void IsAllowed_NullReversePath_EmptyLists_Allowed()
    {
        // Null reverse path (bounces / NDRs) must pass through when no filters
        // are configured; an outbound relay should not block legitimate NDRs.
        MailAddressFilter.IsAllowed("@", [], []).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_NullReversePath_AllowListSet_Denied()
    {
        // "@" does not match any normal allow-list entry → rejected
        MailAddressFilter.IsAllowed("@", ["user@example.com"], []).Should().BeFalse();
    }

    // =========================================================================
    // MatchesAny – direct edge-case tests
    // =========================================================================

    [Fact]
    public void MatchesAny_EmptyList_ReturnsFalse()
    {
        MailAddressFilter.MatchesAny("user@example.com", []).Should().BeFalse();
    }

    [Fact]
    public void MatchesAny_DomainWildcard_SubdomainSuffix_NotMatched()
    {
        // "user@xexample.com" must NOT match "@example.com"
        // (the "@" in the entry prevents false suffix matches)
        MailAddressFilter.MatchesAny("user@xexample.com", ["@example.com"]).Should().BeFalse();
    }

    // =========================================================================
    // GetDenyReason – log diagnostics for rejections
    // =========================================================================

    [Fact]
    public void GetDenyReason_BlockedByExactEntry_NamesTheEntry()
    {
        MailAddressFilter.GetDenyReason("spam@example.com", [], ["spam@example.com"])
            .Should().Be("matches block list entry 'spam@example.com'");
    }

    [Fact]
    public void GetDenyReason_BlockedByDomainWildcard_NamesTheWildcard()
    {
        MailAddressFilter.GetDenyReason("user@bad.org", [], ["@bad.org"])
            .Should().Be("matches block list entry '@bad.org'");
    }

    [Fact]
    public void GetDenyReason_NotInAllowList_SaysSo()
    {
        MailAddressFilter.GetDenyReason("outsider@other.com", ["@corp.com"], [])
            .Should().Be("not covered by any allow list entry");
    }

    [Fact]
    public void GetDenyReason_BlockListWinsOverAllowList()
    {
        // Address is in both lists — the block match must be reported,
        // mirroring the precedence in IsAllowed.
        MailAddressFilter.GetDenyReason("user@corp.com", ["@corp.com"], ["user@corp.com"])
            .Should().Be("matches block list entry 'user@corp.com'");
    }
}
