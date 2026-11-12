using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Media;
using NodeX.Core.Audio;
using AudioFormat = NodeX.Core.Audio.AudioFormat; // Android.Media also defines AudioFormat

namespace NodeX.UI.Android.Audio;

/// <summary>
/// Android <see cref="IAudioDeviceService"/> built on AudioRecord / AudioTrack at 8 kHz mono PCM16.
/// Uses the VoiceCommunication source/usage so the OS applies echo cancellation and routes to a
/// Bluetooth SCO headset when <see cref="AndroidBackgroundKeeper.StartBluetoothSco"/> is active.
/// </summary>
public sealed class AndroidAudioDeviceService : IAudioDeviceService
{
    private readonly object _gate = new();
    private AudioRecord? _record;
    private AudioTrack? _track;
    private Thread? _captureThread;
    private volatile bool _captureRunning;
    private Action<ReadOnlyMemory<short>>? _onPcm;
    private long _samplesWritten;

    public bool IsCapturing => _captureRunning;
    public bool IsPlaying { get; private set; }
    public event Action<string>? ErrorOccurred;

    public TimeSpan BufferedPlayback
    {
        get
        {
            var track = _track;
            if (track == null) return TimeSpan.Zero;
            // The head position is a 32-bit frame counter that wraps; the helper compares modulo 2^32.
            int pending = AudioFormat.PendingPlaybackSamples(
                Interlocked.Read(ref _samplesWritten), track.PlaybackHeadPosition);
            return AudioFormat.SamplesToDuration(pending);
        }
    }

    public void StartCapture(Action<ReadOnlyMemory<short>> onPcmSampleBlock)
    {
        ArgumentNullException.ThrowIfNull(onPcmSampleBlock);
        lock (_gate)
        {
            if (_captureRunning) return;
            _onPcm = onPcmSampleBlock;

            int min = AudioRecord.GetMinBufferSize(AudioFormat.SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
            int bufBytes = Math.Max(min, AudioFormat.SamplesPerFrame * 2 * 4);
            _record = new AudioRecord(AudioSource.VoiceCommunication, AudioFormat.SampleRate,
                ChannelIn.Mono, Encoding.Pcm16bit, bufBytes);

            if (_record.State != State.Initialized)
            {
                _record.Release();
                _record = null;
                throw new InvalidOperationException(
                    "Microphone could not be opened. Is RECORD_AUDIO granted and the mic free?");
            }

            _record.StartRecording();
            _captureRunning = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "NodeX-Capture" };
            _captureThread.Start();
        }
    }

    private void CaptureLoop()
    {
        short[] buffer = new short[AudioFormat.SamplesPerFrame];
        var record = _record;
        while (_captureRunning && record != null)
        {
            int n = record.Read(buffer, 0, buffer.Length);
            if (n > 0)
            {
                _onPcm?.Invoke(buffer.AsMemory(0, n));
            }
            else if (n < 0)
            {
                ErrorOccurred?.Invoke($"AudioRecord.Read failed ({n}).");
                break;
            }
        }
    }

    public void StopCapture()
    {
        Thread? thread;
        lock (_gate)
        {
            _captureRunning = false;
            thread = _captureThread;
            _captureThread = null;
        }
        // Stop the recorder first so a blocked Read() returns, then join outside the lock.
        try { _record?.Stop(); } catch (Exception) { /* already stopped */ }
        thread?.Join(500);
        lock (_gate)
        {
            _record?.Release();
            _record = null;
            _onPcm = null;
        }
    }

    public void StartPlayback()
    {
        lock (_gate)
        {
            if (IsPlaying) return;
            AndroidAudioRoute.RouteChanged += OnRouteChanged;
            BuildTrack();
            IsPlaying = true;
        }
    }

    private bool _trackIsMedia;

    // Loudspeaker and car listening play as ordinary media so the phone never enters communication
    // mode (which turns other apps' notification sounds into a generic beep); the phone-earpiece and
    // headset routes keep the call stream.
    private static bool WantMedia => CarSession.Active || AndroidAudioRoute.SpeakerActive;

    private void BuildTrack()
    {
        int min = AudioTrack.GetMinBufferSize(AudioFormat.SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
        int bufBytes = Math.Max(min, AudioFormat.SamplesPerFrame * 2 * 6);

        _trackIsMedia = WantMedia;
        var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(_trackIsMedia ? AudioUsageKind.Media : AudioUsageKind.VoiceCommunication)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!)!
            .SetAudioFormat(new global::Android.Media.AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(AudioFormat.SampleRate)!
                .SetChannelMask(ChannelOut.Mono)!
                .Build()!)!
            .SetBufferSizeInBytes(bufBytes)
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();

        Interlocked.Exchange(ref _samplesWritten, 0);
        track.Play();
        _track = track;
    }

    private void OnRouteChanged()
    {
        lock (_gate)
        {
            if (!IsPlaying || _trackIsMedia == WantMedia) return;
            var old = _track;
            _track = null; // EnqueuePlaybackPcm drops frames for the instant the stream is swapped
            if (old != null)
            {
                try { old.Stop(); } catch (Exception) { /* already stopped */ }
                old.Release();
            }
            BuildTrack();
        }
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            AndroidAudioRoute.RouteChanged -= OnRouteChanged;
            if (_track == null) return;
            try { _track.Stop(); } catch (Exception) { /* already stopped */ }
            _track.Release();
            _track = null;
            IsPlaying = false;
        }
    }

    public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm)
    {
        var track = _track;
        if (track == null || pcm.IsEmpty) return;

        int written;
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(pcm, out var segment) && segment.Array != null)
        {
            written = track.Write(segment.Array, segment.Offset, segment.Count, WriteMode.NonBlocking);
        }
        else
        {
            short[] data = pcm.ToArray();
            written = track.Write(data, 0, data.Length, WriteMode.NonBlocking);
        }
        if (written > 0) Interlocked.Add(ref _samplesWritten, written);
        else if (written < 0) ErrorOccurred?.Invoke($"AudioTrack.Write failed ({written}).");
    }

    public ValueTask DisposeAsync()
    {
        StopCapture();
        StopPlayback();
        return ValueTask.CompletedTask;
    }
}
