using GraphMailer.ConfigTool.Views;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Footnote under the Metrics page's top-N rankings (top hosts, top failure causes). Those lists
/// are deliberately not pageable — they answer "who is worst", not "show me everything" — but a
/// ranking that quietly drops most of its input still reads as the full picture.
/// </summary>
public sealed class MetricsTopNLabelTests
{
    [Fact]
    public void MoreLabel_EverythingFitsInTheRanking_IsEmpty()
    {
        MetricsPage.MoreLabel(shown: 10, total: 10, noun: "cause")
            .Should().BeEmpty("nothing was left out, so there is nothing to admit");
    }

    [Fact]
    public void TopRankingSize_IsOneNumberForEveryRanking()
    {
        MetricsPage.TopRankingSize.Should().Be(10,
            "top hosts and top failure causes used to be 8 and 6 with no reason behind either");
    }

    [Fact]
    public void MoreLabel_FewerEntriesThanTheRankingHolds_IsEmpty()
    {
        MetricsPage.MoreLabel(shown: 3, total: 3, noun: "host").Should().BeEmpty();
    }

    [Fact]
    public void MoreLabel_EntriesLeftOut_NamesHowMany()
    {
        MetricsPage.MoreLabel(shown: 10, total: 28, noun: "cause")
            .Should().Be("+ 18 more causes not shown");
    }

    [Fact]
    public void MoreLabel_ExactlyOneLeftOut_UsesTheSingular()
    {
        MetricsPage.MoreLabel(shown: 10, total: 11, noun: "host")
            .Should().Be("+ 1 more host not shown");
    }

    [Fact]
    public void MoreLabel_LargeRemainder_IsThousandsSeparated()
    {
        MetricsPage.MoreLabel(shown: 10, total: 1510, noun: "host")
            .Should().Be($"+ {1500:N0} more hosts not shown");
    }
}
