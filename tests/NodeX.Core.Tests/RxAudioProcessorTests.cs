using NodeX.Core.Audio;

namespace NodeX.Core.Tests;

public class RxAudioProcessorTests
{
    private const int Frame = AudioFormat.SamplesPerFrame;

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

    private static short[] Run(RxAudioProcessor p, List<short[]> input)
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
    public void UnityGain_WithoutAutoLevel_IsBitExactNoOp()
    {
        var p = new RxAudioProcessor { Gain = 1.0f, AutoLevel = false };
        short[] input = Tone(1000, -18, 1)[0];
        short[] copy = (short[])input.Clone();

        p.Process(copy);

        Assert.Equal(input, copy);
        Assert.Equal(0, p.CurrentAgcGainDb, 6);
    }

    [Fact]
    public void ManualBoost_AmplifiesQuietSignal_AndSoftLimitsWithoutHardClipping()
    {
        var p = new RxAudioProcessor { Gain = 4.0f, AutoLevel = false };

        var quiet = Tone(1000, -30, 2);
        var boosted = Run(p, quiet);
        // After the 1-frame ramp settles, +12 dB (4x) lifts -30 dBFS to ~-18 dBFS.
        Assert.InRange(RmsDb(boosted), -19.5, -16.5);

        // Full-scale square wave at 4x must be softly clamped under LimiterCeiling (0.94 * 32767 ≈ 30801).
        var hot = new short[Frame];
        for (int i = 0; i < Frame; i++) hot[i] = (i / 20) % 2 == 0 ? short.MaxValue : short.MinValue;
        p.Process(hot);
        int ceiling = (int)(new RxAudioProcessorOptions().LimiterCeiling * 32767) + 1;
        Assert.All(hot, s => Assert.InRange(Math.Abs((int)s), 0, ceiling));
        Assert.Contains(hot, s => Math.Abs((int)s) > 0.88 * 32767);
    }

    [Fact]
    public void AutoLevel_BringsQuietStationUp_AndTamesHotStation()
    {
        var quietProc = new RxAudioProcessor { Gain = 1.0f, AutoLevel = true };
        var quietOut = Run(quietProc, Tone(1000, -32, 160));
        Assert.True(quietProc.CurrentAgcGainDb > 12, $"gain only {quietProc.CurrentAgcGainDb:F1} dB");
        Assert.InRange(RmsDb(quietOut), -18.5, -12.5);

        var hotProc = new RxAudioProcessor { Gain = 1.0f, AutoLevel = true };
        var allHot = new List<short[]>();
        foreach (var f in Tone(1000, -4, 100))
        {
            var copy = (short[])f.Clone();
            hotProc.Process(copy);
            allHot.Add(copy);
        }
        Assert.True(hotProc.CurrentAgcGainDb < -4, $"hot gain {hotProc.CurrentAgcGainDb:F1} dB");
        Assert.InRange(RmsDb(allHot[^1]), -13.5, -7.5); // -4 dBFS with MinGainDb = -6 settles near -10 dBFS
        Assert.True(Peak(allHot) <= 0.94 * 32767 + 2);
    }

    [Fact]
    public void AutoLevel_IgnoresNoiseBelowGate_AndRelaxesTowardUnity()
    {
        var p = new RxAudioProcessor { Gain = 1.0f, AutoLevel = true };
        Run(p, Tone(1000, -32, 160));
        double afterSpeech = p.CurrentAgcGainDb;
        Assert.True(afterSpeech > 12);

        // Background hiss at -56 dBFS (well below -44 dBFS gate) must not be chased up; gain decays back.
        var rng = new Random(7);
        double amp = 32768 * Math.Pow(10, -56.0 / 20) * Math.Sqrt(3);
        for (int f = 0; f < 300; f++)
        {
            var noise = new short[Frame];
            for (int i = 0; i < Frame; i++) noise[i] = (short)((rng.NextDouble() * 2 - 1) * amp);
            p.Process(noise);
            Assert.True(p.CurrentAgcGainDb <= afterSpeech + 1e-9);
        }
        Assert.InRange(p.CurrentAgcGainDb, -0.5, 2.0);
    }

    [Fact]
    public void ObserveSilence_RelaxesAutoLevelTowardZero_AndUpdatesRampGain()
    {
        var p = new RxAudioProcessor { Gain = 1.0f, AutoLevel = true };
        Run(p, Tone(1000, -32, 100));
        double before = p.CurrentAgcGainDb;
        Assert.True(before > 10);

        for (int i = 0; i < 300; i++) p.ObserveSilence();
        Assert.InRange(p.CurrentAgcGainDb, -0.1, 0.5);

        p.AutoLevel = false;
        p.Gain = 2.0f;
        p.ObserveSilence();
        Assert.Equal(0, p.CurrentAgcGainDb, 6);
    }

    [Theory]
    [InlineData(1.0f, false, 0)]
    [InlineData(4.0f, false, 0)]
    [InlineData(float.NaN, true, 0)]
    [InlineData(0.5f, true, 600)]
    [InlineData(1.0f, true, 600)]
    [InlineData(2.5f, true, 900)]
    [InlineData(4.0f, true, 1200)]
    [InlineData(8.0f, true, 1200)]
    public void ComputeAndroidLoudnessMillibels_ScalesFrom600To1200_WhenEnabled(float rxGain, bool enabled, int expectedMb) =>
        Assert.Equal(expectedMb, RxAudioProcessor.ComputeAndroidLoudnessMillibels(rxGain, enabled));

    [Theory]
    [InlineData(0.5f, "0.5× (-6 dB)")]
    [InlineData(1.0f, "1.0× (0 dB)")]
    [InlineData(2.0f, "2.0× (+6 dB)")]
    [InlineData(4.0f, "4.0× (+12 dB)")]
    [InlineData(float.NaN, "1.0× (0 dB)")]
    public void FormatGainWithDb_FormatsExpectedStrings(float rxGain, string expected) =>
        Assert.Equal(expected, RxAudioProcessor.FormatGainWithDb(rxGain));
}
