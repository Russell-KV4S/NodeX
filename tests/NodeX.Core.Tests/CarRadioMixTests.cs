namespace NodeX.Core.Tests;

public class CarRadioMixTests
{
    [Theory]
    [InlineData(true, true, false, true)]    // car playing the radio: mix
    [InlineData(true, true, true, false)]    // NodeX is the car's source: plain media
    [InlineData(true, false, false, false)]  // not in a car
    [InlineData(false, true, false, false)]  // option off
    public void Applies_only_in_car_with_option_on_while_car_plays_something_else(bool enabled, bool car, bool hasAudio, bool expected) =>
        Assert.Equal(expected, CarRadioMix.Applies(enabled, car, hasAudio));

    [Theory]
    [InlineData(true, false, true, PlaybackUsage.NavigationGuidance)]
    [InlineData(true, false, false, PlaybackUsage.Media)]
    [InlineData(false, true, false, PlaybackUsage.Media)]
    [InlineData(false, false, false, PlaybackUsage.VoiceCommunication)]
    public void Choose_picks_the_stream(bool car, bool speaker, bool mix, PlaybackUsage expected) =>
        Assert.Equal(expected, CarRadioMix.Choose(car, speaker, mix));

    [Fact]
    public void Gate_holds_while_heard_and_releases_after_delay()
    {
        var g = new RadioDuckGate();
        Assert.False(g.Update(true, false, 0));          // quiet: nothing to hold
        Assert.False(g.Holding);

        Assert.True(g.Update(true, true, 1000));         // node talks: duck
        Assert.True(g.Holding);
        Assert.False(g.Update(true, false, 1000 + 1400)); // short pause: keep holding
        Assert.True(g.Holding);
        Assert.True(g.Update(true, false, 1000 + 1500)); // 1.5 s quiet: let the radio back up
        Assert.False(g.Holding);
    }

    [Fact]
    public void Gate_releases_at_once_when_mixing_stops_applying()
    {
        var g = new RadioDuckGate();
        g.Update(true, true, 0);
        Assert.True(g.Update(false, true, 100));          // NodeX became the car's source / option turned off
        Assert.False(g.Holding);
        Assert.False(g.Update(true, false, 200));         // old talk does not re-duck
    }
}
