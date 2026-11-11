using NodeX.Core.Audio;

namespace NodeX.Core.Tests;

public class AudioLevelMeterTests
{
    [Fact]
    public void Silence_IsZero_AndFloorsDbfs()
    {
        var l = AudioLevelMeter.Measure(new short[160]);
        Assert.Equal(0, l.Rms);
        Assert.Equal(0, l.Peak);
        Assert.Equal(AudioLevels.MinDbfs, l.RmsDbfs);
    }

    [Fact]
    public void Empty_IsSilent() => Assert.Equal(AudioLevels.Silent, AudioLevelMeter.Measure([]));

    [Fact]
    public void FullScaleSquare_IsOne_AndMinusOneHandled()
    {
        short[] pcm = [short.MinValue, short.MaxValue, short.MinValue, short.MaxValue];
        var l = AudioLevelMeter.Measure(pcm);
        Assert.Equal(1.0, l.Peak, 3);
        Assert.InRange(l.Rms, 0.999, 1.0);
        Assert.InRange(l.PeakDbfs, -0.01, 0.0);
    }

    [Fact]
    public void Sine_RmsIsPeakOverRoot2()
    {
        short[] pcm = new short[800];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(16384 * Math.Sin(2 * Math.PI * i / 80));
        var l = AudioLevelMeter.Measure(pcm);
        Assert.Equal(0.5, l.Peak, 2);
        Assert.Equal(0.5 / Math.Sqrt(2), l.Rms, 2);
    }

    [Fact]
    public void Ballistics_AttackFasterThanRelease()
    {
        var m = new AudioLevelMeter { Attack = 0.8, Release = 0.1 };
        short[] loud = Enumerable.Repeat((short)16384, 160).ToArray();

        var up = m.Process(loud);
        Assert.Equal(0.5 * 0.8, up.Peak, 3);

        var down = m.Process(new short[160]);
        Assert.Equal(0.4 * 0.9, down.Peak, 3);
        m.Reset();
        Assert.Equal(AudioLevels.Silent, m.Current);
    }
}

public class PcmFramerTests
{
    [Fact]
    public void Reblocks_ArbitraryChunksIntoExact160SampleFrames_PreservingOrder()
    {
        var framer = new PcmFramer();
        var frames = new List<short[]>();
        short next = 0;

        foreach (int size in new[] { 1, 159, 161, 480, 7, 332 }) // 1140 samples = 7 frames + 20
        {
            short[] chunk = new short[size];
            for (int i = 0; i < size; i++) chunk[i] = next++;
            framer.Push(chunk, f => frames.Add(f.ToArray()));
        }

        Assert.Equal(7, frames.Count);
        Assert.All(frames, f => Assert.Equal(160, f.Length));
        Assert.Equal(20, framer.PendingSamples);
        Assert.Equal(Enumerable.Range(0, 1120).Select(i => (short)i), frames.SelectMany(f => f));
    }

    [Fact]
    public void Reset_DiscardsPartialFrame()
    {
        var framer = new PcmFramer();
        framer.Push(new short[100], _ => { });
        framer.Reset();
        Assert.Equal(0, framer.PendingSamples);
    }
}

public class JitterBufferTests
{
    private static short[] Frame(short v) => Enumerable.Repeat(v, 160).ToArray();

    private static JitterBuffer Primed(int depth = 3)
    {
        var jb = new JitterBuffer(targetDepthFrames: depth);
        for (int i = 0; i < depth; i++) jb.Push((ushort)(i * 20), Frame((short)(i + 1) * 1000 is var v ? (short)v : (short)0));
        return jb;
    }

    [Fact]
    public void Primes_BeforePlaying()
    {
        var jb = new JitterBuffer(targetDepthFrames: 3);
        var o = new short[160];
        jb.Push(0, Frame(1000));
        jb.Push(20, Frame(2000));
        Assert.True(jb.IsPriming);
        Assert.Equal(JitterPopResult.Silence, jb.Pop(o));

        jb.Push(40, Frame(3000));
        Assert.False(jb.IsPriming);
        Assert.Equal(JitterPopResult.Played, jb.Pop(o));
        Assert.Equal(1000, o[0]);
    }

