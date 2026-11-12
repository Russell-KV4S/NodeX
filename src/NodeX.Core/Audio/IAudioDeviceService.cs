namespace NodeX.Core.Audio;

/// <summary>
/// Platform audio driver abstraction implemented by each UI head (desktop, Android, iOS).
/// All audio is 8000 Hz, 16-bit signed, mono PCM (see <see cref="AudioFormat"/>).
/// </summary>
public interface IAudioDeviceService : IAsyncDisposable
{
    /// <summary>Whether microphone capture is running.</summary>
    bool IsCapturing { get; }

    /// <summary>Whether the playback output is running.</summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Amount of PCM queued in the device output buffer that has not been played yet.
    /// Used by <see cref="NodeAudioEngine"/> to avoid unbounded latency growth from clock drift.
    /// </summary>
    TimeSpan BufferedPlayback { get; }

    /// <summary>Raised when the device reports a recoverable or fatal error.</summary>
    event Action<string>? ErrorOccurred;

    /// <summary>
    /// Starts microphone capture. <paramref name="onPcmSampleBlock"/> is invoked on the driver's
    /// audio thread with blocks of any size; the memory is only valid for the duration of the call.
    /// </summary>
    void StartCapture(Action<ReadOnlyMemory<short>> onPcmSampleBlock);

    /// <summary>Stops microphone capture.</summary>
    void StopCapture();

    /// <summary>Starts the playback output.</summary>
    void StartPlayback();

    /// <summary>Stops playback and discards anything queued.</summary>
    void StopPlayback();

    /// <summary>Queues PCM for playback. The implementation must copy the data.</summary>
    void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm);
}

/// <summary>Fixed audio format used across the NodeX audio pipeline (matches IAX2 G.711 µ-law).</summary>
public static class AudioFormat
{
    public const int SampleRate = G711Ulaw.SampleRate;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int FrameMilliseconds = 20;
    public const int SamplesPerFrame = G711Ulaw.SamplesPer20MsFrame;

    public static TimeSpan SamplesToDuration(int samples) =>
        TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / SampleRate);

    /// <summary>
    /// Samples handed to an output device that it has not played yet, from a running total of samples written and
    /// the device's own playback position when that position is a 32-bit counter that wraps (Android's
    /// <c>AudioTrack.PlaybackHeadPosition</c>: about 149 hours of continuous audio at 8 kHz). The two are compared
    /// modulo 2^32, so the answer stays right across the wrap; comparing the 64-bit total with the wrapped position
    /// directly made the backlog look enormous from then on, and the engine dropped every frame as "device behind".
    /// A position ahead of the total (a stream that was just rebuilt) counts as nothing pending.
    /// </summary>
    public static int PendingPlaybackSamples(long samplesWritten, int devicePosition) =>
        Math.Max(0, unchecked((int)((uint)samplesWritten - (uint)devicePosition)));
}
