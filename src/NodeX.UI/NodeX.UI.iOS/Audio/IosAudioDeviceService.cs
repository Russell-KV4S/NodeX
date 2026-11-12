using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AVFoundation;
using Foundation;
using NodeX.Core.Audio;

namespace NodeX.UI.iOS.Audio;

/// <summary>
/// iOS <see cref="IAudioDeviceService"/> on AVAudioEngine. Capture taps the input node at the hardware
/// rate and resamples to 8 kHz PCM16; playback schedules 8 kHz float buffers on an AVAudioPlayerNode
/// and lets the engine resample to the output rate.
/// </summary>
/// <remarks>
/// Pair with <see cref="NodeAudioEngineOptions.KeepAudioPipelineAlive"/> = true (see
/// <see cref="RecommendedEngineOptions"/>): the engine then writes zero-amplitude PCM during receive
/// silence so iOS keeps the audio session, UDP socket and BLE loop running in the background.
/// </remarks>
public sealed class IosAudioDeviceService : IAudioDeviceService
{
    private readonly IosAudioSessionManager _session;
    private readonly object _gate = new();
    private readonly AVAudioFormat _playbackFormat =
        new(AVAudioCommonFormat.PCMFloat32, AudioFormat.SampleRate, AudioFormat.Channels, false);

    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _player;
    private PcmResampler? _resampler;
    private Action<ReadOnlyMemory<short>>? _onPcm;
    private bool _captureWanted;
    private bool _playbackWanted;
    private long _pendingSamples;
    private float[] _tapInputScratch = [];
    private short[] _tapOutputScratch = [];
    private float[] _playbackFloatScratch = new float[AudioFormat.SamplesPerFrame];

    public IosAudioDeviceService(IosAudioSessionManager session)
    {
        _session = session;
        _session.InterruptionEnded += OnInterruptionEnded;
        _session.ErrorOccurred += m => ErrorOccurred?.Invoke(m);
    }

    /// <summary>Engine options the iOS head should use (zero-PCM keeper enabled).</summary>
    public static NodeAudioEngineOptions RecommendedEngineOptions { get; } =
        new() { KeepAudioPipelineAlive = true, OpenMicOnlyWhileKeyed = true };

    public bool IsCapturing { get; private set; }
    public bool IsPlaying { get; private set; }
    public event Action<string>? ErrorOccurred;

    public TimeSpan BufferedPlayback =>
        AudioFormat.SamplesToDuration((int)Math.Max(0, Interlocked.Read(ref _pendingSamples)));

    public void StartCapture(Action<ReadOnlyMemory<short>> onPcmSampleBlock)
    {
        ArgumentNullException.ThrowIfNull(onPcmSampleBlock);
        lock (_gate)
        {
            _onPcm = onPcmSampleBlock;
            _captureWanted = true;
            StartEngineLocked();
        }
    }

    public void StopCapture()
    {
        lock (_gate)
        {
            _captureWanted = false;
            IsCapturing = false;
            _engine?.InputNode.RemoveTapOnBus(0);
            _onPcm = null;
            StopEngineIfIdleLocked();
        }
    }

