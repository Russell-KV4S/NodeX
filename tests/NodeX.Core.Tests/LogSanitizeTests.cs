using NodeX.UI.ViewModels;
using Xunit;

namespace NodeX.Core.Tests;

public class LogSanitizeTests
{
    [Fact]
    public void TrailingNul_FromAsteriskTextFrame_IsRemoved()
    {
        // The Windows clipboard ends text at the first NUL, which truncated "Copy" after this message.
        Assert.Equal("!NEWKEY1!", MainViewModel.SanitizeLogText("!NEWKEY1!\0"));
    }

    [Fact]
    public void EmbeddedNul_AndLineBreaks_DoNotSplitOrTruncateTheEntry()
    {
        string s = MainViewModel.SanitizeLogText("L\0\r\nT451924,T521196\0");
        Assert.DoesNotContain('\0', s);
        Assert.DoesNotContain('\n', s);
        Assert.DoesNotContain('\r', s);
        Assert.Contains("T451924,T521196", s);
    }

    [Fact]
    public void NormalText_IsUnchanged() =>
        Assert.Equal("Connecting to 45192 at 12.17.28.213:4569...", MainViewModel.SanitizeLogText("Connecting to 45192 at 12.17.28.213:4569..."));

    [Fact]
    public void NullOrEmpty_GivesEmpty()
    {
        Assert.Equal("", MainViewModel.SanitizeLogText(""));
        Assert.Equal("", MainViewModel.SanitizeLogText(null!));
    }
}
