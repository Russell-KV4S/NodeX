using NodeX.Core;

namespace NodeX.Core.Tests;

public class MediaKeyTransmitTests
{
    [Fact]
    public void Off_IsIgnored()
    {
        var d = MediaKeyTransmit.Decide(enabled: false, linkConnected: true, currentlyKeyed: false, TransmitBlock.None);
        Assert.Equal(MediaKeyAction.Ignore, d.Action);
    }

    [Fact]
    public void NotConnected_IsIgnored()
    {
        var d = MediaKeyTransmit.Decide(true, linkConnected: false, currentlyKeyed: false, TransmitBlock.None);
        Assert.Equal(MediaKeyAction.Ignore, d.Action);
    }

    [Fact]
    public void Ready_Keys()
    {
        var d = MediaKeyTransmit.Decide(true, true, false, TransmitBlock.None);
        Assert.Equal(MediaKeyAction.Key, d.Action);
    }

    [Theory]
    [InlineData(TransmitBlock.ListenOnly)]
    [InlineData(TransmitBlock.MicrophoneDenied)]
    public void Blocked_RefusesWithReason(TransmitBlock block)
    {
        var d = MediaKeyTransmit.Decide(true, true, false, block);
        Assert.Equal(MediaKeyAction.Refuse, d.Action);
        Assert.NotEmpty(d.Message);
    }

    [Theory]
    [InlineData(false, false, TransmitBlock.None)]
    [InlineData(false, true, TransmitBlock.ListenOnly)]
    [InlineData(true, false, TransmitBlock.MicrophoneDenied)]
    public void WhenKeyed_AlwaysUnkeys(bool enabled, bool connected, TransmitBlock block)
    {
        var d = MediaKeyTransmit.Decide(enabled, connected, currentlyKeyed: true, block);
        Assert.Equal(MediaKeyAction.Unkey, d.Action);
    }
}
