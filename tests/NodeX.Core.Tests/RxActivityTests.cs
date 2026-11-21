using NodeX.Core.Audio;
using NodeX.Core.Settings;

namespace NodeX.Core.Tests;

public class RxActivityTests
{
    [Fact]
    public void Voice_within_hold_is_heard() =>
        Assert.True(RxActivity.IsHeard(false, 0, 1000, 1000, 1400));

    [Fact]
    public void Keyed_with_recent_voice_stays_heard() =>
        Assert.True(RxActivity.IsHeard(true, 1000, 3500, 3500, 5000));

    [Fact]
    public void Keyed_but_silent_past_stale_limit_is_not_heard()
    {
        // KEY arrived, voice stopped, UNKEY never came: must drop after KeyStale, not stay green forever.
        Assert.True(RxActivity.IsHeard(true, 1000, 1200, 1200, 2000));
        Assert.False(RxActivity.IsHeard(true, 1000, 1200, 1200, 1200 + 3001));
    }

    [Fact]
    public void Node_that_sends_KEY_is_not_lit_by_voice_after_UNKEY() =>
        // KEY seen at 1000, UNKEY since, but frames keep arriving (a hub relaying silence): not heard.
        Assert.False(RxActivity.IsHeard(false, 1000, 4900, 4900, 5000));

    [Fact]
    public void Node_that_never_sends_KEY_is_heard_by_voice_alone()
    {
        Assert.True(RxActivity.IsHeard(false, 0, 4900, 4900, 5000));
        Assert.False(RxActivity.IsHeard(false, 0, 4000, 4000, 5000));
    }

    [Fact]
    public void Node_without_KEY_is_not_lit_by_silent_frames() =>
        // Frames keep arriving (last at 4900) but the last one with sound was long ago: not heard.
        Assert.False(RxActivity.IsHeard(false, 0, 4900, 1000, 5000));

    [Fact]
    public void Known_key_sender_after_reconnect_is_not_lit_by_idle_frames()
    {
        // Fresh call to a hub already seen sending KEY: KeyedAtTicks starts as KnownKeySender, so voice with sound
        // and no KEY on this call does not light it...
        Assert.False(RxActivity.IsHeard(false, RxActivity.KnownKeySender, 4900, 4900, 5000));
        // ...but its next KEY does.
        Assert.True(RxActivity.IsHeard(true, 4800, 4900, 4900, 5000));
    }

    [Fact]
    public void HasSound_ignores_ulaw_silence_and_detects_audio()
    {
        // 0xFF and 0x7F are the two µ-law zero codes; 0xFE / 0x7E are +-8 counts (idle noise).
        Assert.False(RxActivity.HasSound([0xFF, 0x7F, 0xFE, 0x7E, 0xFF]));
        Assert.False(RxActivity.HasSound([]));
        Assert.True(RxActivity.HasSound([0xFF, 0xFF, 0x00, 0xFF]));   // 0x00 = loudest negative sample
        var tone = new byte[160];
        for (int i = 0; i < tone.Length; i++)
            tone[i] = G711Ulaw.EncodeSample((short)(Math.Sin(i * 2 * Math.PI / 8) * 3000));
        Assert.True(RxActivity.HasSound(tone));
        var hiss = new byte[160];
        for (int i = 0; i < hiss.Length; i++)
            hiss[i] = G711Ulaw.EncodeSample((short)((i % 2 == 0) ? 40 : -40));
        Assert.False(RxActivity.HasSound(hiss));
    }

    [Fact]
    public void HasSound_ignores_hub_background_noise_that_a_peak_test_let_through()
    {
        // 2026-11-21: hubs streaming low noise between overs lit "RX" with nothing audible. Noise peaking at 400 counts
        // (-38 dBFS peak, far above the old 100-count peak threshold) is about -46 dBFS RMS: not sound.
        var noise = new byte[160];
        var rng = new Random(7);
        for (int i = 0; i < noise.Length; i++)
            noise[i] = G711Ulaw.EncodeSample((short)(rng.Next(-400, 401) * (i % 3 == 0 ? 1 : 0.5)));
        Assert.True(RxActivity.RmsDbfs(noise) < RxActivity.SoundRmsDbfs);
        Assert.False(RxActivity.HasSound(noise));

        // Quiet speech around -30 dBFS RMS still counts.
        var speech = new byte[160];
        for (int i = 0; i < speech.Length; i++)
            speech[i] = G711Ulaw.EncodeSample((short)(Math.Sin(i * 2 * Math.PI / 20) * 1460));
        Assert.InRange(RxActivity.RmsDbfs(speech), -31.5, -28.5);
        Assert.True(RxActivity.HasSound(speech));

        Assert.Equal(double.NegativeInfinity, RxActivity.RmsDbfs([0xFF, 0x7F]));
    }

    [Fact]
    public async Task Silent_voice_frames_do_not_light_a_node_but_sound_does()
    {
        long clock = 1000;
        using var rx = new RxActivity(() => Volatile.Read(ref clock));
        using var remote = new FakeIaxRemote();
        var settings = new NodeSettings { NodeNumber = "68498" };
        var target = new NodeInfo("2000", "Hub", remote.EndPoint, "Hub", DateTime.UtcNow);
        await using var link = new NodeLink(settings, target, new NullAudioDeviceService());
        rx.Attach(link);
        var connectTask = link.ConnectAsync();
        await remote.AcceptCallAsync();
        await connectTask;

        var silence = Enumerable.Repeat((byte)0xFF, 160).ToArray();
        for (int i = 0; i < 5; i++)
            await remote.SendAsync(Iax2Protocol.FrameType.Voice, 0x04, (uint)(20 * (i + 1)), silence);
        await Task.Delay(200);
        Assert.False(rx.IsReceiving("2000"));

        await remote.SendAsync(Iax2Protocol.FrameType.Voice, 0x04, 200, new byte[160]);
        for (int i = 0; i < 40 && !rx.IsReceiving("2000"); i++)
            await Task.Delay(10);
        Assert.True(rx.IsReceiving("2000"));
    }

    [Fact]
    public void Unkeyed_and_quiet_is_not_heard() =>
        Assert.False(RxActivity.IsHeard(false, 1000, 1100, 1100, 5000));

    [Fact]
    public async Task Attach_TracksRemoteKeyAndVoice_AndDetachStopsTracking()
    {
        long clock = 1000;
        using var rx = new RxActivity(() => Volatile.Read(ref clock));
        using var remote = new FakeIaxRemote();

        var settings = new NodeSettings { NodeNumber = "68498" };
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
