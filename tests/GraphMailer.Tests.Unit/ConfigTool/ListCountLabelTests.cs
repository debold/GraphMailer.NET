using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Counter line shared by the paged monitoring lists (Metrics → Recent Activity, Logs, Messages).
/// Each of them caps what it loads, so the label carries the whole contract that the user never
/// mistakes a capped list for a complete one.
/// </summary>
public sealed class ListCountLabelTests
{
    // Num() formats with "N0", which is culture-dependent — build expectations the same way
    // instead of hard-coding separators (the target machines run German Windows).
    private static string N(long value) => value.ToString("N0");

    [Fact]
    public void Build_AllRowsLoaded_ReportsPlainTotal()
    {
        ListCountLabel.Build(412, 412, "events", filtered: false, hasMore: false)
            .Should().Be($"{N(412)} events");
    }

    [Fact]
    public void Build_NoRows_IsEmpty()
    {
        ListCountLabel.Build(0, 0, "events", filtered: false, hasMore: false)
            .Should().BeEmpty("the empty-state hint below the grid already says there is nothing");
    }

    [Fact]
    public void Build_MoreRowsAvailable_NamesShownAndPool()
    {
        ListCountLabel.Build(500, 3421, "events", filtered: false, hasMore: true)
            .Should().Be($"newest {N(500)} of {N(3421)}");
    }

    [Fact]
    public void Build_FilteredAndFullyScanned_ReportsExactMatchCount()
    {
        ListCountLabel.Build(47, 3421, "events", filtered: true, hasMore: false)
            .Should().Be($"{N(47)} matches of {N(3421)}");
    }

    [Fact]
    public void Build_FilteredAndStoppedAtPageLimit_MarksCountAsALowerBound()
    {
        ListCountLabel.Build(500, 3421, "events", filtered: true, hasMore: true)
            .Should().Be($"{N(500)}+ matches of {N(3421)}",
                "a filtered load that fills the page cannot know how many further matches follow");
    }

    [Fact]
    public void Build_NoMatches_StillNamesThePoolThatWasSearched()
    {
        ListCountLabel.Build(0, 3421, "events", filtered: true, hasMore: false)
            .Should().Be($"{N(0)} matches of {N(3421)}",
                "an empty result is only meaningful next to the size of what was searched");
    }

    // ── Unknown pool: the Logs page cannot count its files up front ──────────

    [Fact]
    public void Build_UnknownPoolWithMoreAvailable_OmitsTheTotalButKeepsTheQualifier()
    {
        ListCountLabel.Build(2000, null, "entries", filtered: false, hasMore: true)
            .Should().Be($"newest {N(2000)} entries",
                "\"newest\" marks the cut-off without inventing a total that was never counted");
    }

    [Fact]
    public void Build_UnknownPoolWhileFiltered_DropsTheOfClause()
    {
        ListCountLabel.Build(47, null, "entries", filtered: true, hasMore: false)
            .Should().Be($"{N(47)} matches");
    }

    // ── Scan-cap note ───────────────────────────────────────────────────────

    [Fact]
    public void Build_WithNote_AppendsItAfterASeparator()
    {
        ListCountLabel.Build(12, 900_000, "events", filtered: true, hasMore: false,
                note: "stopped after 25,000 events")
            .Should().Be($"{N(12)} matches of {N(900_000)} · stopped after 25,000 events");
    }

    [Fact]
    public void Build_NoteOnAnEmptyLabel_HasNoLeadingSeparator()
    {
        ListCountLabel.Build(0, null, "entries", filtered: false, hasMore: false,
                note: "stopped after 25,000 entries")
            .Should().Be("stopped after 25,000 entries");
    }

    /// <summary>
    /// The regression the whole label exists for: before paging, the lists showed a fixed newest-N
    /// with an empty or filter-only counter, so a truncated list was indistinguishable from a
    /// complete one. Whenever rows were left out, the label must not be silent.
    /// </summary>
    [Theory]
    [InlineData(false, true, null)]                      // page limit reached, no filter
    [InlineData(true, true, null)]                       // page limit reached while filtering
    [InlineData(true, false, "stopped after 25,000")]    // scan cap reached while filtering
    public void Build_WheneverRowsAreLeftOut_LabelIsNeverSilent(bool filtered, bool hasMore, string? note)
    {
        ListCountLabel.Build(500, 3421, "events", filtered, hasMore, note)
            .Should().NotBeEmpty();
    }
}
