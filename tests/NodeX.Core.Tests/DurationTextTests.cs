namespace NodeX.Core.Tests;

public class DurationTextTests
{
    [Theory]
    [InlineData(0, "< 1 min")]
    [InlineData(59, "< 1 min")]
    [InlineData(60, "1 min")]
    [InlineData(59 * 60 + 59, "59 min")]
    [InlineData(3600, "1 h 00 min")]
    [InlineData(2 * 3600 + 5 * 60, "2 h 05 min")]
    [InlineData(23 * 3600 + 59 * 60, "23 h 59 min")]
    [InlineData(24 * 3600, "1 d 0 h")]
    [InlineData(2 * 86400 + 5 * 3600 + 1800, "2 d 5 h")]
    [InlineData(-30, "< 1 min")]
    public void Format_UsesTheRightUnit(int seconds, string expected) =>
        Assert.Equal(expected, DurationText.Format(TimeSpan.FromSeconds(seconds)));
}
