namespace NodeX.Core.Audio;

/// <summary>
/// Transmit-audio clean-up applied to each 20 ms microphone frame before G.711 encoding:
/// a 300 Hz high-pass (removes desk/room rumble and wasted low-frequency energy), a slow automatic gain
/// control that brings speech to a consistent radio-friendly level without amplifying pauses, and a soft
/// limiter so loud syllables round off instead of hard-clipping. G.711 itself does nothing to the signal
/// (it just samples at 8 kHz and quantises logarithmically), so without this the radio gets exactly what
/// the microphone hears at whatever level it happens to be.
/// Single-threaded: call from the capture thread only.
/// </summary>
public sealed class MicProcessor
{
    private readonly MicProcessorOptions _o;

    // High-pass biquad (direct form I) state
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    private double _gainDb;                 // current AGC gain
    private readonly float[] _work = new float[AudioFormat.SamplesPerFrame];

    public MicProcessor(MicProcessorOptions? options = null)
    {
        _o = options ?? new MicProcessorOptions();

        // 2nd-order Butterworth high-pass via the bilinear transform (RBJ cookbook).
        double w0 = 2 * Math.PI * _o.HighPassHz / AudioFormat.SampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * 0.70710678118654752);
        double a0 = 1 + alpha;
        _b0 = (1 + cos) / 2 / a0;
        _b1 = -(1 + cos) / a0;
        _b2 = (1 + cos) / 2 / a0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    /// <summary>Current automatic gain in dB (for diagnostics and tests).</summary>
    public double CurrentGainDb => _gainDb;

    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0;
        _gainDb = 0;
    }

    /// <summary>Processes one frame in place. Frames of any length are accepted (up to 20 ms worth).</summary>
    public void Process(Span<short> frame)
    {
        if (frame.IsEmpty) return;
        int n = Math.Min(frame.Length, _work.Length);

        // 1. High-pass
        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double x = frame[i];
            double y = _o.HighPass ? _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2 : x;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            _work[i] = (float)y;
            sumSq += y * y;
        }

        // 2. Automatic gain: decide the gain for the end of this frame, ramp towards it across the frame.
        double startGainDb = _gainDb;
        if (_o.AutoGain)
        {
            double rmsDb = DbFs(Math.Sqrt(sumSq / n));
            if (rmsDb > _o.GateDbfs)
            {
                double wanted = Math.Clamp(_o.TargetRmsDbfs - rmsDb, _o.MinGainDb, _o.MaxGainDb);
                // Turn down quickly (so a shout is tamed at once), turn up slowly (no pumping).
                double rate = wanted < _gainDb ? _o.AttackPerFrame : _o.ReleasePerFrame;
                _gainDb += (wanted - _gainDb) * rate;
            }
            else
            {
                // Pause or background noise only: do not chase it upwards. Drift back towards unity so
                // hiss is never left boosted by the gain that speech needed.
                _gainDb += (0 - _gainDb) * _o.IdleReturnPerFrame;
            }
        }
        else
        {
            _gainDb = 0;
        }

        // 3. Apply (linear ramp of the gain across the frame, avoids clicks) and soft-limit.
        double g0 = Math.Pow(10, startGainDb / 20), g1 = Math.Pow(10, _gainDb / 20);
        double ceiling = _o.LimiterCeiling * 32767.0;
        double knee = ceiling * _o.LimiterKnee;
        for (int i = 0; i < n; i++)
        {
            double g = g0 + (g1 - g0) * (i + 1) / n;
            double v = _work[i] * g;
            double a = Math.Abs(v);
            if (a > knee)
            {
                double room = ceiling - knee;
                a = knee + room * Math.Tanh((a - knee) / room);
            }
            frame[i] = (short)Math.Round(Math.Sign(v) * a);
        }
    }

    private static double DbFs(double rms) => rms <= 1e-9 ? -200 : 20 * Math.Log10(rms / 32768.0);
}

/// <summary>Tuning for <see cref="MicProcessor"/>. Defaults suit speech into an FM node link.</summary>
public sealed record MicProcessorOptions
{
    public bool HighPass { get; init; } = true;
    public double HighPassHz { get; init; } = 300;

    public bool AutoGain { get; init; } = true;

    /// <summary>Speech level the AGC aims for (RMS, dBFS). -21 dBFS RMS is roughly -6 dBFS peaks for speech.</summary>
    public double TargetRmsDbfs { get; init; } = -21;

    /// <summary>Frames quieter than this are treated as pauses and do not raise the gain.</summary>
    public double GateDbfs { get; init; } = -50;

    public double MaxGainDb { get; init; } = 24;
    public double MinGainDb { get; init; } = -12;

    /// <summary>Fraction of the gap closed per 20 ms frame when the gain must fall (~40 ms).</summary>
    public double AttackPerFrame { get; init; } = 0.5;

    /// <summary>Fraction of the gap closed per 20 ms frame when the gain may rise (~0.7 s).</summary>
    public double ReleasePerFrame { get; init; } = 0.03;

    /// <summary>Fraction of the way back to 0 dB per 20 ms frame while only noise is present (~1 s).</summary>
    public double IdleReturnPerFrame { get; init; } = 0.02;

    /// <summary>Output never exceeds this fraction of full scale (0.89 is about -1 dBFS).</summary>
    public double LimiterCeiling { get; init; } = 0.89;

    /// <summary>The soft limiter starts bending the signal above this fraction of the ceiling.</summary>
    public double LimiterKnee { get; init; } = 0.7;
}
