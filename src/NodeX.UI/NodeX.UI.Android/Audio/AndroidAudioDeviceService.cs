using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Media;
using Android.Media.Audiofx;
using NodeX.Core.Audio;
using NodeX.Core.Diagnostics;
using AudioFormat = NodeX.Core.Audio.AudioFormat; // Android.Media also defines AudioFormat

namespace NodeX.UI.Android.Audio;

/// <summary>
/// Android <see cref="IAudioDeviceService"/> built on AudioRecord / AudioTrack at 8 kHz mono PCM16.
/// Uses lazy AudioTrack allocation with idle release so up to 8 simultaneous links never hold idle tracks
/// against Android AudioFlinger's client limit, and monitors playback head progress to rebuild stalled tracks.
/// </summary>
public sealed class AndroidAudioDeviceService : IAudioDeviceService
{
    private const int IdleReleaseMs = 15_000;
    private const int IdleCheckPeriodMs = 5_000;

    private static int _loudnessTargetMb;
    private static event Action? LoudnessBoostChanged;

    /// <summary>
    /// Updates Android's native <see cref="LoudnessEnhancer"/> target boost for active and future tracks.
    /// Safe to call from any thread.
    /// </summary>
    public static void SetLoudnessBoost(float rxGain, bool enabled)
    {
        int targetMb = RxAudioProcessor.ComputeAndroidLoudnessMillibels(rxGain, enabled);
        if (Interlocked.Exchange(ref _loudnessTargetMb, targetMb) != targetMb)
            LoudnessBoostChanged?.Invoke();
    }

    // Lock ordering invariant: ALWAYS acquire _gate before _trackLock; never acquire _gate while holding _trackLock.
    private readonly object _gate = new();
    private readonly object _trackLock = new();
    private readonly PlaybackStallDetector _stallDetector = new();

    private AudioRecord? _record;
    private AudioTrack? _track;
    private LoudnessEnhancer? _loudnessEnhancer;
    private Timer? _idleTimer;
    private Thread? _captureThread;
    private volatile bool _captureRunning;
    private Action<ReadOnlyMemory<short>>? _onPcm;
    private long _samplesWritten;
    private long _lastWriteTicks;
    private int _rebuildInFlight;
    private NodeX.Core.PlaybackUsage _trackUsage;

    public bool IsCapturing => _captureRunning;
    public bool IsPlaying { get; private set; }
    public event Action<string>? ErrorOccurred;

    public TimeSpan BufferedPlayback
    {
        get
        {
            string? rebuildReason = null;
            TimeSpan result = TimeSpan.Zero;
            lock (_trackLock)
            {
                var track = _track;
                if (track == null) return TimeSpan.Zero;

                long written = Interlocked.Read(ref _samplesWritten);
                int head = SafeHeadPosition(track);
                int pending = AudioFormat.PendingPlaybackSamples(written, head);
                var decision = _stallDetector.ObserveProgress(written, head, Environment.TickCount64);
                if (decision.ShouldRebuild)
                    rebuildReason = decision.Reason ?? "stalled playback head";
                else
                    result = AudioFormat.SamplesToDuration(pending);
            }

            if (rebuildReason != null) TriggerAsyncRebuild(rebuildReason);
            return result;
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
            LoudnessBoostChanged += OnLoudnessBoostChanged;
            IsPlaying = true;
        }
    }

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

