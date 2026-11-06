namespace NodeX.Core.Audio;

/// <summary>
/// An <see cref="IAudioDeviceService"/> that captures nothing and plays nothing. Used by the
/// designer and as a fallback on platforms without an audio backend yet, so the app still runs
/// (signalling and registration work; there is simply no sound).
/// </summary>
public sealed class NullAudioDeviceService : IAudioDeviceService
{
    public bool IsCapturing { get; private set; }
    public bool IsPlaying { get; private set; }
    public TimeSpan BufferedPlayback => TimeSpan.Zero;

    public event Action<string>? ErrorOccurred;

    public void StartCapture(Action<ReadOnlyMemory<short>> onPcmSampleBlock)
    {
        IsCapturing = true;
        ErrorOccurred?.Invoke("No audio backend is available on this platform; microphone is disabled.");
    }

    public void StopCapture() => IsCapturing = false;
    public void StartPlayback() => IsPlaying = true;
    public void StopPlayback() => IsPlaying = false;
    public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm) { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
