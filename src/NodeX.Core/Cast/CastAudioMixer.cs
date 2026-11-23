using NodeX.Core.Audio;

namespace NodeX.Core.Cast;

/// <summary>
/// Thread-safe 8 kHz mono 20 ms frame mixer across up to <see cref="NodeRuntime.MaxLinks"/> active links for Chromecast output.
/// </summary>
public sealed class CastAudioMixer
{
    private const int MaxQueuedFramesPerLink = 5;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<short[]>> _queues = new(StringComparer.Ordinal);
    private readonly int[] _mixAccum = new int[AudioFormat.SamplesPerFrame];

    public void PushFrame(string linkId, ReadOnlySpan<short> pcm16)
    {
        if (string.IsNullOrEmpty(linkId) || pcm16.Length == 0) return;

        short[] copy = new short[AudioFormat.SamplesPerFrame];
        pcm16[..Math.Min(pcm16.Length, AudioFormat.SamplesPerFrame)].CopyTo(copy);

        lock (_gate)
        {
            if (!_queues.TryGetValue(linkId, out var q))
            {
                q = new Queue<short[]>(MaxQueuedFramesPerLink);
                _queues[linkId] = q;
            }
            if (q.Count >= MaxQueuedFramesPerLink)
                q.Dequeue();
            q.Enqueue(copy);
        }
    }

    public void Detach(string linkId)
    {
        lock (_gate) _queues.Remove(linkId);
    }

    public void Clear()
    {
        lock (_gate) _queues.Clear();
    }

    /// <summary>
    /// Pops one 20 ms frame from each active link queue, sums and clamps to PCM16, returning true when at least
    /// one link contributed a frame. Clears <paramref name="output"/> to silence when false.
    /// </summary>
    public bool MixNextFrame(Span<short> output)
    {
        if (output.Length < AudioFormat.SamplesPerFrame)
            throw new ArgumentException($"Output buffer must hold at least {AudioFormat.SamplesPerFrame} samples.", nameof(output));

        lock (_gate)
        {
            Array.Clear(_mixAccum);
            bool any = false;

            foreach (var q in _queues.Values)
            {
                if (q.Count == 0) continue;
                short[] frame = q.Dequeue();
                for (int i = 0; i < AudioFormat.SamplesPerFrame; i++)
                    _mixAccum[i] += frame[i];
                any = true;
            }

            if (!any)
            {
                output[..AudioFormat.SamplesPerFrame].Clear();
                return false;
            }

            for (int i = 0; i < AudioFormat.SamplesPerFrame; i++)
                output[i] = (short)Math.Clamp(_mixAccum[i], short.MinValue, short.MaxValue);
            return true;
        }
    }
}