    /// <summary>Builds and starts a fresh AudioTrack; caller must hold <see cref="_gate"/>.</summary>
    private void BuildTrack(string reason)
    {
        int min = AudioTrack.GetMinBufferSize(AudioFormat.SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
        int bufBytes = Math.Max(min, AudioFormat.SamplesPerFrame * 2 * 6);

        var usage = WantUsage;
        var androidUsage = ToAndroid(usage);

        AudioTrack? track = TryCreateTrack(androidUsage, bufBytes, AudioTrackPerformanceMode.LowLatency)
                            ?? TryCreateTrack(androidUsage, bufBytes, AudioTrackPerformanceMode.None);

        if (track == null || track.State != AudioTrackState.Initialized)
        {
            track?.Release();
            DiagnosticLog.Default.Info("audio", $"AudioTrack failed to initialize ({reason}): usage={usage}, min={min}, bufBytes={bufBytes}");
            throw new InvalidOperationException("AudioTrack failed to initialize.");
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            try
            {
                int bufFrames = track.BufferSizeInFrames;
                if (bufFrames > 0)
                    track.SetStartThresholdInFrames(Math.Min(bufFrames, AudioFormat.SamplesPerFrame * 2));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Default.Detail("audio", $"SetStartThresholdInFrames skipped: {ex.Message}");
            }
        }

        track.Play();
        _trackUsage = usage;

        lock (_trackLock)
        {
            Interlocked.Exchange(ref _samplesWritten, 0);
            _stallDetector.Reset();
            _track = track;
            Volatile.Write(ref _lastWriteTicks, Environment.TickCount64);
            ApplyLoudnessEnhancerLocked(track);
        }

        _idleTimer ??= new Timer(OnIdleTick, null, IdleCheckPeriodMs, IdleCheckPeriodMs);
        DiagnosticLog.Default.Info("audio",
            $"AudioTrack built ({reason}): usage={usage}, perf={track.PerformanceMode}, bufBytes={bufBytes}, bufFrames={track.BufferSizeInFrames}, playState={track.PlayState}");
    }

    private static AudioTrack? TryCreateTrack(AudioUsageKind usage, int bufBytes, AudioTrackPerformanceMode perfMode)
    {
        AudioTrack? track = null;
        try
        {
            track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(usage)!
                    .SetContentType(AudioContentType.Speech)!
                    .Build()!)!
                .SetAudioFormat(new global::Android.Media.AudioFormat.Builder()
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetSampleRate(AudioFormat.SampleRate)!
                    .SetChannelMask(ChannelOut.Mono)!
                    .Build()!)!
                .SetBufferSizeInBytes(bufBytes)
                .SetTransferMode(AudioTrackMode.Stream)
                .SetPerformanceMode(perfMode)
                .Build();

            if (track.State == AudioTrackState.Initialized) return track;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Default.Detail("audio", $"AudioTrack.Build({perfMode}) failed: {ex.Message}");
        }

        try { track?.Release(); } catch (Exception) { /* best effort */ }
        return null;
    }

    private void OnRouteChanged()
    {
        lock (_gate)
        {
            if (!IsPlaying) return;
            bool hasTrack;
            lock (_trackLock) hasTrack = _track != null;
            if (!hasTrack || _trackUsage == WantUsage) return;

            ReleaseTrackLocked();
            BuildTrack("route-changed");
        }
    }

    private void OnIdleTick(object? _)
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (!IsPlaying) return;
            long idleMs = Environment.TickCount64 - Volatile.Read(ref _lastWriteTicks);
            if (idleMs < IdleReleaseMs) return;

            bool released = ReleaseTrackLocked();
            _idleTimer?.Dispose();
            _idleTimer = null;
            if (released)
                DiagnosticLog.Default.Detail("audio", $"AudioTrack released after {idleMs} ms idle silence");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Stops and releases any active <see cref="_track"/>; caller must hold <see cref="_gate"/>.</summary>
    private bool ReleaseTrackLocked()
    {
        AudioTrack? old;
        lock (_trackLock)
        {
            ReleaseLoudnessEnhancerLocked();
            old = _track;
            _track = null;
            Interlocked.Exchange(ref _samplesWritten, 0);
            _stallDetector.Reset();
        }
        if (old == null) return false;
        try { old.Stop(); } catch (Exception) { /* already stopped */ }
        try { old.Release(); } catch (Exception) { /* already released */ }
        return true;
    }

    private void OnLoudnessBoostChanged()
    {
        lock (_trackLock)
        {
            if (_track != null) ApplyLoudnessEnhancerLocked(_track);
        }
    }

