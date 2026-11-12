using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NodeX.Core.Audio;
using PortAudioSharp;
using Stream = PortAudioSharp.Stream;

namespace NodeX.UI.Desktop.Audio;

/// <summary>
/// Cross-platform (Linux ALSA, macOS CoreAudio; also works on Windows) implementation of
/// <see cref="IAudioDeviceService"/> using PortAudio. The pipeline format is 8 kHz / 16-bit / mono; each
/// stream is first opened at exactly that format and, if the host API refuses it, at the device's default
/// rate with conversion done here (area-average down for the mic, linear interpolation up for the speaker).
/// </summary>
public sealed class PortAudioDeviceService : IAudioDeviceService
{
    private static readonly object InitGate = new();
    private static bool _initialised;

    private readonly int _inputDevice;
    private readonly int _outputDevice;
    private readonly bool _tryPipelineRateFirst;
    private readonly object _gate = new();

    private Stream? _inStream;
    private Stream? _outStream;
    private Stream.Callback? _inCallback;   // kept referenced so the GC cannot collect the native callback
    private Stream.Callback? _outCallback;
    private Action<ReadOnlyMemory<short>>? _onPcm;
    private PcmResampler? _micResampler;
    private int _micRate = AudioFormat.SampleRate;

    // Playback ring (samples at the output stream rate) and reusable callback scratch buffers.
    private readonly object _ringGate = new();
    private readonly PlaybackRingBuffer _playbackRing = new();
    private short[] _inScratch = Array.Empty<short>();
    private short[] _resampleScratch = Array.Empty<short>();
    private short[] _outScratch = Array.Empty<short>();

    /// <param name="inputDevice">PortAudio device index, or -1 for the system default.</param>
    /// <param name="outputDevice">PortAudio device index, or -1 for the system default.</param>
    public PortAudioDeviceService(int inputDevice = -1, int outputDevice = -1, bool tryPipelineRateFirst = true)
    {
        _inputDevice = inputDevice;
        _outputDevice = outputDevice;
        _tryPipelineRateFirst = tryPipelineRateFirst;
    }

    public bool IsCapturing { get; private set; }
    public bool IsPlaying { get; private set; }

    public TimeSpan BufferedPlayback
    {
        get
        {
            lock (_ringGate)
                return _playbackRing.BufferedDuration;
        }
    }

    public event Action<string>? ErrorOccurred;

