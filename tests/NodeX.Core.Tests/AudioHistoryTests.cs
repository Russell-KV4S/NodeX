using NodeX.Core.Audio;

namespace NodeX.Core.Tests;

public sealed class AudioHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nodex-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class RecordingAudioDevice : IAudioDeviceService
    {
        public bool IsCapturing { get; private set; }
        public bool IsPlaying { get; private set; }
        public TimeSpan BufferedPlayback => TimeSpan.Zero;
        public event Action<string>? ErrorOccurred { add { } remove { } }
        public int SamplesPlayed;

        public void StartCapture(Action<ReadOnlyMemory<short>> onPcmSampleBlock) => IsCapturing = true;
        public void StopCapture() => IsCapturing = false;
        public void StartPlayback() => IsPlaying = true;
        public void StopPlayback() => IsPlaying = false;
        public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm) => Interlocked.Add(ref SamplesPlayed, pcm.Length);
        public ValueTask DisposeAsync() { StopPlayback(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public void WavCodec_RoundTripsPcm16Samples()
    {
        short[] input = [0, 1234, -5678, short.MaxValue, short.MinValue, 42, -42];
        byte[] wav = WavCodec.EncodePcm16(input, 8000);
        short[] decoded = WavCodec.DecodePcm16(wav, out int rate);

        Assert.Equal(8000, rate);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void WavCodec_RejectsCorruptData()
    {
        Assert.Throws<InvalidDataException>(() => WavCodec.DecodePcm16("not-a-wav"u8, out _));
    }

    [Fact]
    public void AudioHistoryStore_PersistsPrunesAndSelfHeals()
    {
        var store = new AudioHistoryStore(_dir, maxClips: 10);
        short[] samples = new short[AudioFormat.SampleRate]; // 1 second

        var t0 = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 12; i++)
        {
            store.SaveClip($"52380{i % 4}", $"Node {i}", i % 2 == 0 ? AudioClipDirection.Rx : AudioClipDirection.Tx,
                samples, t0.AddSeconds(i * 10));
        }

        // Pruned to MaxClips = 10, newest first
        Assert.Equal(10, store.Clips.Count);
        Assert.Equal(t0.AddSeconds(110), store.Clips[0].TimestampUtc);
        Assert.Equal(5, store.UnheardCount); // only RX clips start unheard
        Assert.NotNull(store.LatestRxClip);

        // Mark heard persists across reload
        string latestRxId = store.LatestRxClip!.Id;
        store.MarkHeard(latestRxId);
        Assert.Equal(4, store.UnheardCount);

        // Delete one WAV file externally and reload — store drops missing entry cleanly
        File.Delete(store.GetFilePath(store.Clips[^1]));
        var reloaded = new AudioHistoryStore(_dir, maxClips: 10);
        reloaded.Load();
        Assert.Equal(9, reloaded.Clips.Count);
        Assert.Equal(3, reloaded.UnheardCount);

        reloaded.ClearAll();
        Assert.Empty(reloaded.Clips);
        Assert.Empty(Directory.GetFiles(_dir, "*.wav"));
    }

    [Fact]
    public async Task AudioClipPlayer_ReplaysSavedWavLocally()
    {
        var store = new AudioHistoryStore(_dir, maxClips: 10);
        short[] samples = new short[AudioFormat.SamplesPerFrame * 4]; // 80 ms
        for (int i = 0; i < samples.Length; i++) samples[i] = 4000;
        var clip = store.SaveClip("523800", "KV4S Cloud", AudioClipDirection.Rx, samples);

        var device = new RecordingAudioDevice();
        await using var player = new AudioClipPlayer(() => device);
        await player.PlayAsync(clip, store.GetFilePath(clip));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (player.IsPlaying && !cts.IsCancellationRequested)
            await Task.Delay(20);

        Assert.False(player.IsPlaying);
        Assert.Equal(samples.Length, device.SamplesPlayed);
    }
}
