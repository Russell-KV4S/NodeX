namespace NodeX.Core.Audio;

/// <summary>Instantaneous level of a PCM block. Linear values are normalized to 0..1 of full scale.</summary>
public readonly record struct AudioLevels(double Rms, double Peak)
{
    public static readonly AudioLevels Silent = new(0, 0);

    /// <summary>Floor used for dBFS of digital silence.</summary>
    public const double MinDbfs = -96.0;

    public double RmsDbfs => ToDbfs(Rms);
    public double PeakDbfs => ToDbfs(Peak);

    public static double ToDbfs(double linear) =>
        linear <= 0 ? MinDbfs : Math.Max(MinDbfs, 20 * Math.Log10(linear));
}

/// <summary>
/// RMS / peak level calculation plus VU-style ballistics (fast attack, slower release)
/// for driving UI level meters.
/// </summary>
public sealed class AudioLevelMeter
{
    /// <summary>Computes RMS and peak of a block without allocating.</summary>
    public static AudioLevels Measure(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty) return AudioLevels.Silent;

        double sumSquares = 0;
        int peak = 0;
        foreach (short s in pcm)
        {
            int v = s;
            sumSquares += (double)v * v;
            int abs = v < 0 ? -v : v; // -32768 -> 32768 is fine in int
            if (abs > peak) peak = abs;
        }

        double rms = Math.Sqrt(sumSquares / pcm.Length) / 32768.0;
        return new AudioLevels(Math.Min(1.0, rms), Math.Min(1.0, peak / 32768.0));
    }

    /// <summary>Fraction of the gap closed per update when the level rises (0..1].</summary>
    public double Attack { get; set; } = 0.8;

    /// <summary>Fraction of the gap closed per update when the level falls (0..1].</summary>
    public double Release { get; set; } = 0.15;

    /// <summary>Most recent smoothed level.</summary>
    public AudioLevels Current { get; private set; } = AudioLevels.Silent;

    /// <summary>Feeds a block, applying ballistics, and returns the smoothed level.</summary>
    public AudioLevels Process(ReadOnlySpan<short> pcm) => Update(Measure(pcm));

    /// <summary>Applies ballistics to an instantaneous measurement.</summary>
    public AudioLevels Update(AudioLevels instant)
    {
        Current = new AudioLevels(Smooth(Current.Rms, instant.Rms), Smooth(Current.Peak, instant.Peak));
        return Current;
    }

    public void Reset() => Current = AudioLevels.Silent;

    private double Smooth(double previous, double next) =>
        previous + (next - previous) * (next > previous ? Attack : Release);
}
