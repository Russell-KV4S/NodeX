namespace NodeX.Core.Audio;

/// <summary>
/// Streaming area-averaging resampler to the 8 kHz mono PCM16 pipeline format. Averaging over each
/// output sample's input span doubles as a simple anti-alias low-pass, which is adequate for
/// narrow-band voice. Keeps fractional state between calls, so blocks may be any size.
/// Not thread-safe.
/// </summary>
public sealed class PcmResampler
{
    private readonly double _ratio; // input samples per output sample
    private double _acc;
    private double _need;

    public PcmResampler(int inputSampleRate, int outputSampleRate = AudioFormat.SampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRate);
        InputSampleRate = inputSampleRate;
        OutputSampleRate = outputSampleRate;
        _ratio = (double)inputSampleRate / outputSampleRate;
        _need = _ratio;
    }

    public int InputSampleRate { get; }
    public int OutputSampleRate { get; }

    /// <summary>Upper bound on output samples produced by <see cref="Process(ReadOnlySpan{float}, Span{short})"/>.</summary>
    public int MaxOutputSamples(int inputSamples) => (int)Math.Ceiling(inputSamples / _ratio) + 1;

    /// <summary>Resamples float samples in the range -1..1; returns the number of samples written.</summary>
    public int Process(ReadOnlySpan<float> input, Span<short> output)
    {
        if (output.Length < MaxOutputSamples(input.Length))
            throw new ArgumentException("Output span is too small; use MaxOutputSamples().", nameof(output));

        int written = 0;
        foreach (float x in input)
        {
            double avail = 1.0;
            while (avail > 1e-12)
            {
                double take = Math.Min(avail, _need);
                _acc += x * take;
                avail -= take;
                _need -= take;
                if (_need <= 1e-9)
                {
                    double v = _acc / _ratio * 32767.0;
                    output[written++] = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
                    _acc = 0;
                    _need = _ratio;
                }
            }
        }
        return written;
    }

    /// <summary>Resamples PCM16 samples; returns the number of samples written.</summary>
    public int Process(ReadOnlySpan<short> input, Span<short> output)
    {
        if (output.Length < MaxOutputSamples(input.Length))
            throw new ArgumentException("Output span is too small; use MaxOutputSamples().", nameof(output));

        int written = 0;
        foreach (short s in input)
        {
            double avail = 1.0;
            while (avail > 1e-12)
            {
                double take = Math.Min(avail, _need);
                _acc += s * take;
                avail -= take;
                _need -= take;
                if (_need <= 1e-9)
                {
                    output[written++] = (short)Math.Clamp(Math.Round(_acc / _ratio), short.MinValue, short.MaxValue);
                    _acc = 0;
                    _need = _ratio;
                }
            }
        }
        return written;
    }

    public void Reset()
    {
        _acc = 0;
        _need = _ratio;
    }
}
