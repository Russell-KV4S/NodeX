using NodeX.Core.Audio;
using NodeX.Core.Settings;

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

    [Fact]
    public async Task Attach_TracksRemoteKeyAndVoice_AndDetachStopsTracking()
    {
        long clock = 1000;
        using var rx = new RxActivity(() => Volatile.Read(ref clock));
        using var remote = new FakeIaxRemote();

        var settings = new NodeSettings { NodeNumber = "68498", Callsign = "KV4S" };
        var target = new NodeInfo("2000", "Hub", remote.EndPoint, "Hub", DateTime.UtcNow);
        await using var link = new NodeLink(settings, target, new NullAudioDeviceService());
        rx.Attach(link);

        var connectTask = link.ConnectAsync();
        await remote.AcceptCallAsync();
        await connectTask;

        Assert.Empty(rx.Talking());
        Assert.False(rx.IsReceiving("2000"));

        // Send a voice frame from the remote node: heard for VoiceHold (500 ms).
        await remote.SendAsync(Iax2Protocol.FrameType.Voice, 0x04, 20, new byte[160]);
        for (int i = 0; i < 40 && !rx.IsReceiving("2000"); i++)
            await Task.Delay(10);

        Assert.True(rx.IsReceiving("2000"));
        Assert.Equal(["2000"], rx.Talking());

        // Advance simulated clock past VoiceHold (500 ms): no longer talking.
        Interlocked.Exchange(ref clock, 1600);
        Assert.False(rx.IsReceiving("2000"));
        Assert.Empty(rx.Talking());

        // Detach removes the node from tracking.
        rx.Detach("2000");
        Assert.False(rx.IsReceiving("2000"));
    }
}
