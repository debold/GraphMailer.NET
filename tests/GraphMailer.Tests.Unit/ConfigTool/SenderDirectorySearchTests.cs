using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Filter predicate of the read-only sender-directory viewer. The grid can hold tens of
/// thousands of rows, so a filter that silently misses a match defeats the point of the window:
/// the operator would conclude an address is not synced when in fact it is.
/// </summary>
public sealed class SenderDirectorySearchTests
{
    private static bool Match(string query) =>
        SenderDirectorySearch.Matches(
            "Sales Team", "sales@corp.com", ["sales@corp.com", "vertrieb@corp.com"], query);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Matches_EmptyQuery_KeepsEveryRow(string? query)
        => Match(query!).Should().BeTrue();

    [Fact]
    public void Matches_DisplayName_IsFound()
        => Match("sales team").Should().BeTrue();

    [Fact]
    public void Matches_PrimaryAddress_IsFound()
        => Match("sales@corp").Should().BeTrue();

    [Fact]
    public void Matches_Alias_IsFound()
        => Match("vertrieb").Should().BeTrue("an alias is exactly what an operator comes here to check");

    [Fact]
    public void Matches_IsCaseInsensitive()
        => Match("VERTRIEB@CORP.COM").Should().BeTrue();

    [Fact]
    public void Matches_SurroundingWhitespace_IsIgnored()
        => Match("  vertrieb  ").Should().BeTrue();

    [Fact]
    public void Matches_NoOccurrence_FiltersTheRowOut()
        => Match("marketing").Should().BeFalse();

    [Fact]
    public void Matches_NullFields_DoNotThrow()
        => SenderDirectorySearch.Matches(null, null, null, "anything").Should().BeFalse();

    [Fact]
    public void Matches_OnlyAliasesSet_StillSearchesThem()
        => SenderDirectorySearch.Matches(null, null, ["pf@corp.com"], "pf@").Should().BeTrue();
}
