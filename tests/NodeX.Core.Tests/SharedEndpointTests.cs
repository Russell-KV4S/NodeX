using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

/// <summary>
/// The shared call socket (field report 2026-11-19: far nodes refused links because the registration and the call
/// left a carrier NAT through different public addresses). Calls and the IAX2 registration must leave from one
/// socket, and calls on it must not hear each other.
/// </summary>
public class SharedEndpointTests
{
    private static async Task<Iax2Session> DialAsync(Iax2Endpoint endpoint, FakeIaxRemote remote, string node)
    {
        var s = new Iax2Session(endpoint) { LocalNodeNumber = "1234", RemoteNodeNumber = node, KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.StateChanged += st => { if (st == Iax2SessionState.Connected) connected.TrySetResult(); };
        await s.ConnectAsync(remote.EndPoint);
        await remote.AcceptCallAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return s;
    }

    [Fact]
    public async Task TwoCalls_LeaveFromTheSameSocket_AndOnlyHearTheirOwnNode()
    {
        using var endpoint = new Iax2Endpoint();
        using var a = new FakeIaxRemote();
        using var b = new FakeIaxRemote();

        await using var callA = await DialAsync(endpoint, a, "2000");
        await using var callB = await DialAsync(endpoint, b, "3000");

        Assert.True(callA.UsesSharedEndpoint);
        Assert.Equal(endpoint.LocalPort, a.SessionEndPoint.Port);
        Assert.Equal(endpoint.LocalPort, b.SessionEndPoint.Port);
        Assert.NotEqual(a.SessionCall, b.SessionCall);          // distinct call numbers on one socket

        int heardA = 0, heardB = 0;
        callA.VoiceFrameReceived += (_, _) => Interlocked.Increment(ref heardA);
        callB.VoiceFrameReceived += (_, _) => Interlocked.Increment(ref heardB);

        await a.SendAsync(FrameType.Voice, (byte)CodecFormat.Ulaw, payload: new byte[160]);
        await Wait.UntilAsync(() => Volatile.Read(ref heardA) == 1);
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref heardB));
    }

    [Fact]
    public async Task RetiredEndpoint_StaysOpenForItsCall_AndClosesWhenTheCallEnds()
    {
        var endpoint = new Iax2Endpoint();
        using var remote = new FakeIaxRemote();
        var call = await DialAsync(endpoint, remote, "2000");

        endpoint.Retire();                         // network changed: no new users
        Assert.False(endpoint.IsDisposed);         // the running call keeps it

        call.HangupAckWait = TimeSpan.Zero;
        await call.DisposeAsync();
        Assert.True(endpoint.IsDisposed);
    }

    [Fact]
    public async Task SessionOnAClosedEndpoint_FallsBackToItsOwnSocket()
    {
        var endpoint = new Iax2Endpoint();
        endpoint.Dispose();
        using var remote = new FakeIaxRemote();

        await using var call = await DialAsync(endpoint, remote, "2000");
        Assert.False(call.UsesSharedEndpoint);
        Assert.Equal(Iax2SessionState.Connected, call.State);
    }

    [Fact]
    public async Task Registrar_RegistersFromTheCallSocket()
    {
        using var endpoint = new Iax2Endpoint();
        using var registry = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var registryEp = (IPEndPoint)registry.Client.LocalEndPoint!;
        using var remote = new FakeIaxRemote();

        await using var call = await DialAsync(endpoint, remote, "2000");
        await using var reg = new Iax2Registrar("55553", "pw", endpoint, registryEp) { ReplyTimeout = TimeSpan.FromSeconds(2) };
        var registering = reg.RegisterOnceAsync();

        var r = await registry.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(TryParseFrame(r.Buffer, out var req));
        Assert.Equal((byte)IaxControlSubclass.RegReq, req.Subclass);
        Assert.Equal(remote.SessionEndPoint.Port, r.RemoteEndPoint.Port);   // same socket as the call

        byte[] ack = new byte[FullHeaderSize];
        WriteFullFrame(ack, 9, req.SourceCallNumber, 0, 0, 1, FrameType.IaxControl, (byte)IaxControlSubclass.RegAck);
        await registry.SendAsync(ack, r.RemoteEndPoint);

        Assert.True(await registering);
        Assert.Equal("IAX2", reg.Transport);
        Assert.NotNull(reg.LastSuccessfulHeartbeat);
        Assert.Equal(Iax2SessionState.Connected, call.State);               // the registration did not disturb the call
    }

    [Fact]
    public async Task Registrar_ResendsALostRequest()
    {
        using var registry = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var registryEp = (IPEndPoint)registry.Client.LocalEndPoint!;
        await using var reg = new Iax2Registrar("55553", "pw", registryEp) { ReplyTimeout = TimeSpan.FromMilliseconds(300) };
        var registering = reg.RegisterOnceAsync();

        await registry.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));             // "lost": not answered
        var again = await registry.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5)); // the re-send
        Assert.True(TryParseFrame(again.Buffer, out var req));
        byte[] ack = new byte[FullHeaderSize];
        WriteFullFrame(ack, 9, req.SourceCallNumber, 0, 0, 1, FrameType.IaxControl, (byte)IaxControlSubclass.RegAck);
        await registry.SendAsync(ack, again.RemoteEndPoint);

        Assert.True(await registering);
    }
}