    [Fact]
    public void InOrder_PlaysAllFramesInSequence()
    {
        var jb = Primed();
        var o = new short[160];
        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(JitterPopResult.Played, jb.Pop(o));
            Assert.Equal(i * 1000, o[0]);
        }
        Assert.Equal(3, jb.Stats.Played);
    }

    [Fact]
    public void Reorders_OutOfOrderPackets()
    {
        var jb = new JitterBuffer(targetDepthFrames: 3);
        jb.Push(0, Frame(1));
        jb.Push(40, Frame(3));
        jb.Push(20, Frame(2));
        var o = new short[160];
        for (short i = 1; i <= 3; i++)
        {
            Assert.Equal(JitterPopResult.Played, jb.Pop(o));
            Assert.Equal(i, o[0]);
        }
    }

    [Fact]
    public void DropsDuplicates_AndLateFrames()
    {
        var jb = Primed();
        var o = new short[160];
        jb.Push(20, Frame(9)); // duplicate of queued frame
        jb.Pop(o);             // plays ts=0
        jb.Push(0, Frame(9));  // late: already played

        var s = jb.Stats;
        Assert.Equal(1, s.Duplicate);
        Assert.Equal(1, s.Late);
    }

    [Fact]
    public void LostFrame_IsConcealedWithFade_ThenNextFrameResumes()
    {
        var jb = new JitterBuffer(targetDepthFrames: 3);
        jb.Push(0, Frame(8000));
        jb.Push(20, Frame(8000));
        // ts=40 lost
        jb.Push(60, Frame(5000));
        jb.Push(80, Frame(5000));

        var o = new short[160];
        jb.Pop(o); jb.Pop(o);
        Assert.Equal(JitterPopResult.Concealed, jb.Pop(o));
        Assert.Equal(4000, o[0]); // 8000 >> 1

        Assert.Equal(JitterPopResult.Played, jb.Pop(o));
        Assert.Equal(5000, o[0]);
        Assert.Equal(1, jb.Stats.Lost);
        Assert.Equal(1, jb.Stats.Concealed);
    }

    [Fact]
    public void LongLoss_FadesToSilence_AndRePrimesForNextOver()
    {
        var jb = new JitterBuffer(targetDepthFrames: 2, maxConcealFrames: 2);
        jb.Push(0, Frame(8000));
        jb.Push(20, Frame(8000));
        var o = new short[160];
        jb.Pop(o); jb.Pop(o);

        Assert.Equal(JitterPopResult.Concealed, jb.Pop(o));
        Assert.Equal(JitterPopResult.Concealed, jb.Pop(o));
        Assert.Equal(2000, o[0]);
        Assert.Equal(JitterPopResult.Silence, jb.Pop(o));
        Assert.True(jb.IsPriming);

        // New talk spurt with an unrelated timestamp re-anchors cleanly.
        jb.Push(30000, Frame(1234));
        jb.Push(30020, Frame(1234));
        Assert.Equal(JitterPopResult.Played, jb.Pop(o));
        Assert.Equal(1234, o[0]);
    }

    [Fact]
    public void TimestampWraparound_IsHandled()
    {
        var jb = new JitterBuffer(targetDepthFrames: 3);
        jb.Push(65500, Frame(1));
        jb.Push(65520, Frame(2));
        jb.Push(65540 - 65536 is var wrapped ? (ushort)wrapped : (ushort)0, Frame(3));

        var o = new short[160];
        for (short i = 1; i <= 3; i++)
        {
            Assert.Equal(JitterPopResult.Played, jb.Pop(o));
            Assert.Equal(i, o[0]);
        }
    }

    [Fact]
    public void FarFutureFrame_ResyncsInsteadOfCorrupting()
    {
        var jb = Primed();
        jb.Push(20000, Frame(7));
        Assert.Equal(1, jb.Stats.Overflow);
        Assert.True(jb.IsPriming);
        Assert.Equal(1, jb.Depth);
    }

    [Fact]
    public void ShortFrame_IsZeroPadded()
    {
        var jb = new JitterBuffer(targetDepthFrames: 1);
        jb.Push(0, new short[] { 5, 5, 5 });
        var o = new short[160];
        Array.Fill(o, (short)99);
        jb.Pop(o);
        Assert.Equal(5, o[2]);
        Assert.Equal(0, o[3]);
    }

    [Fact]
    public void Pop_RejectsSmallOutput()
    {
        Assert.Throws<ArgumentException>(() => new JitterBuffer().Pop(new short[10]));
    }

    [Fact]
    public async Task ConcurrentPushAndPop_DoesNotThrowOrDeadlock()
    {
        // Bounded by work done, not by a wall-clock window: a time window made this flaky when the thread pool was busy
        // (the producer could start after the window had closed and push nothing).
        const int frames = 5000;
        var jb = new JitterBuffer();
        int producerDone = 0;
        var producer = Task.Factory.StartNew(() =>
        {
            ushort ts = 0;
            for (int i = 0; i < frames; i++) { jb.Push(ts, Frame(1)); ts += 20; Thread.Yield(); }
            Volatile.Write(ref producerDone, 1);
        }, TaskCreationOptions.LongRunning);
        var consumer = Task.Factory.StartNew(() =>
        {
            var o = new short[160];
            while (Volatile.Read(ref producerDone) == 0) { jb.Pop(o); Thread.Yield(); }
        }, TaskCreationOptions.LongRunning);

        var all = Task.WhenAll(producer, consumer);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60))));   // deadlock guard
        await all;
        Assert.Equal(frames, jb.Stats.Received);
    }
}

