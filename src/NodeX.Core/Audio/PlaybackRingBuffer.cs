namespace NodeX.Core.Audio;

/// <summary>
/// Bounded 1-second playback ring buffer that accepts 8 kHz mono PCM16 frames from the audio engine
/// and drains at the output device's sample rate (8 kHz pass-through, or stateful linear-interpolating
/// upsampling to rates such as 44.1 kHz or 48 kHz). Underruns are padded with silence; samples beyond
/// capacity are dropped so latency cannot grow unbounded.
/// Not thread-safe; callers synchronize access.
/// </summary>
public sealed class PlaybackRingBuffer
{
    private short[] _ring = [];
    private int _ringHead;
    private int _ringCount;
    private int _outRate = AudioFormat.SampleRate;
    private int _upPhase;
    private short _upPrev;

    public int OutputSampleRate => _outRate;
    public int CapacitySamples => _ring.Length;
    public int BufferedSamples => _ringCount;

    public TimeSpan BufferedDuration =>
        TimeSpan.FromSeconds((double)_ringCount / Math.Max(1, _outRate));

    /// <summary>Configures the ring buffer for a 1-second capacity at <paramref name="outputSampleRate"/>.</summary>
    public void Configure(int outputSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRate);
        _outRate = outputSampleRate;
        _ring = new short[outputSampleRate];
        _ringHead = 0;
        _ringCount = 0;
        _upPhase = 0;
        _upPrev = 0;
    }

    public void Clear()
    {
        _ringHead = 0;
        _ringCount = 0;
    }

    /// <summary>Enqueues 8 kHz mono PCM16 samples, upsampling to <see cref="OutputSampleRate"/> when needed.</summary>
    public void Enqueue(ReadOnlySpan<short> pcm8k)
    {
        if (pcm8k.IsEmpty || _ring.Length == 0) return;

        if (_outRate == AudioFormat.SampleRate)
        {
            foreach (short s in pcm8k) Push(s);
            return;
        }

        // Exact rational-phase linear-interpolating up-sampler (8 kHz -> device rate), stateful across blocks.
        int i = 0;
        while (true)
        {
            // _upPhase in [0, _outRate) is the position between _upPrev (at 0) and pcm8k[i] (at _outRate).
            while (_upPhase >= _outRate)
            {
                _upPhase -= _outRate;
                _upPrev = i < pcm8k.Length ? pcm8k[i] : _upPrev;
                i++;
            }
            if (i >= pcm8k.Length) break;
            double v = _upPrev + (pcm8k[i] - _upPrev) * ((double)_upPhase / _outRate);
            Push((short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue));
            _upPhase += AudioFormat.SampleRate;
        }
    }

    /// <summary>
    /// Drains up to <paramref name="destination"/>.Length samples into <paramref name="destination"/> and
    /// zero-fills any remaining space (silence on underrun). Returns the number of buffered samples read.
    /// </summary>
    public int Read(Span<short> destination)
    {
        if (destination.IsEmpty) return 0;
        if (_ring.Length == 0 || _ringCount == 0)
        {
            destination.Clear();
            return 0;
        }

        int n = Math.Min(destination.Length, _ringCount);
        for (int k = 0; k < n; k++)
            destination[k] = _ring[(_ringHead + k) % _ring.Length];

        _ringHead = (_ringHead + n) % _ring.Length;
        _ringCount -= n;

        if (n < destination.Length)
            destination[n..].Clear();

        return n;
    }

    private void Push(short s)
    {
        if (_ringCount == _ring.Length) return;
        _ring[(_ringHead + _ringCount) % _ring.Length] = s;
        _ringCount++;
    }
}
