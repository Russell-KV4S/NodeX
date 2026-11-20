using NodeX.Core;

namespace NodeX.Core.Tests;

public class AllStarStatsLinksTests
{
    [Theory]
    [InlineData("2200", "https://stats.allstarlink.org/stats/2200/networkMap")]
    [InlineData(" 47923 ", "https://stats.allstarlink.org/stats/47923/networkMap")]
    [InlineData("523800", "https://stats.allstarlink.org/stats/523800/networkMap")]
    public void BubbleChart_PublicNodeNumber_GivesNetworkMapUrl(string node, string expected)
    {
        Assert.Equal(expected, AllStarStatsLinks.BubbleChart(node)!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1999@node.example.org")]
    [InlineData("12a4")]
    [InlineData("../2200")]
    [InlineData("12345678901")]
    public void BubbleChart_NotAPublicNodeNumber_GivesNull(string? node)
    {
        Assert.Null(AllStarStatsLinks.BubbleChart(node));
    }
}
