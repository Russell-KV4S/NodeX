using NodeX.Core.Audio;

namespace NodeX.Core.Tests;

public class MicProcessorTests
{
    private const int Frame = AudioFormat.SamplesPerFrame;

    /// <summary>Generates <paramref name="frames"/> frames of a sine at <paramref name="rmsDbfs"/> (phase continuous).</summary>
    private static List<short[]> Tone(double hz, double rmsDbfs, int frames)
    {
        double amp = 32768.0 * Math.Pow(10, rmsDbfs / 20) * Math.Sqrt(2);
        var list = new List<short[]>();
        long n = 0;
        for (int f = 0; f < frames; f++)
        {
            var s = new short[Frame];
            for (int i = 0; i < Frame; i++, n++)
                s[i] = (short)Math.Round(amp * Math.Sin(2 * Math.PI * hz * n / AudioFormat.SampleRate));
            list.Add(s);
        }
        return list;
    }

    private static double RmsDb(short[] s) =>
        20 * Math.Log10(Math.Max(1e-9, Math.Sqrt(s.Select(x => (double)x * x).Average())) / 32768.0);

    private static int Peak(IEnumerable<short[]> frames) => frames.SelectMany(f => f).Max(x => Math.Abs((int)x));

    private static short[] Run(MicProcessor p, List<short[]> input)
    {
        short[] last = [];
        foreach (var frame in input)
        {
            last = (short[])frame.Clone();
            p.Process(last);
        }
        return last;
    }

    [Fact]
    public void HighPass_RemovesRumble_AndLeavesSpeechBandAlone()
    {
        var opts = new MicProcessorOptions { AutoGain = false };

        var rumble = Run(new MicProcessor(opts), Tone(80, -20, 50));
        var voice = Run(new MicProcessor(opts), Tone(1000, -20, 50));

        Assert.True(RmsDb(rumble) < -20 - 18, $"80 Hz only attenuated to {RmsDb(rumble):F1} dBFS");
        Assert.InRange(RmsDb(voice), -21.5, -18.5);                         // 1 kHz within 1.5 dB
    }

    [Fact]
    public void AutoGain_BringsAQuietVoiceUpToTheTargetLevel()
    {
        var p = new MicProcessor();
        var last = Run(p, Tone(1000, -40, 200));                            // 4 s at -40 dBFS RMS

        Assert.InRange(RmsDb(last), -24, -18);                              // target is -21
        Assert.True(p.CurrentGainDb > 15, $"gain only {p.CurrentGainDb:F1} dB");
    }

    [Fact]
    public void AutoGain_TamesALoudVoice_AndNeverExceedsTheLimiterCeiling()
    {
        var p = new MicProcessor();
        var all = new List<short[]>();
        foreach (var frame in Tone(1000, -4, 200))                          // very hot: peaks about -1 dBFS
        {
            var copy = (short[])frame.Clone();
            p.Process(copy);
            all.Add(copy);
        }

        // The AGC can turn down by at most 12 dB, so -4 dBFS RMS ends near -16.
        Assert.True(p.CurrentGainDb < -8, $"gain {p.CurrentGainDb:F1} dB");
        Assert.InRange(RmsDb(all[^1]), -19, -13);
        Assert.True(Peak(all) <= 0.89 * 32767 + 1, $"peak {Peak(all)} exceeds the ceiling");
    }

    [Fact]
    public void Limiter_RoundsOffFullScaleSquareWaves_WithoutHardClipping()
    {
        var p = new MicProcessor(new MicProcessorOptions { HighPass = false, AutoGain = false });
        var frame = new short[Frame];
        for (int i = 0; i < Frame; i++) frame[i] = (i / 20) % 2 == 0 ? short.MaxValue : short.MinValue;

        p.Process(frame);

        Assert.All(frame, s => Assert.InRange(Math.Abs((int)s), 0, (int)(0.89 * 32767) + 1));
        Assert.Contains(frame, s => Math.Abs((int)s) > 0.85 * 32767);       // still loud, just rounded
    }

    [Fact]
    public void Pauses_AreNotAmplified_AndGainRelaxesAfterSpeech()
    {
        var p = new MicProcessor();
        Run(p, Tone(1000, -40, 200));                                       // speech: gain climbs
        double afterSpeech = p.CurrentGainDb;
        Assert.True(afterSpeech > 15);

        // Then 6 s of background hiss at -62 dBFS RMS (below the gate): must not be boosted further, and
        // the gain must drift back towards unity instead of leaving the hiss amplified.
        var rng = new Random(1);
        double amp = 32768 * Math.Pow(10, -62.0 / 20) * Math.Sqrt(3);
        for (int f = 0; f < 300; f++)
        {
            var noise = new short[Frame];
            for (int i = 0; i < Frame; i++) noise[i] = (short)((rng.NextDouble() * 2 - 1) * amp);
            p.Process(noise);
            Assert.True(p.CurrentGainDb <= afterSpeech + 1e-9);
        }
        Assert.InRange(p.CurrentGainDb, -0.5, 3);
    }

