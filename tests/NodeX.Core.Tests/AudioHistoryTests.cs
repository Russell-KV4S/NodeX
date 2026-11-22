using NodeX.Core.Audio;
using NodeX.Core.Settings;

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

    [Fact]
    public void AudioHistoryStore_CombineClips_OrdersChronologicallyWithSilenceGaps_AndSetsMetadata()
    {
        var store = new AudioHistoryStore(_dir, maxClips: 25);
        var t0 = new DateTimeOffset(2026, 11, 21, 18, 0, 0, TimeSpan.Zero);

        short[] s1 = Enumerable.Repeat((short)1111, AudioFormat.SamplesPerFrame * 2).ToArray();
        short[] s2 = Enumerable.Repeat((short)2222, AudioFormat.SamplesPerFrame * 2).ToArray();
        short[] s3 = Enumerable.Repeat((short)3333, AudioFormat.SamplesPerFrame * 2).ToArray();

        // Save in mixed order to confirm CombineClips sorts strictly oldest -> newest by TimestampUtc
        var c1 = store.SaveClip("523800", "Home", AudioClipDirection.Rx, s1, t0.AddSeconds(10));
        var c3 = store.SaveClip("523800", "Home", AudioClipDirection.Rx, s3, t0.AddSeconds(30));
        var c2 = store.SaveClip("523800", "Home", AudioClipDirection.Tx, s2, t0.AddSeconds(20));

        // Combine single-node clips passed out-of-order
        var combined = store.CombineClips([c3.Id, c1.Id, c2.Id], customTitle: "  Net Check  ", timestampUtc: t0.AddSeconds(40));

        Assert.True(combined.IsCombined);
        Assert.False(combined.IsUnheard);
        Assert.Equal("523800", combined.Node);
        Assert.Equal("Net Check", combined.NodeLabel);
        Assert.Equal(combined.Id, store.Clips[0].Id);
        Assert.Equal(4, store.Clips.Count); // originals preserved
        Assert.True(File.Exists(store.GetFilePath(combined)));

        short[] decoded = WavCodec.DecodePcm16(File.ReadAllBytes(store.GetFilePath(combined)), out int rate);
        Assert.Equal(AudioFormat.SampleRate, rate);

        int gapSamples = AudioFormat.SampleRate * AudioHistoryStore.CombineGapMilliseconds / 1000;
        short[] silence = new short[gapSamples];
        short[] expected = [.. s1, .. silence, .. s2, .. silence, .. s3];
        Assert.Equal(expected, decoded);

        // Multi-node combination with default label
        var otherNode = store.SaveClip("27339", "Alabama Link", AudioClipDirection.Rx, s1, t0.AddSeconds(50));
        var multi = store.CombineClips([c1.Id, otherNode.Id]);
        Assert.Equal("Combined", multi.Node);
        Assert.Equal("Combined · 2 nodes (2 clips)", multi.NodeLabel);

        // Reject < 2 clips
        Assert.Throws<InvalidOperationException>(() => store.CombineClips([c1.Id]));
    }

    [Fact]
    public void AudioHistoryStore_DeleteMany_RemovesSelectedClipsInBatch()
    {
        var store = new AudioHistoryStore(_dir, maxClips: 25);
        short[] samples = new short[AudioFormat.SamplesPerFrame * 2];
        var t0 = DateTimeOffset.UtcNow;

        var a = store.SaveClip("523800", "", AudioClipDirection.Rx, samples, t0.AddSeconds(1));
        var b = store.SaveClip("523800", "", AudioClipDirection.Rx, samples, t0.AddSeconds(2));
        var c = store.SaveClip("523800", "", AudioClipDirection.Tx, samples, t0.AddSeconds(3));
        var d = store.SaveClip("523800", "", AudioClipDirection.Rx, samples, t0.AddSeconds(4));

        int deleted = store.DeleteMany([a.Id, c.Id, "missing-id"]);
        Assert.Equal(2, deleted);
        Assert.Equal(2, store.Clips.Count);
        Assert.False(File.Exists(store.GetFilePath(a)));
        Assert.False(File.Exists(store.GetFilePath(c)));
        Assert.True(File.Exists(store.GetFilePath(b)));
        Assert.True(File.Exists(store.GetFilePath(d)));
    }
}
