namespace NodeX.Core.Audio;

/// <summary>
/// Re-blocks arbitrarily sized capture buffers into fixed 20 ms (160-sample) frames.
/// Not thread-safe; call from a single capture thread.
/// </summary>
public sealed class PcmFramer
{
    private readonly short[] _frame;
    private int _filled;

    public PcmFramer(int frameSamples = AudioFormat.SamplesPerFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSamples);
        _frame = new short[frameSamples];
    }

    public int FrameSamples => _frame.Length;

    /// <summary>Samples currently held waiting for a full frame.</summary>
    public int PendingSamples => _filled;

    /// <summary>
    /// Appends samples and invokes <paramref name="onFrame"/> for each completed frame.
    /// The span passed to the callback is reused; copy it if it must outlive the call.
    /// </summary>
    public void Push(ReadOnlySpan<short> samples, FrameHandler onFrame)
    {
        while (!samples.IsEmpty)
        {
            int take = Math.Min(_frame.Length - _filled, samples.Length);
            samples[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            samples = samples[take..];

            if (_filled == _frame.Length)
            {
                onFrame(_frame);
                _filled = 0;
            }
        }
    }

    public void Reset() => _filled = 0;

    public delegate void FrameHandler(ReadOnlySpan<short> frame);
}
