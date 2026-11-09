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
    public void Node_that_sends_KEY_is_not_lit_by_voice_after_UNKEY() =>
        // KEY seen at 1000, UNKEY since, but frames keep arriving (a hub relaying silence): not heard.
        Assert.False(RxActivity.IsHeard(false, 1000, 4900, 5000));

    [Fact]
    public void Node_that_never_sends_KEY_is_heard_by_voice_alone()
    {
        Assert.True(RxActivity.IsHeard(false, 0, 4900, 5000));
        Assert.False(RxActivity.IsHeard(false, 0, 4000, 5000));
    }

    [Fact]
    public void Unkeyed_and_quiet_is_not_heard() =>
        Assert.False(RxActivity.IsHeard(false, 1000, 1100, 5000));
}
