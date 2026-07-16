using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Tests for <see cref="AddressPatternValidator"/>.
///
/// Covers:
///   • Format validation (valid and invalid inputs)
///   • Duplicate detection within the same list
///   • Redundancy detection (exact address + domain wildcard in same list)
///   • Cross-list conflict: allow entry shadowed by block
///   • Cross-list conflict: block @domain making allow entries ineffective
///   • Legitimate combinations that must NOT produce an error
/// </summary>
public class AddressPatternValidatorTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static string? Allow(string pattern,
        IReadOnlyList<string>? sameList = null,
        IReadOnlyList<string>? blockList = null)
        => AddressPatternValidator.Validate(
            pattern,
            sameList ?? [],
            blockList,
            isAllowList: true);

    private static string? Block(string pattern,
        IReadOnlyList<string>? sameList = null,
        IReadOnlyList<string>? allowList = null)
        => AddressPatternValidator.Validate(
            pattern,
            sameList ?? [],
            allowList,
            isAllowList: false);

    // ── IsValidPattern ────────────────────────────────────────────────────

    [Theory]
    [InlineData("user@domain.com")]
    [InlineData("User@Domain.COM")]
    [InlineData("u@sub.domain.co.uk")]
    [InlineData("@domain.com")]
    [InlineData("@sub.domain.co.uk")]
    public void IsValidPattern_ValidInput_ReturnsTrue(string pattern)
        => AddressPatternValidator.IsValidPattern(pattern).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("domain.com")]          // no @ prefix → treated as exact; no local part
    [InlineData("*@domain.com")]        // * not supported
    [InlineData("*")]                   // wildcard not supported
    [InlineData("nodomain")]            // no @ at all
    [InlineData("@")]                   // empty domain
    [InlineData("user@")]              // empty domain
    [InlineData("@.com")]              // domain starts with dot
    [InlineData("user @domain.com")]   // space in local part
    public void IsValidPattern_InvalidInput_ReturnsFalse(string pattern)
        => AddressPatternValidator.IsValidPattern(pattern).Should().BeFalse();

    // ── Empty / whitespace ────────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyPattern_ReturnsError()
        => Allow("   ").Should().Contain("must not be empty");

    // ── Invalid format ────────────────────────────────────────────────────

    [Theory]
    [InlineData("domain.com")]
    [InlineData("*")]
    [InlineData("*@domain.com")]
    public void Validate_InvalidFormat_ReturnsFormatError(string pattern)
        => Allow(pattern).Should().Contain("not a valid address or pattern");

    // ── Duplicate in same list ────────────────────────────────────────────

    [Fact]
    public void Validate_ExactDuplicate_ReturnsAlreadyInListError()
        => Allow("user@domain.com", ["user@domain.com"])
            .Should().Contain("already in the list");

    [Fact]
    public void Validate_DuplicateCaseInsensitive_ReturnsAlreadyInListError()
        => Allow("User@Domain.COM", ["user@domain.com"])
            .Should().Contain("already in the list");

    [Fact]
    public void Validate_DomainWildcardDuplicate_ReturnsAlreadyInListError()
        => Allow("@domain.com", ["@domain.com"])
            .Should().Contain("already in the list");

    // ── Redundancy within same list ───────────────────────────────────────

    [Fact]
    public void Validate_ExactAddressWhenDomainWildcardPresent_ReturnsRedundancyError()
        => Allow("user@domain.com", ["@domain.com"])
            .Should().Contain("Redundant");

    [Fact]
    public void Validate_DomainWildcardWhenExactAddressPresent_IsNotRedundant()
        // @domain is broader — adding it is valid even if individual addresses exist
        => Allow("@domain.com", ["user@domain.com"])
            .Should().BeNull();

    // ── Cross-list: adding to ALLOW when entry exists in BLOCK ────────────

    [Fact]
    public void Validate_AllowExact_WhenExactIsBlocked_ReturnsConflictError()
        => Allow("user@domain.com", blockList: ["user@domain.com"])
            .Should().Contain("block list").And.Contain("takes precedence");

    [Fact]
    public void Validate_AllowExact_WhenDomainWildcardIsBlocked_ReturnsConflictError()
        => Allow("user@domain.com", blockList: ["@domain.com"])
            .Should().Contain("are blocked").And.Contain("no effect");

    [Fact]
    public void Validate_AllowDomainWildcard_WhenSameDomainWildcardIsBlocked_ReturnsConflictError()
        => Allow("@domain.com", blockList: ["@domain.com"])
            .Should().Contain("block list").And.Contain("takes precedence");

    [Fact]
    public void Validate_AllowExact_WhenDifferentDomainBlocked_ReturnsNull()
        // @other.com blocked does not affect user@domain.com
        => Allow("user@domain.com", blockList: ["@other.com"])
            .Should().BeNull();

    [Fact]
    public void Validate_AllowDomainWildcard_WhenExactAddressOfThatDomainBlocked_ReturnsNull()
        // @domain on allow side is valid even if one specific address is blocked:
        // block(marc@domain) + allow(@domain) means "allow all except marc"
        => Allow("@domain.com", blockList: ["user@domain.com"])
            .Should().BeNull();

    // ── Cross-list: adding @domain to BLOCK when allow entries exist ──────

    [Fact]
    public void Validate_BlockDomainWildcard_WhenExactAllowedAddressExists_ReturnsConflictError()
    {
        var error = Block("@domain.com", allowList: ["user@domain.com"]);
        error.Should().Contain("make the following allow entries ineffective")
              .And.Contain("'user@domain.com'");
    }

    [Fact]
    public void Validate_BlockDomainWildcard_WhenSameDomainWildcardAllowed_ReturnsConflictError()
    {
        var error = Block("@domain.com", allowList: ["@domain.com"]);
        error.Should().Contain("make the following allow entries ineffective")
              .And.Contain("'@domain.com'");
    }

    [Fact]
    public void Validate_BlockDomainWildcard_MultipleAffectedAllowEntries_ListsAll()
    {
        var error = Block("@domain.com",
            allowList: ["a@domain.com", "b@domain.com", "c@domain.com", "d@domain.com"]);
        error.Should().Contain("+1 more");
    }

    [Fact]
    public void Validate_BlockDomainWildcard_WhenOnlyOtherDomainAllowed_ReturnsNull()
        => Block("@domain.com", allowList: ["user@other.com"])
            .Should().BeNull();

    [Fact]
    public void Validate_BlockExactAddress_WhenDomainWildcardAllowed_ReturnsNull()
        // Blocking marc@domain while @domain is allowed = valid; other addresses still get through
        => Block("user@domain.com", allowList: ["@domain.com"])
            .Should().BeNull();

    [Fact]
    public void Validate_BlockExactAddress_WhenSameExactAllowed_ReturnsNull()
        // Not flagged from the block side — the conflict message surfaces when
        // the user tries to add it to the allow list (tested in allow-side tests above)
        => Block("user@domain.com", allowList: ["user@domain.com"])
            .Should().BeNull();

    // ── No-conflict happy paths ───────────────────────────────────────────

    [Fact]
    public void Validate_FreshExactAddress_EmptyLists_ReturnsNull()
        => Allow("user@domain.com").Should().BeNull();

    [Fact]
    public void Validate_FreshDomainWildcard_EmptyLists_ReturnsNull()
        => Allow("@domain.com").Should().BeNull();

    [Fact]
    public void Validate_BlockDomainWildcard_EmptyAllowList_ReturnsNull()
        => Block("@domain.com").Should().BeNull();

    [Fact]
    public void Validate_BlockDomainWildcard_AllowListHasOtherDomain_ReturnsNull()
        => Block("@domain.com", allowList: ["user@other.com", "@third.com"])
            .Should().BeNull();

    [Fact]
    public void Validate_AllowExact_DifferentDomainInBlock_ReturnsNull()
        => Allow("user@domain.com", blockList: ["@other.com", "someone@third.com"])
            .Should().BeNull();

    // ── Case-insensitivity throughout ─────────────────────────────────────

    [Fact]
    public void Validate_AllowExact_BlockedWithDifferentCase_ReturnsConflictError()
        => Allow("User@Domain.COM", blockList: ["user@domain.com"])
            .Should().Contain("block list");

    [Fact]
    public void Validate_ExactRedundancy_DifferentCase_ReturnsRedundancyError()
        => Allow("User@Domain.COM", ["@DOMAIN.COM"])
            .Should().Contain("Redundant");

    // ── Subdomain boundary (mirrors MailAddressFilter behaviour) ──────────
    //
    // MailAddressFilter.MatchesAny: "@domain.com" matches only addresses
    // ending with exactly "@domain.com" — subdomains are NOT covered.
    // The validator must reflect this: no false positives or false negatives
    // when subdomains are involved.

    [Theory]
    [InlineData("@sub.domain.com")]
    [InlineData("@a.b.domain.com")]
    [InlineData("user@sub.domain.com")]
    public void IsValidPattern_SubdomainPatterns_ReturnsTrue(string pattern)
        => AddressPatternValidator.IsValidPattern(pattern).Should().BeTrue();

    [Fact]
    public void Validate_AllowExact_SubdomainAddress_WhenParentDomainBlocked_ReturnsNull()
        // @domain.com in block does NOT block user@sub.domain.com (no subdomain matching).
        // Adding user@sub.domain.com to allow is therefore valid — no conflict.
        => Allow("user@sub.domain.com", blockList: ["@domain.com"])
            .Should().BeNull();

    [Fact]
    public void Validate_AllowSubdomainWildcard_WhenParentDomainBlocked_ReturnsNull()
        // @sub.domain.com is a distinct entry from @domain.com.
        // The service treats them independently — no conflict.
        => Allow("@sub.domain.com", blockList: ["@domain.com"])
            .Should().BeNull();

    [Fact]
    public void Validate_AllowExact_WhenExactSubdomainWildcardBlocked_ReturnsConflictError()
        // @sub.domain.com IS in block list → user@sub.domain.com on allow side is ineffective.
        => Allow("user@sub.domain.com", blockList: ["@sub.domain.com"])
            .Should().Contain("are blocked").And.Contain("no effect");

    [Fact]
    public void Validate_BlockParentDomain_WhenSubdomainAddressAllowed_DoesNotFlagAsIneffective()
        // Blocking @domain.com does NOT affect user@sub.domain.com on the allow list
        // because @domain.com does not match subdomain addresses.
        => Block("@domain.com", allowList: ["user@sub.domain.com"])
            .Should().BeNull();

    [Fact]
    public void Validate_BlockSubdomainWildcard_WhenSubdomainAddressAllowed_ReturnsConflictError()
        // Blocking @sub.domain.com makes user@sub.domain.com on the allow list ineffective.
        => Block("@sub.domain.com", allowList: ["user@sub.domain.com"])
            .Should().Contain("make the following allow entries ineffective");

    [Fact]
    public void Validate_BlockParentDomain_WhenSubdomainWildcardAllowed_DoesNotFlagAsIneffective()
        // @domain.com does not cover @sub.domain.com — no conflict.
        => Block("@domain.com", allowList: ["@sub.domain.com"])
            .Should().BeNull();

    [Fact]
    public void Validate_ExactSubdomainAddress_WhenSubdomainWildcardInSameList_ReturnsRedundancyError()
        // Same-list redundancy applies to subdomains equally.
        => Allow("user@sub.domain.com", ["@sub.domain.com"])
            .Should().Contain("Redundant");

    [Fact]
    public void Validate_ExactSubdomainAddress_WhenParentDomainWildcardInSameList_ReturnsNull()
        // @domain.com in the same list does NOT cover user@sub.domain.com — not redundant.
        => Allow("user@sub.domain.com", ["@domain.com"])
            .Should().BeNull();
}
