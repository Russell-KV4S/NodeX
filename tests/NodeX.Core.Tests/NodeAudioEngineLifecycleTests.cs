using NodeX.Core.Audio;

namespace NodeX.Core.Tests;

/// <summary>
/// Start and stop of the audio engine must never interleave. Seen on a phone 2026-11-12: a node hung up about
/// 100 ms after accepting, so the call's audio was being started (on a worker thread) while the call was being
/// disposed, and the log showed "Audio could not start: Object reference not set to an instance of an object".
/// </summary>
public class NodeAudioEngineLifecycleTests
{
    private sealed class QuietChannel : IVoiceChannel
    {
        public bool IsTransmitting => false;
        public event Action<ushort, ReadOnlyMemory<byte>>? VoiceFrameReceived;
        public bool HasSubscribers => VoiceFrameReceived != null;
        public Task SendAudioFrameAsync(ReadOnlyMemory<byte> ulawAudio, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>A device whose playback takes a moment to open, as a real one does, to widen the race.</summary>
    private sealed class SlowDevice : IAudioDeviceService
    {
        private int _playing;
        public bool IsCapturing => false;
        public bool IsPlaying => Volatile.Read(ref _playing) != 0;
        public int Starts;
        public TimeSpan BufferedPlayback => TimeSpan.Zero;
        public event Action<string>? ErrorOccurred { add { } remove { } }
        public void StartCapture(Action<ReadOnlyMemory<short>> onPcm) { }
        public void StopCapture() { }

        public void StartPlayback()
        {
            Interlocked.Increment(ref Starts);
            Thread.Sleep(2);
            Volatile.Write(ref _playing, 1);
        }

        public void StopPlayback() => Volatile.Write(ref _playing, 0);
        public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Start_AfterDispose_DoesNothing()
    {
        var channel = new QuietChannel();
        var device = new SlowDevice();
        var engine = new NodeAudioEngine(channel, device);

        await engine.DisposeAsync();
        engine.Start();

        Assert.False(engine.IsRunning);
        Assert.False(device.IsPlaying);
        Assert.Equal(0, device.Starts);
        Assert.False(channel.HasSubscribers);
    }

    [Fact]
    public async Task StartRacingDispose_NeverThrows_AndNeverLeavesTheDeviceRunning()
    {
        for (int i = 0; i < 300; i++)
        {
            var channel = new QuietChannel();
            var device = new SlowDevice();
            var engine = new NodeAudioEngine(channel, device);
            using var go = new ManualResetEventSlim();

            var start = Task.Run(() => { go.Wait(); engine.Start(); });
            var dispose = Task.Run(async () => { go.Wait(); await engine.DisposeAsync(); });
            go.Set();

            await start;        // the old code threw a NullReferenceException here when Dispose won the race
            await dispose;

            Assert.False(engine.IsRunning);
            Assert.False(device.IsPlaying);
            Assert.False(channel.HasSubscribers);
        }
    }
}