    /// <summary>Lists devices as (PortAudio index, name, max input channels, max output channels).</summary>
    public static IReadOnlyList<(int Index, string Name, int InputChannels, int OutputChannels)> ListDevices()
    {
        EnsureInitialised();
        var list = new List<(int, string, int, int)>();
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            list.Add((i, d.name, d.maxInputChannels, d.maxOutputChannels));
        }
        return list;
    }

    /// <summary>
    /// Checks that the native PortAudio library loads and initialises. Returns null when audio is usable,
    /// otherwise a human-readable reason. The bundled Linux library links libjack.so.0, so a machine
    /// without JACK client libraries lands here (fix: install libjack, e.g. libjack-jackd2-0).
    /// </summary>
    public static string? CheckAvailable()
    {
        try
        {
            EnsureInitialised();
            return null;
        }
        catch (DllNotFoundException ex)
        {
            return OperatingSystem.IsLinux()
                ? "PortAudio could not load (usually the JACK client library is missing: install libjack-jackd2-0 "
                  + $"or libjack0, plus ALSA). {FirstLine(ex.Message)}"
                : $"PortAudio could not load. {FirstLine(ex.Message)}";
        }
        catch (Exception ex)
        {
            return $"PortAudio failed to initialise: {ex.Message}";
        }
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return i < 0 ? s : s[..i].Trim();
    }

    private static void EnsureInitialised()
    {
        lock (InitGate)
        {
            if (_initialised) return;
            PortAudio.Initialize();
            _initialised = true;
        }
    }

    public void StartCapture(Action<ReadOnlyMemory<short>> onPcmSampleBlock)
    {
        ArgumentNullException.ThrowIfNull(onPcmSampleBlock);
        EnsureInitialised();
        lock (_gate)
        {
            if (IsCapturing) return;
            int dev = _inputDevice >= 0 ? _inputDevice : PortAudio.DefaultInputDevice;
            if (dev == PortAudio.NoDevice) throw new InvalidOperationException("No microphone found.");
            var info = PortAudio.GetDeviceInfo(dev);
            if (info.maxInputChannels < 1) throw new InvalidOperationException($"'{info.name}' has no input channels.");

            _onPcm = onPcmSampleBlock;
            Stream.Callback cb = OnInput;
            var stream = OpenWithFallback(
                rate => new Stream(
                    new StreamParameters
                    {
                        device = dev,
                        channelCount = 1,
                        sampleFormat = SampleFormat.Int16,
                        suggestedLatency = Math.Max(info.defaultLowInputLatency, 0.02),
                        hostApiSpecificStreamInfo = IntPtr.Zero
                    },
                    null, rate, PortAudio.FramesPerBufferUnspecified, StreamFlags.ClipOff, cb, null),
                info.defaultSampleRate, _tryPipelineRateFirst, out int usedRate);

            _micRate = usedRate;
            _micResampler = usedRate == AudioFormat.SampleRate ? null : new PcmResampler(usedRate);
            _inCallback = cb;
            _inStream = stream;
            try { stream.Start(); }
            catch { stream.Dispose(); _inStream = null; _inCallback = null; _onPcm = null; throw; }
            IsCapturing = true;
        }
    }

    public void StopCapture()
    {
        lock (_gate)
        {
            if (_inStream == null) return;
            IsCapturing = false;
            _onPcm = null;
            try { _inStream.Stop(); } catch { /* already stopped */ }
            _inStream.Dispose();
            _inStream = null;
            _inCallback = null;
            _micResampler = null;
        }
    }

    public void StartPlayback()
    {
        EnsureInitialised();
        lock (_gate)
        {
            if (IsPlaying) return;
            int dev = _outputDevice >= 0 ? _outputDevice : PortAudio.DefaultOutputDevice;
            if (dev == PortAudio.NoDevice) throw new InvalidOperationException("No speaker found.");
            var info = PortAudio.GetDeviceInfo(dev);
            if (info.maxOutputChannels < 1) throw new InvalidOperationException($"'{info.name}' has no output channels.");

            Stream.Callback cb = OnOutput;
            var stream = OpenWithFallback(
                rate => new Stream(
                    null,
                    new StreamParameters
                    {
                        device = dev,
                        channelCount = 1,
                        sampleFormat = SampleFormat.Int16,
                        suggestedLatency = Math.Max(info.defaultLowOutputLatency, 0.06),
                        hostApiSpecificStreamInfo = IntPtr.Zero
                    },
                    rate, PortAudio.FramesPerBufferUnspecified, StreamFlags.ClipOff, cb, null),
                info.defaultSampleRate, _tryPipelineRateFirst, out int usedRate);

            lock (_ringGate)
            {
                _playbackRing.Configure(usedRate);
            }
            _outCallback = cb;
            _outStream = stream;
            try { stream.Start(); }
            catch { stream.Dispose(); _outStream = null; _outCallback = null; throw; }
            IsPlaying = true;
        }
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            if (_outStream == null) return;
            IsPlaying = false;
            try { _outStream.Stop(); } catch { /* already stopped */ }
            _outStream.Dispose();
            _outStream = null;
            _outCallback = null;
            lock (_ringGate) { _playbackRing.Clear(); }
        }
    }

    public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm)
    {
        if (pcm.IsEmpty) return;
        lock (_ringGate)
        {
            if (!IsPlaying) return;
            _playbackRing.Enqueue(pcm.Span);
        }
    }

    private StreamCallbackResult OnInput(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        try
        {
            var cb = _onPcm;
            if (cb == null || input == IntPtr.Zero || frameCount == 0) return StreamCallbackResult.Continue;

            int count = (int)frameCount;
            if (_inScratch.Length < count) _inScratch = new short[count];
            Marshal.Copy(input, _inScratch, 0, count);

            var rs = _micResampler;
            if (rs == null)
            {
                cb(_inScratch.AsMemory(0, count));
            }
            else
            {
                int maxOut = rs.MaxOutputSamples(count);
                if (_resampleScratch.Length < maxOut) _resampleScratch = new short[maxOut];
                int n = rs.Process(_inScratch.AsSpan(0, count), _resampleScratch);
                if (n > 0) cb(_resampleScratch.AsMemory(0, n));
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into native code.
            ErrorOccurred?.Invoke($"Microphone callback failed: {ex.Message}");
        }
        return StreamCallbackResult.Continue;
    }

    private StreamCallbackResult OnOutput(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        try
        {
            int want = (int)frameCount;
            if (_outScratch.Length < want) _outScratch = new short[want];
            lock (_ringGate)
            {
                _playbackRing.Read(_outScratch.AsSpan(0, want));
            }
            Marshal.Copy(_outScratch, 0, output, want);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Playback callback failed: {ex.Message}");
        }
        return StreamCallbackResult.Continue;
    }

    /// <summary>Tries the 8 kHz pipeline rate first, then the device's default rate.</summary>
    private static Stream OpenWithFallback(Func<double, Stream> open, double deviceRate, bool pipelineFirst, out int usedRate)
    {
        int fallback = deviceRate >= 8000 ? (int)Math.Round(deviceRate) : 48000;
        if (!pipelineFirst && fallback != AudioFormat.SampleRate)
        {
            // Test seam / quirky hosts: go straight to the device rate (conversion happens in this class).
            var s = open(fallback);
            usedRate = fallback;
            return s;
        }

        try
        {
            var s = open(AudioFormat.SampleRate);
            usedRate = AudioFormat.SampleRate;
            return s;
        }
        catch (Exception first)
        {
            try
            {
                var s = open(fallback);
                usedRate = fallback;
                return s;
            }
            catch (Exception second)
            {
                throw new InvalidOperationException(
                    $"Audio device would not open at 8000 Hz ({first.Message}) or {fallback} Hz ({second.Message}).", second);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        StopCapture();
        StopPlayback();
        return ValueTask.CompletedTask;
    }
}
