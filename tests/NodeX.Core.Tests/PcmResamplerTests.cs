using NodeX.Core.Audio;

namespace NodeX.Core.Tests;

public class PcmResamplerTests
{
    private static float[] Sine(int rate, double hz, int n, double amp = 0.5) =>
        Enumerable.Range(0, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    private static short[] Run(PcmResampler r, float[] input, int chunk)
    {
        var result = new List<short>();
        for (int i = 0; i < input.Length; i += chunk)
        {
            var part = input.AsSpan(i, Math.Min(chunk, input.Length - i));
            short[] buf = new short[r.MaxOutputSamples(part.Length)];
            int n = r.Process(part, buf);
            result.AddRange(buf.AsSpan(0, n).ToArray());
        }
        return result.ToArray();
    }

    [Fact]
    public void SameRate_IsPassThrough()
    {
        var r = new PcmResampler(8000);
        short[] input = [0, 100, -100, 32767, -32768, 5];
        short[] output = new short[r.MaxOutputSamples(input.Length)];
        int n = r.Process(input, output);
        Assert.Equal(input, output[..n]);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(16000)]
    public void OutputCount_MatchesRateRatio(int rate)
    {
        var r = new PcmResampler(rate);
        var output = Run(r, Sine(rate, 440, rate), 480); // 1 second
        Assert.InRange(output.Length, 7999, 8000);
    }

    [Fact]
    public void DcLevel_IsPreserved()
    {
        var r = new PcmResampler(48000);
        var output = Run(r, Enumerable.Repeat(0.25f, 4800).ToArray(), 1000);
        Assert.All(output, s => Assert.InRange(s, 8190, 8194)); // 0.25 * 32767
    }

    [Fact]
    public void Streaming_IsIndependentOfChunkSize()
    {
        float[] input = Sine(44100, 700, 4410);
        short[] whole = Run(new PcmResampler(44100), input, input.Length);
        short[] chunked = Run(new PcmResampler(44100), input, 37);
        Assert.Equal(whole, chunked);
    }

    [Fact]
    public void InBandTone_KeepsAmplitude_OutOfBandTone_IsAttenuated()
    {
        static double Rms(short[] s) => Math.Sqrt(s.Skip(40).Average(v => (double)v * v)) / 32767.0;

        double inBand = Rms(Run(new PcmResampler(48000), Sine(48000, 400, 48000), 480));
        double outOfBand = Rms(Run(new PcmResampler(48000), Sine(48000, 9000, 48000), 480));

        Assert.InRange(inBand, 0.30, 0.36);      // 0.5 / sqrt(2) = 0.354, minus box-filter droop
        Assert.True(outOfBand < inBand / 3, $"out-of-band {outOfBand:F3} not attenuated vs {inBand:F3}");
    }

    [Fact]
    public void ShortInput_ProducesNoOverflow_AndClips()
    {
        var r = new PcmResampler(48000);
        short[] output = new short[r.MaxOutputSamples(6)];
        int n = r.Process(new float[] { 5f, 5f, 5f, 5f, 5f, 5f }, output);
        Assert.Equal(1, n);
        Assert.Equal(short.MaxValue, output[0]);
    }

    [Fact]
    public void SmallOutputSpan_Throws()
    {
        var r = new PcmResampler(48000);
        Assert.Throws<ArgumentException>(() => r.Process(new float[480], new short[2]));
    }

    [Fact]
    public void Reset_DiscardsPartialSample()
    {
        var r = new PcmResampler(48000);
        short[] o = new short[10];
        Assert.Equal(0, r.Process(new float[3], o)); // half an output sample pending
        r.Reset();
        Assert.Equal(1, r.Process(new float[6], o));
    }
}
