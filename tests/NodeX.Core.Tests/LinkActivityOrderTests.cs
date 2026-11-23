using Xunit;

namespace NodeX.Core.Tests;

public class LinkActivityOrderTests
{
    [Fact]
    public void SortByActivity_MovesMostRecentToTop_AndKeepsUnheardAtBottomInConnectOrder()
    {
        var entries = new LinkActivityOrder.Entry[]
        {
            new("1999", 0, 0),
            new("1992", 1, 0),
            new("53431", 2, 1),
            new("43136", 3, 0),
        };

        var ordered = LinkActivityOrder.Order(entries, sortByActivity: true);

        Assert.Equal(["53431", "1999", "1992", "43136"], ordered);
    }

    [Fact]
    public void SortByActivity_LatestActivityOvertakesEarlierActivity()
    {
        var entries = new LinkActivityOrder.Entry[]
        {
            new("53431", 2, 1),
            new("1999", 0, 2),
            new("1992", 1, 0),
            new("43136", 3, 0),
        };

        var ordered = LinkActivityOrder.Order(entries, sortByActivity: true);

        Assert.Equal(["1999", "53431", "1992", "43136"], ordered);
    }

    [Fact]
    public void FixedMode_PreservesConnectOrderEvenWithActivity()
    {
        var entries = new LinkActivityOrder.Entry[]
        {
            new("53431", 2, 9),
            new("1999", 0, 3),
            new("43136", 3, 0),
            new("1992", 1, 0),
        };

        var ordered = LinkActivityOrder.Order(entries, sortByActivity: false);

        Assert.Equal(["1999", "1992", "53431", "43136"], ordered);
    }

    [Fact]
    public void EmptySingleAndNegativeSequences_HandledSafely()
    {
        Assert.Empty(LinkActivityOrder.Order([], sortByActivity: true));
        Assert.Equal(["2000"], LinkActivityOrder.Order([new("2000", 0, 0)], sortByActivity: true));

        var entries = new LinkActivityOrder.Entry[]
        {
            new("2000", 0, -5),
            new("3000", 1, 0),
            new("4000", 2, 1),
        };
        Assert.Equal(["4000", "2000", "3000"], LinkActivityOrder.Order(entries, sortByActivity: true));
    }
}
