namespace NodeX.Core.Audio;

/// <summary>Outcome of pulling one frame from the <see cref="JitterBuffer"/>.</summary>
public enum JitterPopResult
{
    /// <summary>A received frame was played.</summary>
    Played,
    /// <summary>The expected frame was missing; a faded copy of the last frame was produced.</summary>
    Concealed,
    /// <summary>Nothing to play (buffering, idle, or loss beyond the concealment limit): silence.</summary>
    Silence
}

/// <summary>Running counters for diagnostics.</summary>
public readonly record struct JitterBufferStats(
    long Received, long Played, long Concealed, long Lost, long Late, long Duplicate, long Overflow, int Depth);

/// <summary>
/// Adaptive-free fixed-depth jitter buffer for 20 ms voice frames keyed by the IAX2 16-bit
/// millisecond timestamp. Reorders out-of-order packets, drops late/duplicate ones, conceals
/// short losses by repeating the last frame with a fade, and re-primes after the stream goes idle
/// (e.g. between overs). Thread-safe: <see cref="Push"/> runs on the network thread while
/// <see cref="Pop"/> runs on the playout clock.
/// </summary>
public sealed class JitterBuffer
{
    private readonly int _frameSamples;
    private readonly int _frameMs;
    private readonly short[][] _slots;
    private readonly bool[] _present;
    private readonly short[] _lastFrame;
    private readonly Lock _lock = new();

    private int _head;            // ring index of the next frame to play
    private ushort _nextTs;       // timestamp expected at _head
    private bool _priming = true;
    private int _consecutiveMisses;
    private int _depth;

    private long _received, _played, _concealed, _lost, _late, _duplicate, _overflow;

    /// <param name="targetDepthFrames">Frames buffered before playout starts (latency = depth × 20 ms).</param>
    /// <param name="capacityFrames">Maximum frames held; packets further ahead trigger a resync.</param>
    /// <param name="maxConcealFrames">Consecutive missing frames to conceal before outputting silence.</param>
    public JitterBuffer(
        int targetDepthFrames = 3,
        int capacityFrames = 25,
        int maxConcealFrames = 3,
        int frameSamples = AudioFormat.SamplesPerFrame,
        int frameMilliseconds = AudioFormat.FrameMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetDepthFrames);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, targetDepthFrames + 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcealFrames);

        TargetDepthFrames = targetDepthFrames;
        MaxConcealFrames = maxConcealFrames;
        _frameSamples = frameSamples;
        _frameMs = frameMilliseconds;
        _slots = new short[capacityFrames][];
        for (int i = 0; i < capacityFrames; i++) _slots[i] = new short[frameSamples];
        _present = new bool[capacityFrames];
        _lastFrame = new short[frameSamples];
    }

    public int TargetDepthFrames { get; }
    public int MaxConcealFrames { get; }
    public int CapacityFrames => _slots.Length;
    public int FrameSamples => _frameSamples;

    /// <summary>True while waiting to accumulate <see cref="TargetDepthFrames"/> before playing.</summary>
    public bool IsPriming { get { lock (_lock) return _priming; } }

    /// <summary>Frames currently buffered.</summary>
    public int Depth { get { lock (_lock) return _depth; } }

    public JitterBufferStats Stats
    {
        get
        {
            lock (_lock)
                return new JitterBufferStats(_received, _played, _concealed, _lost, _late, _duplicate, _overflow, _depth);
        }
    }

    /// <summary>
    /// Inserts a frame. Samples beyond <see cref="FrameSamples"/> are ignored; short frames are zero-padded.
    /// </summary>
    public void Push(ushort timestamp, ReadOnlySpan<short> pcm)
    {
        lock (_lock)
        {
            _received++;

            if (_priming && _depth == 0)
            {
                // First frame of a new talk spurt anchors the timeline.
                _nextTs = timestamp;
                _head = 0;
            }

            int slot = SlotOffset(timestamp);
            if (slot < 0)
            {
                _late++;
                return;
            }

            if (slot >= _slots.Length)
            {
                // Far ahead of the playout point: the stream jumped (new over, remote restart, or
                // we fell badly behind). Resynchronize on this frame.
                _overflow++;
                ClearSlots();
                _nextTs = timestamp;
                _priming = true;
                slot = 0;
            }

            int idx = (_head + slot) % _slots.Length;
            if (_present[idx])
            {
                _duplicate++;
                return;
            }

            var dest = _slots[idx].AsSpan();
            int n = Math.Min(pcm.Length, _frameSamples);
            pcm[..n].CopyTo(dest);
            dest[n..].Clear();
            _present[idx] = true;
            _depth++;

            if (_priming && _depth >= TargetDepthFrames)
            {
                _priming = false;
                _consecutiveMisses = 0;
            }
        }
    }

    /// <summary>
    /// Produces exactly one frame into <paramref name="output"/> (must hold <see cref="FrameSamples"/>).
    /// Call once per 20 ms of playout.
    /// </summary>
    public JitterPopResult Pop(Span<short> output)
    {
        if (output.Length < _frameSamples)
            throw new ArgumentException($"Output must hold {_frameSamples} samples.", nameof(output));
        output = output[.._frameSamples];

        lock (_lock)
        {
            if (_priming)
            {
                output.Clear();
                return JitterPopResult.Silence;
            }

            JitterPopResult result;
            if (_present[_head])
            {
                _slots[_head].AsSpan().CopyTo(output);
                _slots[_head].AsSpan().CopyTo(_lastFrame);
                _present[_head] = false;
                _depth--;
                _played++;
                _consecutiveMisses = 0;
                result = JitterPopResult.Played;
            }
            else
            {
                _lost++;
                _consecutiveMisses++;
                if (_consecutiveMisses <= MaxConcealFrames)
                {
                    // Repeat the last good frame, halving the gain for each consecutive loss.
                    int shift = _consecutiveMisses;
                    for (int i = 0; i < output.Length; i++) output[i] = (short)(_lastFrame[i] >> shift);
                    _concealed++;
                    result = JitterPopResult.Concealed;
                }
                else
                {
                    output.Clear();
                    result = JitterPopResult.Silence;
                }
            }

            _head = (_head + 1) % _slots.Length;
            _nextTs = unchecked((ushort)(_nextTs + _frameMs));

            if (_depth == 0 && _consecutiveMisses > MaxConcealFrames)
            {
                // Stream has gone idle (end of over): re-prime for the next talk spurt.
                _priming = true;
                Array.Clear(_lastFrame);
            }

            return result;
        }
    }

    /// <summary>Drops everything and returns to the priming state.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            ClearSlots();
            _priming = true;
            _consecutiveMisses = 0;
            Array.Clear(_lastFrame);
        }
    }

    private int SlotOffset(ushort timestamp)
    {
        // Signed 16-bit difference handles timestamp wrap-around.
        int diffMs = unchecked((short)(timestamp - _nextTs));
        // Round to the nearest frame to tolerate timestamp jitter from the sender's clock.
        return (int)Math.Round(diffMs / (double)_frameMs, MidpointRounding.AwayFromZero);
    }

    private void ClearSlots()
    {
        Array.Clear(_present);
        _depth = 0;
        _head = 0;
    }
}