/// <summary>IAX2 first, HTTPS only when IAX2 has never worked on this network.</summary>
public class FallbackRegistrationTests
{
    private sealed class FakeHttps : INodeRegistration
    {
        public bool Started;
        public bool IsRegistered { get; private set; }
        public DateTime? LastSuccessfulHeartbeat { get; private set; }
        public string LastStatusMessage { get; private set; } = "";
        public IPAddress? PerceivedAddress => IPAddress.Parse("198.51.100.7");
        public int PerceivedPort => 4569;
        public TimeSpan CurrentInterval => TimeSpan.FromSeconds(60);
        public string Transport => "HTTPS";
        public event Action<bool, string>? RegistrationStatusChanged;
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            IsRegistered = true;
            LastSuccessfulHeartbeat = DateTime.UtcNow;
            LastStatusMessage = "Registered as 198.51.100.7:4569";
            RegistrationStatusChanged?.Invoke(true, LastStatusMessage);
            return Task.CompletedTask;
        }
        public Task StopAsync() => Task.CompletedTask;
        public Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsRegistered);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NoIaxReply_SwitchesToHttps()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));   // a registry that never answers
        var iax = new Iax2Registrar("55553", "pw", (IPEndPoint)silent.Client.LocalEndPoint!)
        {
            ReplyTimeout = TimeSpan.FromMilliseconds(50),
            Retries = 0,
            RetryDelay = TimeSpan.FromMilliseconds(50)
        };
        var https = new FakeHttps();
        await using var reg = new FallbackRegistration(iax, () => https);
        string? reason = null;
        reg.FellBack += r => reason = r;

        await reg.StartAsync();
        await Wait.UntilAsync(() => reg.IsRegistered);

        Assert.True(https.Started);
        Assert.True(reg.UsingFallback);
        Assert.Equal("HTTPS", reg.Transport);
        Assert.Contains("No reply", reason);
    }

    [Fact]
    public async Task AfterFallingBack_IaxIsTriedAgain_AndTakesOverWhenItWorks()
    {
        // Phone log 2026-11-19: IAX2 got no reply in the first 17 s after start and NodeX stayed on HTTPS for good.
        using var registry = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var iax = new Iax2Registrar("55553", "pw", (IPEndPoint)registry.Client.LocalEndPoint!)
        {
            ReplyTimeout = TimeSpan.FromMilliseconds(50),
            Retries = 0,
            RetryDelay = TimeSpan.FromMilliseconds(50)
        };
        var https = new FakeHttps();
        await using var reg = new FallbackRegistration(iax, () => https) { IaxRetryInterval = TimeSpan.FromMilliseconds(300) };
        bool recovered = false;
        reg.Recovered += () => recovered = true;

        await reg.StartAsync();
        await Wait.UntilAsync(() => reg.UsingFallback && reg.IsRegistered);     // registry silent: HTTPS

        // The registry starts answering (the network came up).
        var answering = Task.Run(async () =>
        {
            while (true)
            {
                var r = await registry.ReceiveAsync();
                if (!TryParseFrame(r.Buffer, out var req) || req.Subclass != (byte)IaxControlSubclass.RegReq) continue;
                byte[] ack = new byte[FullHeaderSize];
                WriteFullFrame(ack, 9, req.SourceCallNumber, 0, 0, 1, FrameType.IaxControl, (byte)IaxControlSubclass.RegAck);
                await registry.SendAsync(ack, r.RemoteEndPoint);
            }
        });

        await Wait.UntilAsync(() => recovered);
        Assert.False(reg.UsingFallback);
        Assert.Equal("IAX2", reg.Transport);
        Assert.True(reg.IsRegistered);
    }

    [Fact]
    public async Task IaxThatWorked_IsNotAbandonedForOneLostReply()
    {
        using var registry = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var iax = new Iax2Registrar("55553", "pw", (IPEndPoint)registry.Client.LocalEndPoint!)
        {
            ReplyTimeout = TimeSpan.FromMilliseconds(100),
            Retries = 0
        };
        var https = new FakeHttps();
        await using var reg = new FallbackRegistration(iax, () => https);

        var answering = Task.Run(async () =>
        {
            var r = await registry.ReceiveAsync();
            TryParseFrame(r.Buffer, out var req);
            byte[] ack = new byte[FullHeaderSize];
            WriteFullFrame(ack, 9, req.SourceCallNumber, 0, 0, 1, FrameType.IaxControl, (byte)IaxControlSubclass.RegAck);
            await registry.SendAsync(ack, r.RemoteEndPoint);
        });
        Assert.True(await reg.SendHeartbeatAsync());
        await answering;

        Assert.False(await reg.SendHeartbeatAsync());     // the registry goes quiet once...
        Assert.False(await reg.SendHeartbeatAsync());     // ...twice
        Assert.False(reg.UsingFallback);                  // IAX2 has worked here, so a lost reply is a blip
        Assert.False(https.Started);
    }
}

