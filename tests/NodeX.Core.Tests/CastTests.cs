using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NodeX.Core.Audio;
using NodeX.Core.Cast;
using NodeX.Core.Settings;

namespace NodeX.Core.Tests;

public class CastTests
{
    private sealed class FakeCastServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private TcpClient? _client;
        private Task? _task;

        public FakeCastServer(
            bool rejectLaunch = false,
            Func<CastMessage, Stream, Task>? onMessage = null)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            EndPoint = (IPEndPoint)_listener.LocalEndpoint;
            _task = Task.Run(() => ServeAsync(rejectLaunch, onMessage, _cts.Token));
        }

        public IPEndPoint EndPoint { get; }
        public List<CastMessage> Received { get; } = [];

        public Task<Stream> OpenClientStreamAsync(IPEndPoint ep, CancellationToken ct)
        {
            var tcp = new TcpClient();
            tcp.Connect(ep.Address, ep.Port);
            return Task.FromResult<Stream>(tcp.GetStream());
        }

        private async Task ServeAsync(bool rejectLaunch, Func<CastMessage, Stream, Task>? onMessage, CancellationToken ct)
        {
            try
            {
                _client = await _listener.AcceptTcpClientAsync(ct);
                using var stream = _client.GetStream();

                while (!ct.IsCancellationRequested)
                {
                    var msg = await CastProtocol.ReadFrameAsync(stream, ct);
                    if (msg == null) break;
                    lock (Received) Received.Add(msg);

                    if (msg.Namespace == CastProtocol.NsHeartbeat && msg.PayloadUtf8?.Contains("\"PING\"") == true)
                    {
                        await CastProtocol.WriteFrameAsync(stream,
                            new CastMessage("receiver-0", msg.SourceId, CastProtocol.NsHeartbeat, CastPayloadType.String, """{"type":"PONG"}"""), ct);
                    }
                    else if (msg.Namespace == CastProtocol.NsReceiver && msg.PayloadUtf8?.Contains("\"LAUNCH\"") == true)
                    {
                        string reply = rejectLaunch
                            ? """{"type":"LAUNCH_ERROR","reason":"NOT_ALLOWED","requestId":1}"""
                            : """{"type":"RECEIVER_STATUS","requestId":1,"status":{"volume":{"level":0.65,"muted":false},"applications":[{"appId":"ABCDEF12","sessionId":"sess-42","transportId":"web-7"}]}}""";
                        await CastProtocol.WriteFrameAsync(stream,
                            new CastMessage("receiver-0", msg.SourceId, CastProtocol.NsReceiver, CastPayloadType.String, reply), ct);
                    }
                    else if (msg.Namespace == CastProtocol.NsReceiver && msg.PayloadUtf8?.Contains("\"SET_VOLUME\"") == true)
                    {
                        using var doc = JsonDocument.Parse(msg.PayloadUtf8!);
                        var v = doc.RootElement.GetProperty("volume");
                        double lvl = v.TryGetProperty("level", out var le) ? le.GetDouble() : 0.65;
                        bool muted = v.TryGetProperty("muted", out var me) && me.GetBoolean();
                        string status = JsonSerializer.Serialize(new
                        {
                            type = "RECEIVER_STATUS",
                            status = new
                            {
                                volume = new { level = lvl, muted },
                                applications = new[] { new { appId = "ABCDEF12", sessionId = "sess-42", transportId = "web-7" } }
                            }
                        });
                        await CastProtocol.WriteFrameAsync(stream,
                            new CastMessage("receiver-0", "*", CastProtocol.NsReceiver, CastPayloadType.String, status), ct);
                    }

                    if (onMessage != null)
                        await onMessage(msg, stream);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _client?.Dispose(); } catch { }
            if (_task != null) try { await _task; } catch { }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task CastProtocol_EncodesDecodesStringAndBinary_AndFramesOverStream()
    {
        var strMsg = new CastMessage("sender-0", "web-9", CastProtocol.NsNodeX, CastPayloadType.String, """{"type":"state"}""");
        byte[] encoded = CastProtocol.Encode(strMsg);
        Assert.True(CastProtocol.TryDecode(encoded, out var decodedStr));
        Assert.Equal(strMsg, decodedStr);

        var binMsg = new CastMessage("sender-0", "receiver-0", "urn:x-cast:bin", CastPayloadType.Binary, PayloadBinary: [1, 2, 3, 255]);
        Assert.True(CastProtocol.TryDecode(CastProtocol.Encode(binMsg), out var decodedBin));
        Assert.Equal("urn:x-cast:bin", decodedBin!.Namespace);
        Assert.Equal([1, 2, 3, 255], decodedBin.PayloadBinary);

        // Unknown varint field (field 9, wire type 0) appended cleanly must be skipped without error.
        byte[] withExtra = [.. encoded, (9 << 3) | 0, 0x96, 0x01];
        Assert.True(CastProtocol.TryDecode(withExtra, out var withUnknown));
        Assert.Equal(strMsg.PayloadUtf8, withUnknown!.PayloadUtf8);

        // Truncated protobuf rejected cleanly.
        Assert.False(CastProtocol.TryDecode(encoded.AsSpan(0, 6), out _));

        // 4-byte BE stream framing round-trip.
        using var ms = new MemoryStream();
        await CastProtocol.WriteFrameAsync(ms, strMsg);
        ms.Position = 0;
        var fromStream = await CastProtocol.ReadFrameAsync(ms);
        Assert.Equal(strMsg, fromStream);
        Assert.Null(await CastProtocol.ReadFrameAsync(ms));

        // Oversized length prefix rejected cleanly.
        using var bad = new MemoryStream([0x10, 0x00, 0x00, 0x00]);
        await Assert.ThrowsAsync<InvalidDataException>(() => CastProtocol.ReadFrameAsync(bad));
    }

    [Fact]
    public void CastDiscovery_ParsesCompressedSplitMdnsRecords_AndManualAddresses()
    {
        byte[] query = CastDiscovery.BuildPtrQuery();
        Assert.Equal(0x80, query[^2]); // QU unicast-response bit set

        var acc = new CastDiscovery.MdnsAccumulator();
        const string instance = "Living Room TV._googlecast._tcp.local";
        const string host = "living-tv.local";

        // Packet 1: PTR + SRV (compressed host + port 8009)
        byte[] pkt1 = BuildMdnsPacket(
            ptr: (CastDiscovery.ServiceName, instance),
            srv: (instance, 8009, host));
        acc.AddPacket(pkt1);
        Assert.Single(acc.PendingInstances());

        // Packet 2: TXT + A record (with ca=4101 -> HasScreen = true)
        byte[] pkt2 = BuildMdnsPacket(
            txt: (instance, ["fn=Living Room TV", "md=Chromecast with Google TV", "id=abc123", "ca=4101", "rs=Ready"]),
            a: (host, IPAddress.Parse("192.168.1.44")));
        acc.AddPacket(pkt2);
        Assert.Empty(acc.PendingInstances());

        // Second speaker-only device (ca=4100 -> HasScreen = false)
        const string speakerInst = "Kitchen Speaker._googlecast._tcp.local";
        acc.AddPacket(BuildMdnsPacket(
            ptr: (CastDiscovery.ServiceName, speakerInst),
            srv: (speakerInst, 8010, "kitchen.local"),
            txt: (speakerInst, ["fn=Kitchen Speaker", "md=Nest Audio", "id=def456", "ca=4100"]),
            a: ("kitchen.local", IPAddress.Parse("192.168.1.55"))));

        // Malicious self-loop compression pointer must be ignored safely without hanging.
        byte[] loopPkt = [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 0x0C, 0, 12, 0, 1, 0, 0, 0, 10, 0, 2, 0xC0, 0x0C];
        acc.AddPacket(loopPkt);

        var devices = acc.BuildDevices();
        Assert.Equal(2, devices.Count);
        Assert.Equal("Kitchen Speaker", devices[0].FriendlyName);
        Assert.False(devices[0].HasScreen);
        Assert.Equal(8010, devices[0].Port);
        Assert.Equal("Living Room TV", devices[1].FriendlyName);
        Assert.True(devices[1].HasScreen);
        Assert.Equal(IPAddress.Parse("192.168.1.44"), devices[1].Address);

        Assert.True(CastDevice.TryParseManual("192.168.1.50", out var manual, out _));
        Assert.Equal(8009, manual!.Port);
        Assert.True(CastDevice.TryParseManual("192.168.1.50:8012", out var manualPort, out _));
        Assert.Equal(8012, manualPort!.Port);
        Assert.False(CastDevice.TryParseManual("192", out _, out _));
        Assert.False(CastDevice.TryParseManual("[fe80::1]", out _, out _));
        Assert.False(CastDevice.TryParseManual("192.168.1.50:99999", out _, out _));
    }

    [Fact]
    public async Task CastClient_HandshakeVolumeCustomMessageAndClose_WorkOverDuplexStream()
    {
        var pingReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeCastServer(onMessage: (msg, _) =>
        {
            if (msg.Namespace == CastProtocol.NsHeartbeat && msg.PayloadUtf8?.Contains("\"PING\"") == true)
                pingReceived.TrySetResult();
            if (msg.Namespace == CastProtocol.NsReceiver && msg.PayloadUtf8?.Contains("\"STOP\"") == true)
                stopReceived.TrySetResult();
            return Task.CompletedTask;
        });
        await using var client = new CastClient(server.OpenClientStreamAsync)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(40)
        };

        var device = new CastDevice("dev1", "Shop TV", "Chromecast", server.EndPoint.Address, server.EndPoint.Port);
        await client.ConnectAndLaunchAsync(device, "ABCDEF12");

        Assert.Equal("sess-42", client.SessionId);
        Assert.Equal("web-7", client.TransportId);
        Assert.Equal(0.65, client.VolumeLevel, precision: 2);

        var volTcs = new TaskCompletionSource<(double Lvl, bool Muted)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.VolumeChanged += (l, m) => volTcs.TrySetResult((l, m));

        await client.SendCustomJsonAsync(CastProtocol.NsNodeX, """{"type":"state","station":"KV4S · 523802"}""");
        await client.SetVolumeAsync(0.40);
        var volUpdate = await volTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0.40, volUpdate.Lvl, precision: 2);

        // Wait deterministically for a PING heartbeat, then stop and wait for STOP.
        await pingReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await client.StopAsync();
        await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));

        lock (server.Received)
        {
            Assert.Contains(server.Received, m => m.Namespace == CastProtocol.NsConnection && m.DestinationId == "receiver-0" && m.PayloadUtf8!.Contains("CONNECT"));
            Assert.Contains(server.Received, m => m.Namespace == CastProtocol.NsReceiver && m.PayloadUtf8!.Contains("\"LAUNCH\""));
            Assert.Contains(server.Received, m => m.Namespace == CastProtocol.NsConnection && m.DestinationId == "web-7" && m.PayloadUtf8!.Contains("CONNECT"));
            Assert.Contains(server.Received, m => m.Namespace == CastProtocol.NsNodeX && m.DestinationId == "web-7" && m.PayloadUtf8!.Contains("KV4S"));
            Assert.Contains(server.Received, m => m.Namespace == CastProtocol.NsHeartbeat && m.PayloadUtf8!.Contains("PING"));
            Assert.Contains(server.Received, m => m.Namespace == CastProtocol.NsReceiver && m.PayloadUtf8!.Contains("\"STOP\""));
        }
    }

    [Fact]
    public async Task CastClient_HandlesLaunchError_AndRemoteClose()
    {
        await using var badServer = new FakeCastServer(rejectLaunch: true);
        await using var badClient = new CastClient(badServer.OpenClientStreamAsync);
        var device = new CastDevice("dev1", "Shop TV", "Chromecast", badServer.EndPoint.Address, badServer.EndPoint.Port);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => badClient.ConnectAndLaunchAsync(device, "ABCDEF12"));
        Assert.Contains("NOT_ALLOWED", ex.Message);

        var closeSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeCastServer(onMessage: async (msg, stream) =>
        {
            if (msg.Namespace == CastProtocol.NsNodeX)
            {
                await CastProtocol.WriteFrameAsync(stream,
                    new CastMessage("web-7", "*", CastProtocol.NsConnection, CastPayloadType.String, """{"type":"CLOSE"}"""));
                closeSent.TrySetResult();
            }
        });
        await using var client = new CastClient(server.OpenClientStreamAsync);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += r => disconnected.TrySetResult(r);

        await client.ConnectAndLaunchAsync(new CastDevice("dev1", "TV", "Chromecast", server.EndPoint.Address, server.EndPoint.Port), "ABCDEF12");
        await client.SendCustomJsonAsync(CastProtocol.NsNodeX, """{"type":"ping"}""");
        await closeSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        string reason = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("closed", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CastAudioMixer_MixesClampsAndDetachesCleanly()
    {
        var mixer = new CastAudioMixer();
        short[] frameA = Enumerable.Repeat((short)22000, AudioFormat.SamplesPerFrame).ToArray();
        short[] frameB = Enumerable.Repeat((short)22000, AudioFormat.SamplesPerFrame).ToArray();
        mixer.PushFrame("100", frameA);
        mixer.PushFrame("200", frameB);

        short[] outBuf = new short[AudioFormat.SamplesPerFrame];
        Assert.True(mixer.MixNextFrame(outBuf));
        Assert.Equal(short.MaxValue, outBuf[0]); // 44000 clamped cleanly

        // Idle yields false and zeroes buffer
        Assert.False(mixer.MixNextFrame(outBuf));
        Assert.All(outBuf, s => Assert.Equal((short)0, s));

        // Detached link drops queued audio
        mixer.PushFrame("100", frameA);
        mixer.Detach("100");
        Assert.False(mixer.MixNextFrame(outBuf));
    }

    private sealed class StubVoiceChannel : IVoiceChannel
    {
        public bool IsTransmitting => false;
        public event Action<ushort, ReadOnlyMemory<byte>>? VoiceFrameReceived;
        public event Action<bool>? TransmittingChanged { add { } remove { } }
        public Task SendAudioFrameAsync(ReadOnlyMemory<byte> ulawFrame, CancellationToken ct) => Task.CompletedTask;
        public void Push(ushort ts, short sample)
        {
            byte[] ulaw = new byte[AudioFormat.SamplesPerFrame];
            short[] pcm = Enumerable.Repeat(sample, AudioFormat.SamplesPerFrame).ToArray();
            G711Ulaw.Encode(pcm, ulaw);
            VoiceFrameReceived?.Invoke(ts, ulaw);
        }
    }

    private sealed class RecordingAudioDevice : IAudioDeviceService
    {
        public List<short[]> Played { get; } = [];
        public bool IsCapturing { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool Disposed { get; private set; }
        public TimeSpan BufferedPlayback => TimeSpan.Zero;
        public event Action<string>? ErrorOccurred { add { } remove { } }
        public void StartCapture(Action<ReadOnlyMemory<short>> onBlock) => IsCapturing = true;
        public void StopCapture() => IsCapturing = false;
        public void StartPlayback() => IsPlaying = true;
        public void StopPlayback() => IsPlaying = false;
        public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm) => Played.Add(pcm.ToArray());
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task NodeAudioEngine_MuteLocalOutput_SuppressesSpeaker_WhileFiringProcessedCastAudio()
    {
        var ch = new StubVoiceChannel();
        var dev = new RecordingAudioDevice();
        await using var engine = new NodeAudioEngine(ch, dev, new NodeAudioEngineOptions { RxGain = 2.0f })
        {
            MuteLocalOutput = true
        };

        var processed = new List<short[]>();
        var decoded = new List<short[]>();
        engine.RxAudioProcessed += pcm => processed.Add(pcm.ToArray());
        engine.RxFrameDecoded += pcm => decoded.Add(pcm.ToArray());
        engine.Start();

        for (ushort i = 0; i < 4; i++)
            ch.Push((ushort)(i * 160), 1000);

        short[] buf = new short[AudioFormat.SamplesPerFrame];
        engine.PlayOneFrame(buf);

        Assert.Empty(dev.Played);            // local speaker stayed silent
        Assert.Single(decoded);              // Audio Replay unaffected
        Assert.Single(processed);            // Cast received processed audio
        Assert.True(processed[0][0] > decoded[0][0]); // RX gain 2x applied to Cast audio
    }

    [Fact]
    public async Task CastLanAudioServer_ServesAuthorizedWavAndIcon_AndRejectsWrongToken()
    {
        byte[] icon = [0x89, 0x50, 0x4E, 0x47];
        await using var server = new CastLanAudioServer(IPAddress.Loopback, icon, IPAddress.Loopback);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var bad = await http.GetAsync($"http://127.0.0.1:{server.Port}/cast/wrong/live.wav", cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);

        byte[] gotIcon = await http.GetByteArrayAsync(server.IconUrl, cts.Token);
        Assert.Equal(icon, gotIcon);

        using var resp = await http.GetAsync(server.LiveWavUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("audio/wav", resp.Content.Headers.ContentType?.MediaType);

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        byte[] header = new byte[44];
        await stream.ReadExactlyAsync(header, cts.Token);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(header, 8, 4));
        Assert.Equal(AudioFormat.SampleRate, (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)));

        short[] sampleFrame = Enumerable.Repeat((short)1234, AudioFormat.SamplesPerFrame).ToArray();
        server.PushMixedFrame(sampleFrame);

        byte[] pcmBytes = new byte[AudioFormat.SamplesPerFrame * 2];
        await stream.ReadExactlyAsync(pcmBytes, cts.Token);
        Assert.Equal(1234, BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.AsSpan(0, 2)));
    }

    [Fact]
    public void SettingsValidator_AndTransfer_PreserveAndValidateCastSettings()
    {
        var bad = new NodeSettings { NodeNumber = "523802", CastAppId = "NOT_HEX!" };
        Assert.Contains(NodeSettingsValidator.Validate(bad, "pw"), i => i.Field == NodeSettingsValidator.Fields.CastAppId);

        var raw = new NodeSettings { NodeNumber = "523802", CastAppId = " a1b2c3d4 ", CastMuteLocalSpeaker = false };
        var normalized = NodeSettingsValidator.Normalize(raw);
        Assert.Equal("A1B2C3D4", normalized.CastAppId);
        Assert.Empty(NodeSettingsValidator.Validate(normalized, "pw"));

        string exported = SettingsTransfer.Export(normalized);
        var imported = SettingsTransfer.TryImport(exported);
        Assert.True(imported.Success);
        Assert.Equal("A1B2C3D4", imported.Settings!.CastAppId);
        Assert.False(imported.Settings.CastMuteLocalSpeaker);
    }

    private static byte[] BuildMdnsPacket(
        (string Service, string Instance)? ptr = null,
        (string Instance, int Port, string Target)? srv = null,
        (string Instance, string[] Pairs)? txt = null,
        (string Host, IPAddress Ip)? a = null)
    {
        int count = (ptr != null ? 1 : 0) + (srv != null ? 1 : 0) + (txt != null ? 1 : 0) + (a != null ? 1 : 0);
        var buf = new List<byte>
        {
            0, 0, 0x84, 0,
            0, 0,
            (byte)(count >> 8), (byte)count,
            0, 0, 0, 0
        };

        void WriteName(string n)
        {
            foreach (string lbl in n.TrimEnd('.').Split('.'))
            {
                byte[] b = Encoding.UTF8.GetBytes(lbl);
                buf.Add((byte)b.Length);
                buf.AddRange(b);
            }
            buf.Add(0);
        }

        void WriteU16(ushort v) { buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        void WriteU32(uint v) { buf.Add((byte)(v >> 24)); buf.Add((byte)(v >> 16)); buf.Add((byte)(v >> 8)); buf.Add((byte)v); }

        if (ptr is { } p)
        {
            WriteName(p.Service);
            WriteU16(12); WriteU16(1); WriteU32(120);
            int rdLenAt = buf.Count; WriteU16(0);
            int start = buf.Count;
            WriteName(p.Instance);
            int rdLen = buf.Count - start;
            buf[rdLenAt] = (byte)(rdLen >> 8); buf[rdLenAt + 1] = (byte)rdLen;
        }
        if (srv is { } s)
        {
            WriteName(s.Instance);
            WriteU16(33); WriteU16(1); WriteU32(120);
            int rdLenAt = buf.Count; WriteU16(0);
            int start = buf.Count;
            WriteU16(0); WriteU16(0); WriteU16((ushort)s.Port);
            WriteName(s.Target);
            int rdLen = buf.Count - start;
            buf[rdLenAt] = (byte)(rdLen >> 8); buf[rdLenAt + 1] = (byte)rdLen;
        }
        if (txt is { } t)
        {
            WriteName(t.Instance);
            WriteU16(16); WriteU16(1); WriteU32(120);
            int rdLenAt = buf.Count; WriteU16(0);
            int start = buf.Count;
            foreach (string kv in t.Pairs)
            {
                byte[] b = Encoding.UTF8.GetBytes(kv);
                buf.Add((byte)b.Length);
                buf.AddRange(b);
            }
            int rdLen = buf.Count - start;
            buf[rdLenAt] = (byte)(rdLen >> 8); buf[rdLenAt + 1] = (byte)rdLen;
        }
        if (a is { } rec)
        {
            WriteName(rec.Host);
            WriteU16(1); WriteU16(1); WriteU32(120);
            WriteU16(4);
            buf.AddRange(rec.Ip.GetAddressBytes());
        }
        return buf.ToArray();
    }
}
