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

    // Guards _track and _samplesWritten between the playout thread (Write, head position) and a route change or
    // stop (Stop/Release): a released AudioTrack throws IllegalStateException, and a late Interlocked.Add after a
    // rebuild corrupted the new track's backlog (2026-11-21 review). Order: _gate, then _trackLock.
    private readonly object _trackLock = new();
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
            lock (_trackLock)
            {
                var track = _track;
                if (track == null) return TimeSpan.Zero;
                // The head position is a 32-bit frame counter that wraps; the helper compares modulo 2^32.
                int pending = AudioFormat.PendingPlaybackSamples(
                    Interlocked.Read(ref _samplesWritten), track.PlaybackHeadPosition);
                return AudioFormat.SamplesToDuration(pending);
            }
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
            CarSession.Changed += OnRouteChanged;
            CarSession.CarAudioChanged += OnRouteChanged;
            BuildTrack();
            IsPlaying = true;
        }
    }

    private NodeX.Core.PlaybackUsage _trackUsage;

    // Loudspeaker and car listening play as ordinary media so the phone never enters communication
    // mode (which turns other apps' notification sounds into a generic beep); the phone-earpiece and
    // headset routes keep the call stream. With "hear nodes over the car radio" on and the car playing
    // something else, node audio uses the navigation stream so the car mixes it over the radio.
    private static NodeX.Core.PlaybackUsage WantUsage =>
        NodeX.Core.CarRadioMix.Choose(CarSession.Active, AndroidAudioRoute.SpeakerActive, CarSession.RadioMixApplies);

    private static AudioUsageKind ToAndroid(NodeX.Core.PlaybackUsage u) => u switch
    {
        NodeX.Core.PlaybackUsage.Media => AudioUsageKind.Media,
        NodeX.Core.PlaybackUsage.NavigationGuidance => AudioUsageKind.AssistanceNavigationGuidance,
        _ => AudioUsageKind.VoiceCommunication
    };

    private void BuildTrack()
    {
        int min = AudioTrack.GetMinBufferSize(AudioFormat.SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
        int bufBytes = Math.Max(min, AudioFormat.SamplesPerFrame * 2 * 6);

        _trackUsage = WantUsage;
        var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(ToAndroid(_trackUsage))!
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

        track.Play();
        lock (_trackLock)
        {
            Interlocked.Exchange(ref _samplesWritten, 0);
            _track = track;
        }
    }

    private void OnRouteChanged()
    {
        lock (_gate)
        {
            if (!IsPlaying || _trackUsage == WantUsage) return;
            lock (_trackLock)
            {
                var old = _track;
                _track = null; // EnqueuePlaybackPcm drops frames for the instant the stream is swapped
                if (old != null)
                {
                    try { old.Stop(); } catch (Exception) { /* already stopped */ }
                    old.Release();
                }
            }
            BuildTrack();
        }
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            AndroidAudioRoute.RouteChanged -= OnRouteChanged;
            CarSession.Changed -= OnRouteChanged;
            CarSession.CarAudioChanged -= OnRouteChanged;
            lock (_trackLock)
            {
                if (_track == null) return;
                try { _track.Stop(); } catch (Exception) { /* already stopped */ }
                _track.Release();
                _track = null;
            }
            IsPlaying = false;
        }
    }

    public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm)
    {
        if (pcm.IsEmpty) return;

        int written;
        lock (_trackLock)   // non-blocking write: held only for the copy into the track's buffer
        {
            var track = _track;
            if (track == null) return;
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
        }
        if (written < 0) ErrorOccurred?.Invoke($"AudioTrack.Write failed ({written}).");
    }

    public ValueTask DisposeAsync()
    {
        StopCapture();
        StopPlayback();
        return ValueTask.CompletedTask;
    }
}