public class NodeAudioEngineTests
{
    private sealed class FakeChannel : IVoiceChannel
    {
        private bool _tx;
        public bool IsTransmitting { get => _tx; set { _tx = value; TransmittingChanged?.Invoke(value); } }
        public event Action<bool>? TransmittingChanged;
        public List<byte[]> Sent { get; } = [];
        public event Action<ushort, ReadOnlyMemory<byte>>? VoiceFrameReceived;
        public bool HasSubscribers => VoiceFrameReceived != null;
        public void Receive(ushort ts, byte[] data) => VoiceFrameReceived?.Invoke(ts, data);

        public Task SendAudioFrameAsync(ReadOnlyMemory<byte> ulawAudio, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add(ulawAudio.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDevice : IAudioDeviceService
    {
        public bool IsCapturing { get; private set; }
        public bool IsPlaying { get; private set; }
        public TimeSpan BufferedPlayback { get; set; }
        public List<short[]> Played { get; } = [];
        public Action<ReadOnlyMemory<short>>? Capture;
        public event Action<string>? ErrorOccurred;

        public void StartCapture(Action<ReadOnlyMemory<short>> cb) { IsCapturing = true; Capture = cb; }
        public void StopCapture() => IsCapturing = false;
        public void StartPlayback() => IsPlaying = true;
        public void StopPlayback() => IsPlaying = false;
        public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm) { lock (Played) Played.Add(pcm.ToArray()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseError(string m) => ErrorOccurred?.Invoke(m);
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("Condition not met");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Capture_WhileKeyed_SendsOnlyWholeEncodedFrames()
    {
        var ch = new FakeChannel { IsTransmitting = true };
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(ch, dev);
        engine.Start();

        short[] tone = Enumerable.Repeat((short)10000, 400).ToArray(); // 2.5 frames
        dev.Capture!(tone);

        await WaitUntil(() => { lock (ch.Sent) return ch.Sent.Count == 2; });
        lock (ch.Sent)
        {
            Assert.All(ch.Sent, f => Assert.Equal(160, f.Length));
            Assert.Equal(G711Ulaw.EncodeSample(10000), ch.Sent[0][0]);
        }
    }

    [Fact]
    public async Task MicProcessing_BringsAQuietVoiceUp_BeforeEncoding()
    {
        var ch = new FakeChannel { IsTransmitting = true };
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(ch, dev, new NodeAudioEngineOptions { MicProcessing = true });
        engine.Start();

        // 4 s of a 1 kHz tone at about -40 dBFS RMS (the level a quiet mic produced in the live test).
        double amp = 32768.0 * Math.Pow(10, -40.0 / 20) * Math.Sqrt(2);
        for (int f = 0; f < 200; f++)
        {
            var s = new short[160];
            for (int i = 0; i < 160; i++) s[i] = (short)Math.Round(amp * Math.Sin(2 * Math.PI * 1000 * (f * 160 + i) / 8000.0));
            dev.Capture!(s);
        }

        // The send queue drops the oldest frames when fed faster than real time, so wait for it to drain
        // rather than for all 200 frames; what matters is the most recent frame, sent after the AGC settled.
        await WaitUntil(() => { lock (ch.Sent) return ch.Sent.Count >= 8; });
        int count;
        do { lock (ch.Sent) count = ch.Sent.Count; await Task.Delay(100); }
        while (count != ch.Sent.Count);
        byte[] last;
        lock (ch.Sent) last = ch.Sent[^1];
        double rms = Math.Sqrt(G711Ulaw.Decode(last).Select(x => (double)x * x).Average());
        double db = 20 * Math.Log10(rms / 32768.0);
        Assert.InRange(db, -25, -17);              // lifted from -40 towards the -21 dBFS target
    }

    [Fact]
    public async Task OpenMicOnlyWhileKeyed_OpensOnKey_AndClosesAfterHoldOff()
    {
        var ch = new FakeChannel();
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(ch, dev,
            new NodeAudioEngineOptions { OpenMicOnlyWhileKeyed = true, MicHoldOff = TimeSpan.FromMilliseconds(100) });
        engine.Start();
        Assert.False(dev.IsCapturing);                 // idle: mic closed
        Assert.True(dev.IsPlaying);                    // receive path still running

        ch.IsTransmitting = true;
        Assert.True(dev.IsCapturing);

        ch.IsTransmitting = false;
        Assert.True(dev.IsCapturing);                  // hold-off
        await WaitUntil(() => !dev.IsCapturing);

        ch.IsTransmitting = true;                      // re-key during hold-off keeps it open
        ch.IsTransmitting = false;
        ch.IsTransmitting = true;
        await Task.Delay(250);
        Assert.True(dev.IsCapturing);
    }

    [Fact]
    public async Task Capture_WhenNotKeyed_SendsNothing_ButStillMetersInput()
    {
        var ch = new FakeChannel { IsTransmitting = false };
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(ch, dev);
        AudioLevels last = default;
        engine.InputLevelChanged += l => last = l;
        engine.Start();

        dev.Capture!(Enumerable.Repeat((short)16384, 160).ToArray());
        await Task.Delay(50);

        lock (ch.Sent) Assert.Empty(ch.Sent);
        Assert.True(last.Peak > 0.3);
    }

    [Fact]
    public async Task ReceivedAudio_IsDecodedBufferedAndPlayed()
    {
        var ch = new FakeChannel();
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(ch, dev);
        bool? active = null;
        engine.RemoteAudioActiveChanged += a => active = a;
        engine.Start();

        byte code = G711Ulaw.EncodeSample(8000);
        for (int i = 0; i < 6; i++) ch.Receive((ushort)(i * 20), Enumerable.Repeat(code, 160).ToArray());

        await WaitUntil(() => { lock (dev.Played) return dev.Played.Any(f => f[0] != 0); });
        Assert.True(active);
        lock (dev.Played)
        {
            var f = dev.Played.First(x => x[0] != 0);
            Assert.Equal(160, f.Length);
            Assert.Equal(G711Ulaw.DecodeSample(code), f[0]);
        }
        Assert.True(engine.OutputLevel.Peak >= 0);
    }

    [Fact]
    public async Task Muted_StillMetersAndReportsActivity_ButPlaysNothing()
    {
        var ch = new FakeChannel();
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(ch, dev) { Muted = true };
        bool? active = null;
        AudioLevels last = AudioLevels.Silent;
        engine.RemoteAudioActiveChanged += a => active = a;
        engine.OutputLevelChanged += l => last = l;
        engine.Start();

        byte code = G711Ulaw.EncodeSample(8000);
        for (int i = 0; i < 6; i++) ch.Receive((ushort)(i * 20), Enumerable.Repeat(code, 160).ToArray());

        await WaitUntil(() => active == true && last.Peak > 0);   // heard, metered, reported...
        await Task.Delay(100);
        lock (dev.Played) Assert.Empty(dev.Played);               // ...but nothing went to the speaker
    }

    [Fact]
    public async Task Muted_WithKeepAlive_WritesSilenceInsteadOfAudio()
    {
        var ch = new FakeChannel();
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(ch, dev, new NodeAudioEngineOptions { KeepAudioPipelineAlive = true })
        {
            Muted = true
        };
        engine.Start();

        byte code = G711Ulaw.EncodeSample(8000);
        for (int i = 0; i < 6; i++) ch.Receive((ushort)(i * 20), Enumerable.Repeat(code, 160).ToArray());

        await WaitUntil(() => { lock (dev.Played) return dev.Played.Count >= 8; });
        lock (dev.Played) Assert.All(dev.Played, f => Assert.All(f, s => Assert.Equal(0, s)));
    }

    [Fact]
    public async Task Unmuting_RestoresPlayback()
    {
        var ch = new FakeChannel();
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(ch, dev) { Muted = true };
        engine.Start();
        engine.Muted = false;

        byte code = G711Ulaw.EncodeSample(8000);
        for (int i = 0; i < 6; i++) ch.Receive((ushort)(i * 20), Enumerable.Repeat(code, 160).ToArray());

        await WaitUntil(() => { lock (dev.Played) return dev.Played.Any(f => f[0] != 0); });
    }

    [Fact]
    public async Task SilenceIsNotQueued_UnlessKeepAliveEnabled()
    {
        var ch = new FakeChannel();
        var dev = new FakeDevice();
        await using (var engine = new NodeAudioEngine(ch, dev))
        {
            engine.Start();
            await Task.Delay(150);
            await engine.StopAsync();
        }
        lock (dev.Played) Assert.Empty(dev.Played);

        var dev2 = new FakeDevice();
        await using var keepAlive = new NodeAudioEngine(ch, dev2, new NodeAudioEngineOptions { KeepAudioPipelineAlive = true });
        keepAlive.Start();
        await WaitUntil(() => { lock (dev2.Played) return dev2.Played.Count >= 3; });
        lock (dev2.Played) Assert.All(dev2.Played, f => Assert.All(f, s => Assert.Equal(0, s)));
    }

    [Fact]
    public async Task DeviceBacklog_DropsFramesToBoundLatency()
    {
        var ch = new FakeChannel();
        var dev = new FakeDevice { BufferedPlayback = TimeSpan.FromSeconds(1) };
        await using var engine = new NodeAudioEngine(ch, dev, new NodeAudioEngineOptions { KeepAudioPipelineAlive = true });

        engine.PlayOneFrame(new short[160]);
        lock (dev.Played) Assert.Empty(dev.Played);

        dev.BufferedPlayback = TimeSpan.Zero;
        engine.PlayOneFrame(new short[160]);
        lock (dev.Played) Assert.Single(dev.Played);
    }

    [Fact]
    public async Task Stop_UnsubscribesStopsDevice_AndIsIdempotent()
    {
        var ch = new FakeChannel();
        var dev = new FakeDevice();
        var engine = new NodeAudioEngine(ch, dev);
        engine.Start();
        Assert.True(dev.IsCapturing && dev.IsPlaying && ch.HasSubscribers);

        await engine.StopAsync();
        Assert.False(dev.IsCapturing || dev.IsPlaying || ch.HasSubscribers);
        Assert.False(engine.IsRunning);

        await engine.StopAsync();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task DeviceErrors_AreForwarded()
    {
        var dev = new FakeDevice();
        await using var engine = new NodeAudioEngine(new FakeChannel(), dev);
        string? msg = null;
        engine.ErrorOccurred += m => msg = m;
        engine.Start();
        dev.RaiseError("mic unplugged");
        Assert.Equal("mic unplugged", msg);
    }
}
