namespace NodeX.Core.Tests;

public class RxActivityTests
{
    [Fact]
    public void Voice_within_hold_is_heard() =>
        Assert.True(RxActivity.IsHeard(false, 0, 1000, 1400));

    [Fact]
    public void Keyed_with_recent_voice_stays_heard() =>
        Assert.True(RxActivity.IsHeard(true, 1000, 3500, 5000));

    [Fact]
    public void Keyed_but_silent_past_stale_limit_is_not_heard()
    {
        // KEY arrived, voice stopped, UNKEY never came: must drop after KeyStale, not stay green forever.
        Assert.True(RxActivity.IsHeard(true, 1000, 1200, 2000));
        Assert.False(RxActivity.IsHeard(true, 1000, 1200, 1200 + 3001));
    }

    [Fact]
    public void Unkeyed_and_quiet_is_not_heard() =>
        Assert.False(RxActivity.IsHeard(false, 1000, 1100, 5000));
}