    private void ApplyLoudnessEnhancerLocked(AudioTrack track)
    {
        int targetMb = Volatile.Read(ref _loudnessTargetMb);
        if (targetMb <= 0)
        {
            ReleaseLoudnessEnhancerLocked();
            return;
        }

        try
        {
            int sessionId = track.AudioSessionId;
            if (sessionId <= 0) return;

            _loudnessEnhancer ??= new LoudnessEnhancer(sessionId);
            _loudnessEnhancer.SetTargetGain(targetMb);
            _loudnessEnhancer.SetEnabled(true);
            DiagnosticLog.Default.Detail("audio", $"LoudnessEnhancer active: +{targetMb} mB (session={sessionId})");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Default.Detail("audio", $"LoudnessEnhancer unavailable: {ex.Message}");
            ReleaseLoudnessEnhancerLocked();
        }
    }

    private void ReleaseLoudnessEnhancerLocked()
    {
        var enhancer = _loudnessEnhancer;
        _loudnessEnhancer = null;
        if (enhancer == null) return;
        try { enhancer.SetEnabled(false); } catch (Exception) { /* best effort */ }
        try { enhancer.Release(); } catch (Exception) { /* best effort */ }
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            IsPlaying = false;
            AndroidAudioRoute.RouteChanged -= OnRouteChanged;
            CarSession.Changed -= OnRouteChanged;
            CarSession.CarAudioChanged -= OnRouteChanged;
            LoudnessBoostChanged -= OnLoudnessBoostChanged;
            _idleTimer?.Dispose();
            _idleTimer = null;
            ReleaseTrackLocked();
        }
    }

    public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm)
    {
        if (pcm.IsEmpty || !IsPlaying) return;

        bool needBuild;
        lock (_trackLock) needBuild = _track == null;
        if (needBuild)
        {
            lock (_gate)
            {
                if (!IsPlaying) return;
                bool stillNull;
                lock (_trackLock) stillNull = _track == null;
                if (stillNull) BuildTrack("first-audio");
            }
        }

        int written = 0;
        bool restartedPlay = false;
        PlaybackStallDecision decision = PlaybackStallDecision.None;
        bool trackMissing = false;

        lock (_trackLock) // non-blocking write: held only for the copy into the track's buffer
        {
            var track = _track;
            if (track == null)
            {
                trackMissing = true;
            }
            else
            {
                if (track.PlayState != PlayState.Playing)
                {
                    try
                    {
                        track.Play();
                        restartedPlay = true;
                    }
                    catch (Exception) { /* handled if Write fails */ }
                }

                if (MemoryMarshal.TryGetArray(pcm, out var segment) && segment.Array != null)
                {
                    written = track.Write(segment.Array, segment.Offset, segment.Count, WriteMode.NonBlocking);
                }
                else
                {
                    short[] data = pcm.ToArray();
                    written = track.Write(data, 0, data.Length, WriteMode.NonBlocking);
                }

                long now = Environment.TickCount64;
                if (written > 0)
                {
                    Interlocked.Add(ref _samplesWritten, written);
                    Volatile.Write(ref _lastWriteTicks, now);
                }

                decision = _stallDetector.ObserveWrite(
                    written,
                    Interlocked.Read(ref _samplesWritten),
                    SafeHeadPosition(track),
                    now);
            }
        }

        if (trackMissing) return;
        if (restartedPlay)
            DiagnosticLog.Default.Info("audio", "AudioTrack PlayState was not Playing; called Play() again");
        if (decision.ShouldRebuild)
            TriggerAsyncRebuild(decision.Reason ?? "write-error");
    }

    private void TriggerAsyncRebuild(string reason)
    {
        if (!IsPlaying || Interlocked.CompareExchange(ref _rebuildInFlight, 1, 0) != 0) return;
        DiagnosticLog.Default.Info("audio", $"AudioTrack stall/error ({reason}); rebuilding track");
        _ = Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!IsPlaying) return;
                    ReleaseTrackLocked();
                    BuildTrack($"recover:{reason}");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Default.Info("audio", $"AudioTrack rebuild failed: {ex.Message}");
                ErrorOccurred?.Invoke($"Audio output failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _rebuildInFlight, 0);
            }
        });
    }

    private static int SafeHeadPosition(AudioTrack track)
    {
        try { return track.PlaybackHeadPosition; }
        catch (Exception) { return 0; }
    }

    public ValueTask DisposeAsync()
    {
        StopCapture();
        StopPlayback();
        return ValueTask.CompletedTask;
    }
}