    [Fact]
    public void Silence_StaysSilence_AndGainStaysAtUnity()
    {
        var p = new MicProcessor();
        var frame = new short[Frame];
        for (int i = 0; i < 100; i++) p.Process(frame);

        Assert.All(frame, s => Assert.Equal(0, s));
        Assert.Equal(0, p.CurrentGainDb, 6);
    }

    [Fact]
    public void WithEverythingOff_ASmallSignalPassesThroughUnchanged()
    {
        var p = new MicProcessor(new MicProcessorOptions { HighPass = false, AutoGain = false });
        short[] input = Tone(1000, -30, 1)[0];
        short[] copy = (short[])input.Clone();

        p.Process(copy);

        Assert.Equal(input, copy);
    }

    [Fact]
    public void Gain_ChangesAreRampedWithinAFrame_NoStepsOrClicks()
    {
        var p = new MicProcessor(new MicProcessorOptions { HighPass = false });
        Run(p, Tone(1000, -45, 5));                                         // gain starts moving
        var frame = Tone(1000, -45, 1)[0];
        short[] before = (short[])frame.Clone();
        p.Process(frame);

        // Adjacent samples of a 1 kHz tone at 8 kHz differ by a bounded amount; a gain step inside the
        // frame would show as an outsized jump relative to the signal.
        int maxIn = 0, maxOut = 0;
        for (int i = 1; i < Frame; i++)
        {
            maxIn = Math.Max(maxIn, Math.Abs(before[i] - before[i - 1]));
            maxOut = Math.Max(maxOut, Math.Abs(frame[i] - frame[i - 1]));
        }
        double gainLin = Math.Pow(10, p.CurrentGainDb / 20);
        Assert.True(maxOut <= maxIn * gainLin * 1.15, $"max step {maxOut} vs expected {maxIn * gainLin:F0}");
    }
}

public class VoiceTimestampTests
{
    private static uint Next(bool over, uint last, uint wall) =>
        Iax2Session.NextVoiceTimestamp(over, last, wall, 160);

    [Fact]
    public void FirstFrameOfAnOver_UsesTheWallClock()
    {
        Assert.Equal(12345u, Next(false, 999, 12345));
    }

    [Fact]
    public void JitteryArrivalTimes_StillGiveEvenTwentyMillisecondSpacing()
    {
        // Wall-clock send times wobbling by up to +-8 ms (as measured live: 16 to 24 ms gaps).
        uint[] wall = [1000, 1012, 1048, 1060, 1089, 1100, 1129, 1141, 1170, 1180, 1209, 1222, 1250];
        uint last = wall[0];
        var deltas = new List<uint>();
        for (int i = 1; i < wall.Length; i++)
        {
            uint ts = Next(true, last, wall[i]);
            deltas.Add(ts - last);
            last = ts;
        }

        Assert.All(deltas, d => Assert.InRange(d, 19u, 21u));
    }

    [Fact]
    public void ClockDrift_IsFollowedSlowlyAndNeverAccumulates()
    {
        // Capture clock runs 1% slow: wall time advances 20.2 ms per frame. The timeline may not fall behind
        // the wall clock by more than a few ms however long the over lasts.
        uint last = 5000;
        double wall = 5000;
        for (int i = 0; i < 3000; i++)                                       // 60 s of voice
        {
            wall += 20.2;
            last = Next(true, last, (uint)wall);
        }
        Assert.InRange(Math.Abs((double)last - wall), 0, 8);
    }

    [Fact]
    public void ARealGap_RestartsTheTimelineFromTheWallClock()
    {
        uint ts = Next(true, 1000, 1500);                                    // 500 ms stall (e.g. dropped frames)
        Assert.Equal(1500u, ts);
    }

    [Fact]
    public void Timestamps_StayMonotonic_AcrossTheSixteenBitWrap()
    {
        uint last = 65530;
        for (int i = 0; i < 10; i++)
        {
            uint ts = Next(true, last, last + 20);
            Assert.True(ts > last);
            last = ts;
        }
        Assert.True(last > 65536);                                           // crossed the 16-bit boundary smoothly
    }
}
