using NodeX.UI.ViewModels;

namespace NodeX.Core.Tests;

public class LastHeardAgeTests
{
    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(4 * 60, "4 min ago")]
    [InlineData(59 * 60 + 59, "59 min ago")]
    [InlineData(3600, "1 h 0 min ago")]
    [InlineData(2 * 3600 + 5 * 60, "2 h 5 min ago")]
    public void Age_text_leaves_just_now_after_a_minute(int seconds, string expected)
    {
        Assert.Equal(expected, LinkItemViewModel.AgoText(TimeSpan.FromSeconds(seconds)));
    }
}