/// <summary>The STUN network check that tells a consistent carrier from one that changes address per destination.</summary>
public class NetworkCheckTests
{
    [Fact]
    public void ParsesXorMappedAddress()
    {
        byte[] txn = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        byte[] msg = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(msg, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), 0x2112A442);
        txn.CopyTo(msg, 8);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), 8);
        msg[25] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(26), (ushort)(51490 ^ 0x2112));
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(28), 0x4A_FE_4E_13 ^ 0x2112A442);   // 74.254.78.19

        Assert.True(NetworkCheck.TryParseBindingResponse(msg, txn, out var mapped));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("74.254.78.19"), 51490), mapped);

        byte[] otherTxn = new byte[12];
        Assert.False(NetworkCheck.TryParseBindingResponse(msg, otherTxn, out _));   // not our transaction
    }

    [Theory]
    [InlineData("74.254.84.17:1000", "74.254.84.17:1000", null, NatVerdict.Consistent)]
    [InlineData("74.254.84.17:1000", "74.254.84.17:2000", "74.254.84.17", NatVerdict.Consistent)]  // port may differ
    [InlineData("74.254.78.19:1000", "74.254.84.17:1000", null, NatVerdict.AddressPerDestination)]  // the field case
    [InlineData("74.254.84.17:1000", "74.254.84.17:1000", "68.198.148.31", NatVerdict.RegistryMismatch)]
    [InlineData(null, null, null, NatVerdict.Unknown)]
    public void Verdicts(string? first, string? second, string? registry, NatVerdict expected)
    {
        IPEndPoint?[] mapped = [first == null ? null : IPEndPoint.Parse(first), second == null ? null : IPEndPoint.Parse(second)];
        Assert.Equal(expected, NetworkCheck.Judge(mapped, registry == null ? null : IPAddress.Parse(registry), "IAX2"));
    }

    [Fact]
    public async Task RunsAgainstStunServers_FromTheCallSocket()
    {
        using var endpoint = new Iax2Endpoint();
        using var stunA = new FakeStun();
        using var stunB = new FakeStun();

        var result = await NetworkCheck.RunAsync(endpoint, IPAddress.Loopback, "IAX2",
            [("127.0.0.1", stunA.Port), ("127.0.0.1", stunB.Port)], TimeSpan.FromSeconds(1));

        Assert.Equal(NatVerdict.Consistent, result.Verdict);
        Assert.All(result.Mapped, m => Assert.Equal(endpoint.LocalPort, m.Mapped!.Port));
        Assert.Null(result.UserWarning);
    }

    /// <summary>Answers STUN Binding requests with the sender's address, like a real server.</summary>
    private sealed class FakeStun : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public FakeStun() => _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await _udp.ReceiveAsync(_cts.Token);
                    byte[] reply = new byte[32];
                    BinaryPrimitives.WriteUInt16BigEndian(reply, 0x0101);
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), 12);
                    r.Buffer.AsSpan(4, 16).CopyTo(reply.AsSpan(4));     // cookie + transaction id
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(20), 0x0020);
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(22), 8);
                    reply[25] = 0x01;
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(26), (ushort)(r.RemoteEndPoint.Port ^ 0x2112));
                    uint addr = BinaryPrimitives.ReadUInt32BigEndian(r.RemoteEndPoint.Address.GetAddressBytes());
                    BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(28), addr ^ 0x2112A442);
                    await _udp.SendAsync(reply, r.RemoteEndPoint);
                }
            }
            catch (Exception) { /* closed */ }
        });

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
        }
    }
}
