using System.Globalization;

namespace NodeX.Core.Audio;

/// <summary>Tuning for <see cref="RxAudioProcessor"/>.</summary>
public sealed record RxAudioProcessorOptions
{
    /// <summary>Speech RMS level (dBFS) that auto-level aims for on incoming stations.</summary>
    public double TargetRmsDbfs { get; init; } = -15;

    /// <summary>Frames quieter than this are treated as pauses/hiss and do not raise auto-level gain.</summary>
    public double GateDbfs { get; init; } = -44;

    public double MaxGainDb { get; init; } = 18;
    public double MinGainDb { get; init; } = -6;

    /// <summary>Fraction of the gap closed per 20 ms frame when auto-level must attenuate a loud station.</summary>
    public double AttackPerFrame { get; init; } = 0.55;

    /// <summary>Fraction of the gap closed per 20 ms frame when auto-level may boost a quiet station.</summary>
    public double ReleasePerFrame { get; init; } = 0.05;

    /// <summary>Fraction of the way back to 0 dB per 20 ms frame during silence/noise below gate.</summary>
    public double IdleReturnPerFrame { get; init; } = 0.025;

    /// <summary>Soft-limiter ceiling as a fraction of full scale (0.94 ≈ -0.5 dBFS).</summary>
    public double LimiterCeiling { get; init; } = 0.94;

    /// <summary>Fraction of <see cref="LimiterCeiling"/> where soft-knee saturation begins.</summary>
    public double LimiterKnee { get; init; } = 0.72;
}

/// <summary>
/// Receive-side audio conditioning applied before playback: manual gain (<see cref="Gain"/>), optional gated
/// automatic level control (<see cref="AutoLevel"/>), smooth frame-to-frame gain ramping, and a <c>tanh</c> soft
/// limiter so boosted stations get louder without harsh digital clipping.
/// <para>
/// <see cref="Gain"/> and <see cref="AutoLevel"/> can be updated live from any thread; <see cref="Process"/>,
/// <see cref="ObserveSilence"/>, and <see cref="Reset"/> run on the single playout thread.
/// </para>
/// </summary>
public sealed class RxAudioProcessor
{
    public const float MinGain = 0.25f;
    public const float MaxGain = 8.0f;

    private readonly RxAudioProcessorOptions _o;
    private float _gain = 1.0f;
    private bool _autoLevel;
    private double _agcGainDb;
    private double _prevLinearGain = 1.0;

    public RxAudioProcessor(RxAudioProcessorOptions? options = null)
    {
        _o = options ?? new RxAudioProcessorOptions();
    }

    /// <summary>Manual receive gain multiplier (clamped to 0.25–8.0; 1.0 = unity). Thread-safe.</summary>
    public float Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, SanitizeGain(value));
    }

    /// <summary>Whether gated automatic level control is active on incoming audio. Thread-safe.</summary>
    public bool AutoLevel
    {
        get => Volatile.Read(ref _autoLevel);
        set => Volatile.Write(ref _autoLevel, value);
    }

    /// <summary>Current automatic gain in dB (for diagnostics and tests).</summary>
    public double CurrentAgcGainDb => _agcGainDb;

    /// <summary>Resets internal ramp and AGC state to match the current <see cref="Gain"/>.</summary>
    public void Reset()
    {
        _agcGainDb = 0;
        _prevLinearGain = Gain;
    }

    /// <summary>Advances idle AGC decay when a frame carries no active audio, without scanning samples.</summary>
    public void ObserveSilence()
    {
        if (!AutoLevel)
        {
            _agcGainDb = 0;
        }
        else if (_agcGainDb != 0)
        {
            _agcGainDb += (0 - _agcGainDb) * _o.IdleReturnPerFrame;
            if (Math.Abs(_agcGainDb) < 1e-4) _agcGainDb = 0;
        }
        _prevLinearGain = Gain;
    }

    /// <summary>Processes one 20 ms PCM frame in place.</summary>
    public void Process(Span<short> frame)
    {
        if (frame.IsEmpty) return;

        float manual = Gain;
        bool auto = AutoLevel;

        // Bit-exact pass-through at default unity settings when no ramp/AGC tail remains.
        if (!auto && Math.Abs(manual - 1.0f) < 1e-4f && Math.Abs(_agcGainDb) < 1e-3 && Math.Abs(_prevLinearGain - 1.0) < 1e-4)
        {
            _agcGainDb = 0;
            _prevLinearGain = 1.0;
            return;
        }

        if (auto)
        {
            double sumSq = 0;
            for (int i = 0; i < frame.Length; i++)
            {
                double s = frame[i];
                sumSq += s * s;
            }

            double rmsDb = DbFs(Math.Sqrt(sumSq / frame.Length));
            if (rmsDb > _o.GateDbfs)
            {
                double wanted = Math.Clamp(_o.TargetRmsDbfs - rmsDb, _o.MinGainDb, _o.MaxGainDb);
                double rate = wanted < _agcGainDb ? _o.AttackPerFrame : _o.ReleasePerFrame;
                _agcGainDb += (wanted - _agcGainDb) * rate;
            }
            else
            {
                _agcGainDb += (0 - _agcGainDb) * _o.IdleReturnPerFrame;
                if (Math.Abs(_agcGainDb) < 1e-4) _agcGainDb = 0;
            }
        }
        else
        {
            _agcGainDb = 0;
        }

        double g0 = _prevLinearGain;
        double g1 = manual * Math.Pow(10.0, _agcGainDb / 20.0);
        double ceiling = _o.LimiterCeiling * 32767.0;
        double knee = ceiling * _o.LimiterKnee;
        double room = ceiling - knee;
        int n = frame.Length;

        for (int i = 0; i < n; i++)
        {
            double g = g0 + (g1 - g0) * (i + 1) / n;
            double v = frame[i] * g;
            double a = Math.Abs(v);
            if (a > knee && room > 0)
                a = knee + room * Math.Tanh((a - knee) / room);
            double limited = Math.CopySign(a, v);
            frame[i] = (short)Math.Clamp(Math.Round(limited), short.MinValue, short.MaxValue);
        }

        _prevLinearGain = g1;
    }

    /// <summary>Clamps an untrusted gain value to the valid range, falling back to unity when non-finite.</summary>
    public static float SanitizeGain(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, MinGain, MaxGain) : 1.0f;

    /// <summary>
    /// Computes Android <c>LoudnessEnhancer</c> target boost in millibels (+600 mB at ≤1× scaling up to
    /// +1200 mB at ≥4×), or 0 when disabled.
    /// </summary>
    public static int ComputeAndroidLoudnessMillibels(float rxGain, bool loudnessBoost)
    {
        if (!loudnessBoost || !float.IsFinite(rxGain)) return 0;
        float t = Math.Clamp((rxGain - 1.0f) / 3.0f, 0f, 1f);
        return 600 + (int)Math.Round(t * 600f);
    }

    /// <summary>Formats a gain multiplier together with its rounded dB value (for example, <c>2.0× (+6 dB)</c>).</summary>
    public static string FormatGainWithDb(float rxGain)
    {
        float clamped = SanitizeGain(rxGain);
        int db = (int)Math.Round(20.0 * Math.Log10(clamped));
        string dbText = db > 0 ? $"+{db} dB" : $"{db} dB";
        return string.Create(CultureInfo.InvariantCulture, $"{clamped:0.0}× ({dbText})");
    }

    private static double DbFs(double rms) => rms <= 1e-9 ? -200 : 20 * Math.Log10(rms / 32768.0);
}