    public void StartPlayback()
    {
        lock (_gate)
        {
            _playbackWanted = true;
            StartEngineLocked();
        }
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            _playbackWanted = false;
            IsPlaying = false;
            _player?.Stop();
            Interlocked.Exchange(ref _pendingSamples, 0);
            StopEngineIfIdleLocked();
        }
    }

    public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm)
    {
        var player = _player;
        if (player == null || pcm.IsEmpty || !IsPlaying) return;

        int n = pcm.Length;
        var buffer = new AVAudioPcmBuffer(_playbackFormat, (uint)n) { FrameLength = (uint)n };

        if (_playbackFloatScratch.Length < n) _playbackFloatScratch = new float[n];
        var span = pcm.Span;
        for (int i = 0; i < n; i++) _playbackFloatScratch[i] = span[i] / 32768f;

        IntPtr channel0 = Marshal.ReadIntPtr(buffer.FloatChannelData);
        Marshal.Copy(_playbackFloatScratch, 0, channel0, n);

        Interlocked.Add(ref _pendingSamples, n);
        player.ScheduleBuffer(buffer, () => Interlocked.Add(ref _pendingSamples, -n));
    }

    // ----- engine lifecycle -----

    private void StartEngineLocked()
    {
        if (_engine is { Running: true })
        {
            ConfigureLocked();
            return;
        }

        if (!_session.Activate()) return;

        _engine = new AVAudioEngine();
        _player = new AVAudioPlayerNode();
        _engine.AttachNode(_player);
        _engine.Connect(_player, _engine.MainMixerNode, _playbackFormat);

        ConfigureLocked();

        _engine.Prepare();
        if (!_engine.StartAndReturnError(out NSError? error))
        {
            ErrorOccurred?.Invoke($"AVAudioEngine failed to start: {error?.LocalizedDescription}");
            return;
        }

        if (_playbackWanted)
        {
            _player.Play();
            IsPlaying = true;
        }
    }

    private void ConfigureLocked()
    {
        if (_engine == null) return;

        if (_captureWanted && !IsCapturing)
        {
            var input = _engine.InputNode;
            var hwFormat = input.GetBusOutputFormat(0);
            _resampler = new PcmResampler((int)hwFormat.SampleRate);

            // The tap runs on a real-time audio thread: allocate as little as possible.
            input.InstallTapOnBus(0, 1024, hwFormat, OnTap);
            IsCapturing = true;
        }

        if (_playbackWanted && _engine.Running && _player is { Playing: false })
        {
            _player.Play();
            IsPlaying = true;
        }
    }

    private void OnTap(AVAudioPcmBuffer buffer, AVAudioTime when)
    {
        var cb = _onPcm;
        var resampler = _resampler;
        if (cb == null || resampler == null) return;

        int frames = (int)buffer.FrameLength;
        if (frames == 0 || buffer.FloatChannelData == IntPtr.Zero) return;

        if (_tapInputScratch.Length < frames) _tapInputScratch = new float[frames];
        Marshal.Copy(Marshal.ReadIntPtr(buffer.FloatChannelData), _tapInputScratch, 0, frames); // channel 0

        int maxOut = resampler.MaxOutputSamples(frames);
        if (_tapOutputScratch.Length < maxOut) _tapOutputScratch = new short[maxOut];
        int n = resampler.Process(_tapInputScratch.AsSpan(0, frames), _tapOutputScratch);
        if (n > 0) cb(_tapOutputScratch.AsMemory(0, n));
    }

    private void StopEngineIfIdleLocked()
    {
        if (_captureWanted || _playbackWanted || _engine == null) return;

        _player?.Stop();
        _engine.Stop();
        _player?.Dispose();
        _engine.Dispose();
        _player = null;
        _engine = null;
        _resampler = null;
        IsCapturing = IsPlaying = false;
        _session.Deactivate();
    }

    private void OnInterruptionEnded()
    {
        lock (_gate)
        {
            if (!_captureWanted && !_playbackWanted) return;

            // Tear down and rebuild: taps and nodes are invalid after a media-services reset.
            _engine?.InputNode.RemoveTapOnBus(0);
            _engine?.Stop();
            _engine?.Dispose();
            _player?.Dispose();
            _engine = null;
            _player = null;
            IsCapturing = IsPlaying = false;
            Interlocked.Exchange(ref _pendingSamples, 0);
            StartEngineLocked();
        }
    }

    public ValueTask DisposeAsync()
    {
        _session.InterruptionEnded -= OnInterruptionEnded;
        lock (_gate)
        {
            _captureWanted = _playbackWanted = false;
            _engine?.InputNode.RemoveTapOnBus(0);
            StopEngineIfIdleLocked();
        }
        return ValueTask.CompletedTask;
    }
}
