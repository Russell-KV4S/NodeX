using System;
using System.Threading.Tasks;
using NAudio.Wave;
using NodeX.Core.Audio;

namespace NodeX.UI.Desktop.Audio;

/// <summary>
/// Windows (WASAPI via NAudio) implementation of <see cref="IAudioDeviceService"/>.
/// Captures and plays 8 kHz / 16-bit / mono PCM; Windows resamples to the device mix format.
/// </summary>
public sealed class NAudioDeviceService : IAudioDeviceService
{
    private static readonly WaveFormat Format =
        new(AudioFormat.SampleRate, AudioFormat.BitsPerSample, AudioFormat.Channels);

    private readonly int _inputDeviceNumber;
    private readonly int _outputDeviceNumber;
    private readonly object _gate = new();

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _buffer;
    private Action<ReadOnlyMemory<short>>? _onPcm;
    private short[] _captureScratch = new short[AudioFormat.SamplesPerFrame];
    private byte[] _playbackBytesScratch = new byte[AudioFormat.SamplesPerFrame * 2];

    /// <param name="inputDeviceNumber">WaveIn device index, -1 for the system default.</param>
    /// <param name="outputDeviceNumber">WaveOut device index, -1 for the system default.</param>
    public NAudioDeviceService(int inputDeviceNumber = -1, int outputDeviceNumber = -1)
    {
        _inputDeviceNumber = inputDeviceNumber;
        _outputDeviceNumber = outputDeviceNumber;
    }

    public bool IsCapturing { get; private set; }
    public bool IsPlaying { get; private set; }

    public TimeSpan BufferedPlayback
    {
        get
        {
            var b = _buffer;
            return b == null ? TimeSpan.Zero : b.BufferedDuration;
        }
    }

    public event Action<string>? ErrorOccurred;

    public void StartCapture(Action<ReadOnlyMemory<short>> onPcmSampleBlock)
    {
        ArgumentNullException.ThrowIfNull(onPcmSampleBlock);
        lock (_gate)
        {
            if (IsCapturing) return;
            _onPcm = onPcmSampleBlock;
            _waveIn = new WaveInEvent
            {
                DeviceNumber = _inputDeviceNumber,
                WaveFormat = Format,
                BufferMilliseconds = AudioFormat.FrameMilliseconds
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            IsCapturing = true;
        }
    }

    public void StopCapture()
    {
        lock (_gate)
        {
            if (_waveIn == null) return;
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try { _waveIn.StopRecording(); } finally { _waveIn.Dispose(); _waveIn = null; }
            IsCapturing = false;
            _onPcm = null;
        }
    }

    public void StartPlayback()
    {
        lock (_gate)
        {
            if (IsPlaying) return;
            _buffer = new BufferedWaveProvider(Format)
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true
            };
            _waveOut = new WaveOutEvent { DeviceNumber = _outputDeviceNumber, DesiredLatency = 80 };
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Init(_buffer);
            _waveOut.Play();
            IsPlaying = true;
        }
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            if (_waveOut == null) return;
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            try { _waveOut.Stop(); } finally { _waveOut.Dispose(); _waveOut = null; }
            _buffer?.ClearBuffer();
            _buffer = null;
            IsPlaying = false;
        }
    }

    public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm)
    {
        var buffer = _buffer;
        if (buffer == null || pcm.IsEmpty) return;

        var bytesSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.Span);
        if (_playbackBytesScratch.Length < bytesSpan.Length)
            _playbackBytesScratch = new byte[bytesSpan.Length];
        bytesSpan.CopyTo(_playbackBytesScratch);
        buffer.AddSamples(_playbackBytesScratch, 0, bytesSpan.Length);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var cb = _onPcm;
        if (cb == null || e.BytesRecorded < 2) return;

        int sampleCount = e.BytesRecorded / 2;
        if (_captureScratch.Length < sampleCount)
            _captureScratch = new short[sampleCount];
        Buffer.BlockCopy(e.Buffer, 0, _captureScratch, 0, sampleCount * 2);
        cb(_captureScratch.AsMemory(0, sampleCount));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) ErrorOccurred?.Invoke($"Microphone stopped: {e.Exception.Message}");
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) ErrorOccurred?.Invoke($"Playback stopped: {e.Exception.Message}");
    }

    public ValueTask DisposeAsync()
    {
        StopCapture();
        StopPlayback();
        return ValueTask.CompletedTask;
    }
}
