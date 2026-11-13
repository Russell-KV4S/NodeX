namespace NodeX.Core.Tests;

public class CarTransmitTests
{
    private static CarTalkDecision Decide(
        bool enabled = true, string? nowPlaying = "2000", string? transmit = "2000",
        bool connected = true, bool mic = true, bool keyed = false, TransmitBlock block = TransmitBlock.None) =>
        CarTransmit.Decide(enabled, nowPlaying, transmit, connected, mic, keyed, block);

    [Fact]
    public void All_conditions_met_keys()
    {
        Assert.Equal(CarTalkAction.Key, Decide().Action);
    }

    [Fact]
    public void Off_by_default_setting_refuses()
    {
        var d = Decide(enabled: false);
        Assert.Equal(CarTalkAction.Refuse, d.Action);
        Assert.Contains("off", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unkey_is_never_refused()
    {
        // Even with the feature off, a dead link, no mic and a blocked microphone, a keyed transmitter can always be released.
        Assert.Equal(CarTalkAction.Unkey,
            Decide(enabled: false, nowPlaying: null, transmit: null, connected: false, mic: false, keyed: true, block: TransmitBlock.MicrophoneDenied).Action);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("disconnected")]
    [InlineData("micdenied")]
    [InlineData("othertransmit")]
    [InlineData("notransmit")]
    [InlineData("micunusable")]
    public void Every_unsafe_state_refuses(string which)
    {
        var d = which switch
        {
            "none" => Decide(nowPlaying: null),
            "disconnected" => Decide(connected: false),
            "micdenied" => Decide(block: TransmitBlock.MicrophoneDenied),
            "othertransmit" => Decide(nowPlaying: "2000", transmit: "3000"),   // the car never redirects to another node
            "notransmit" => Decide(transmit: null),
            _ => Decide(mic: false),
        };
        Assert.Equal(CarTalkAction.Refuse, d.Action);
        Assert.False(string.IsNullOrWhiteSpace(d.Message));
    }
}
