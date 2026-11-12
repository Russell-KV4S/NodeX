using System.Diagnostics;
using System.Threading.Channels;

namespace NodeX.Core.Audio;

/// <summary>Tuning for <see cref="NodeAudioEngine"/>.</summary>
public sealed record NodeAudioEngineOptions
{
    /// <summary>Jitter buffer prime depth in 20 ms frames.</summary>
    public int JitterTargetDepthFrames { get; init; } = 3;

    /// <summary>Jitter buffer capacity in 20 ms frames.</summary>
    public int JitterCapacityFrames { get; init; } = 25;

    /// <summary>
    /// When true, zero-amplitude PCM is written to the device during receive silence so mobile OSes
    /// (notably iOS) keep the audio session, sockets and BLE loop alive in the background.
    /// </summary>
    public bool KeepAudioPipelineAlive { get; init; }

    /// <summary>
    /// Battery/privacy saver for always-connected use: the microphone is opened when PTT keys and closed
    /// <see cref="MicHoldOff"/> after it releases, instead of staying open for the whole connection.
    /// The input meter is idle while the mic is closed.
    /// </summary>
    public bool OpenMicOnlyWhileKeyed { get; init; }

    /// <summary>How long the microphone stays open after PTT releases (covers quick re-keys).</summary>
    public TimeSpan MicHoldOff { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Device output backlog above which new frames are dropped to bound latency.</summary>
    public TimeSpan MaxDeviceBacklog { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Software gain applied to the microphone before encoding (1.0 = unity).</summary>
    public float MicGain { get; init; } = 1.0f;

    /// <summary>
    /// Clean up transmit audio (high-pass, automatic gain, soft limiter; see <see cref="MicProcessor"/>) after
    /// <see cref="MicGain"/> and before encoding. Off by default here; the app turns it on from settings.
    /// </summary>
    public bool MicProcessing { get; init; }
}

/// <summary>
/// Glues a platform <see cref="IAudioDeviceService"/> to an <see cref="IVoiceChannel"/>:
/// capture → 20 ms framing → µ-law → network while keyed, and network → µ-law decode →
/// jitter buffer → 20 ms playout clock → device. Also publishes input/output levels for VU meters.
/// </summary>
public sealed class NodeAudioEngine : IAsyncDisposable
{
    private readonly IVoiceChannel _channel;
    private readonly IAudioDeviceService _device;
    private readonly NodeAudioEngineOptions _options;
    private readonly PcmFramer _framer = new();
    private readonly MicProcessor? _micProcessor;
    private readonly AudioLevelMeter _inputMeter = new();
    private readonly AudioLevelMeter _outputMeter = new();
    private readonly Channel<byte[]> _sendQueue;
    private readonly short[] _decodeScratch = new short[AudioFormat.SamplesPerFrame];
    private readonly Lock _decodeLock = new();

    // Start and Stop exclude each other. A call that is hung up straight after it is accepted starts its audio
    // (on a worker thread) and is disposed at almost the same moment; without this the two interleaved, which
    // surfaced as "Audio could not start: Object reference not set..." and could start a device after it was stopped.
    private readonly Lock _lifecycle = new();
    private bool _disposed;
    private volatile CancellationTokenSource? _cts;
    private Task? _playoutTask;
    private Task? _sendTask;
    private bool _remoteAudioActive;
    private readonly Lock _micLock = new();
    private CancellationTokenSource? _micCloseCts;
    private bool _micOpen;

    public NodeAudioEngine(IVoiceChannel channel, IAudioDeviceService device, NodeAudioEngineOptions? options = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _options = options ?? new NodeAudioEngineOptions();
        if (_options.MicProcessing) _micProcessor = new MicProcessor();
        JitterBuffer = new JitterBuffer(_options.JitterTargetDepthFrames, _options.JitterCapacityFrames);

        // Small bounded queue between the audio thread and the network; drop oldest under backpressure
        // so a stalled socket never blocks the capture callback.
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public JitterBuffer JitterBuffer { get; }
    public bool IsRunning => _cts != null;

    /// <summary>
    /// When true, received audio is still decoded, metered and reported (<see cref="RemoteAudioActiveChanged"/>) but
    /// not played: silence goes to the device instead (only when <see cref="NodeAudioEngineOptions.KeepAudioPipelineAlive"/>
    /// is on). Used to silence one link while several are connected.
    /// </summary>
    public bool Muted
    {
        get => Volatile.Read(ref _muted);
        set => Volatile.Write(ref _muted, value);
    }

    private bool _muted;

    /// <summary>Smoothed microphone level (updated every 20 ms while capturing).</summary>
    public AudioLevels InputLevel => _inputMeter.Current;

    /// <summary>Smoothed received-audio level (updated every 20 ms of playout).</summary>
    public AudioLevels OutputLevel => _outputMeter.Current;

    /// <summary>Raised on the capture thread every 20 ms with the smoothed mic level.</summary>
    public event Action<AudioLevels>? InputLevelChanged;

    /// <summary>Raised on the playout thread every 20 ms with the smoothed received level.</summary>
    public event Action<AudioLevels>? OutputLevelChanged;

    /// <summary>Raised when received audio starts (true) or the jitter buffer drains (false).</summary>
    public event Action<bool>? RemoteAudioActiveChanged;

    public event Action<string>? ErrorOccurred;

    /// <summary>Starts playback (and capture, per the options). Does nothing once running or after disposal.</summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed || _cts != null) return;
            var cts = new CancellationTokenSource();
            _cts = cts;

            _framer.Reset();
            _micProcessor?.Reset();
            JitterBuffer.Reset();
            _inputMeter.Reset();
            _outputMeter.Reset();

            _channel.VoiceFrameReceived += OnVoiceFrameReceived;
            _device.ErrorOccurred += OnDeviceError;
            _device.StartPlayback();
            if (_options.OpenMicOnlyWhileKeyed)
            {
                _channel.TransmittingChanged += OnTransmittingChanged;
                if (_channel.IsTransmitting) OpenMic();
            }
            else
            {
                _device.StartCapture(OnCaptureBlock);
                _micOpen = true;
            }

            var token = cts.Token;
            _sendTask = Task.Run(() => SendLoopAsync(token));
            _playoutTask = Task.Run(() => PlayoutLoopAsync(token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? playout, send;
        string? stopError = null;

        lock (_lifecycle)
        {
            cts = _cts;
            if (cts == null) return;
            _cts = null;

            _channel.VoiceFrameReceived -= OnVoiceFrameReceived;
            _channel.TransmittingChanged -= OnTransmittingChanged;
            _device.ErrorOccurred -= OnDeviceError;
            cts.Cancel();

            CloseMic();
            try { _device.StopPlayback(); } catch (Exception ex) { stopError = ex.Message; }

            // A start that failed part-way (the device refused to open) leaves these unset.
            playout = _playoutTask;
            send = _sendTask;
            _playoutTask = null;
            _sendTask = null;
        }

        if (stopError != null) OnDeviceError(stopError);
        if (playout != null) await playout.ConfigureAwait(false);
        if (send != null) await send.ConfigureAwait(false);
        cts.Dispose();

        SetRemoteAudioActive(false);
        _inputMeter.Reset();
        _outputMeter.Reset();
        InputLevelChanged?.Invoke(AudioLevels.Silent);
        OutputLevelChanged?.Invoke(AudioLevels.Silent);
    }

    // ----- Mic gating (OpenMicOnlyWhileKeyed) -----

    private void OnTransmittingChanged(bool transmitting)
    {
        if (_cts == null) return;
        if (transmitting)
        {
            OpenMic();
            return;
        }

        CancellationTokenSource close;
        lock (_micLock)
        {
            _micCloseCts?.Cancel();
            _micCloseCts?.Dispose();
            close = _micCloseCts = new CancellationTokenSource();
        }
        _ = CloseMicAfterHoldOffAsync(close);
    }

    private async Task CloseMicAfterHoldOffAsync(CancellationTokenSource close)
    {
        try
        {
            await Task.Delay(_options.MicHoldOff, close.Token).ConfigureAwait(false);
            if (!_channel.IsTransmitting) CloseMic();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void OpenMic()
    {
        lock (_micLock)
        {
            _micCloseCts?.Cancel();
            if (_micOpen) return;
            try
            {
                _framer.Reset();
                _device.StartCapture(OnCaptureBlock);
                _micOpen = true;
            }
            catch (Exception ex) { OnDeviceError($"Microphone could not open: {ex.Message}"); }
        }
    }

    private void CloseMic()
    {
        lock (_micLock)
        {
            _micCloseCts?.Cancel();
            if (!_micOpen && !_device.IsCapturing) return;
            _micOpen = false;
            try { _device.StopCapture(); } catch (Exception ex) { OnDeviceError(ex.Message); }
        }
        _inputMeter.Reset();
        InputLevelChanged?.Invoke(AudioLevels.Silent);
    }

    // ----- TX path (device audio thread) -----

    private void OnCaptureBlock(ReadOnlyMemory<short> block)
    {
        _framer.Push(block.Span, OnCapturedFrame);
    }

    private void OnCapturedFrame(ReadOnlySpan<short> frame)
    {
        Span<short> scaled = stackalloc short[frame.Length];
        float gain = _options.MicGain;
        if (gain == 1.0f)
        {
            frame.CopyTo(scaled);
        }
        else
        {
            for (int i = 0; i < frame.Length; i++)
                scaled[i] = (short)Math.Clamp(frame[i] * gain, short.MinValue, short.MaxValue);
        }

        _micProcessor?.Process(scaled);

        InputLevelChanged?.Invoke(_inputMeter.Process(scaled));

        if (!_channel.IsTransmitting) return;

        byte[] ulaw = new byte[scaled.Length];
        G711Ulaw.Encode(scaled, ulaw);
        _sendQueue.Writer.TryWrite(ulaw);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (byte[] frame in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _channel.SendAudioFrameAsync(frame, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke($"Audio send failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ----- RX path -----

    private void OnVoiceFrameReceived(ushort timestamp, ReadOnlyMemory<byte> ulaw)
    {
        lock (_decodeLock)
        {
            int n = G711Ulaw.Decode(ulaw.Span, _decodeScratch);
            JitterBuffer.Push(timestamp, _decodeScratch.AsSpan(0, n));
        }
    }

    private async Task PlayoutLoopAsync(CancellationToken ct)
    {
        short[] frame = new short[AudioFormat.SamplesPerFrame];
        var clock = Stopwatch.StartNew();
        long framesDue = 0;

        // Tick faster than 20 ms and catch up on however many frames are due; this keeps the
        // average cadence exact even with coarse OS timer resolution (~15.6 ms on Windows).
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long target = clock.ElapsedMilliseconds / AudioFormat.FrameMilliseconds;

                // After a long stall (debugger, suspend) don't burst-play stale time.
                if (target - framesDue > 10) framesDue = target - 1;

                while (framesDue < target)
                {
                    framesDue++;
                    PlayOneFrame(frame);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Playout loop failed: {ex.Message}");
        }
    }

    /// <summary>Pulls one frame from the jitter buffer and writes it to the device. Internal for tests.</summary>
    internal void PlayOneFrame(short[] frame)
    {
        var result = JitterBuffer.Pop(frame);
        bool hasAudio = result != JitterPopResult.Silence;

        if (hasAudio) SetRemoteAudioActive(true);
        else if (JitterBuffer.IsPriming) SetRemoteAudioActive(false);

        OutputLevelChanged?.Invoke(_outputMeter.Process(frame));

        if (Muted)
        {
            // Heard-but-silenced: the meter above still moves so the user can see the link is active.
            if (!_options.KeepAudioPipelineAlive) return;
            Array.Clear(frame);
        }

        if (!hasAudio && !_options.KeepAudioPipelineAlive) return;
        if (_device.BufferedPlayback > _options.MaxDeviceBacklog) return; // device clock slower than ours

        try
        {
            _device.EnqueuePlaybackPcm(frame);
        }
        catch (Exception ex)
        {
            OnDeviceError(ex.Message);
        }
    }

    private void SetRemoteAudioActive(bool active)
    {
        if (_remoteAudioActive == active) return;
        _remoteAudioActive = active;
        RemoteAudioActiveChanged?.Invoke(active);
    }

    private void OnDeviceError(string message) => ErrorOccurred?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycle) _disposed = true;      // a start still on its way to run becomes a no-op
        await StopAsync().ConfigureAwait(false);
        _sendQueue.Writer.TryComplete();
    }
}
