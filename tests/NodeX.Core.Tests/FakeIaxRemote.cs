using System.Net;
using System.Net.Sockets;
using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

/// <summary>
/// Scripted fake remote node on a loopback UDP socket, used to drive a real <see cref="Iax2Session"/>
/// through handshakes and frame exchanges. It tracks sequence numbers like a real peer: its frames
/// carry an ISeqno that acknowledges what the session has sent.
/// </summary>
internal sealed class FakeIaxRemote : IDisposable
{
    public const ushort RemoteCall = 77;

    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private IPEndPoint? _session;
    private ushort _sessionCall;
    private byte _oseq;
    private byte _iseq;

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>Where the session under test sends from (learned from its first packet).</summary>
    public IPEndPoint SessionEndPoint => _session!;

    /// <summary>Call number the session under test chose for itself (learned from its first frame).</summary>
    public ushort SessionCall => _sessionCall;

    /// <summary>OSeqno this fake will use for its next sequenced frame.</summary>
    public byte NextOutboundSeq => _oseq;

    /// <summary>True when the most recently received frame had the R (retransmit) bit set.</summary>
    public bool LastWasRetransmit { get; private set; }

    /// <summary>Receives the next full frame (mini frames are skipped).</summary>
    public async Task<Iax2Frame> ReceiveFrameAsync(int timeoutMs = 10000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var r = await _udp.ReceiveAsync(cts.Token);
            _session = r.RemoteEndPoint;
            if (TryParseFrame(r.Buffer, out var f) && f.IsFullFrame)
            {
                // A NEW from a different call number is a fresh call (for example a reconnect): start
                // a fresh sequence space, as a real peer would.
                if (f.FrameType == FrameType.IaxControl && f.Subclass == (byte)IaxControlSubclass.New
                    && f.SourceCallNumber != _sessionCall)
                {
                    _oseq = 0;
                    _iseq = 0;
                }

                _sessionCall = f.SourceCallNumber;
                LastWasRetransmit = (r.Buffer[2] & 0x80) != 0;

                // Mimic a real peer: count only in-order reliable frames toward our ISeqno.
                if (Iax2Sequencing.IsReliable(f.FrameType, f.Subclass) && f.OSeqno == _iseq) _iseq++;
                return f;
            }
        }
    }

    /// <summary>Reads frames until one matches, skipping the rest (ACKs and so on).</summary>
    public async Task<Iax2Frame> WaitForFrameAsync(Func<Iax2Frame, bool> match, int timeoutMs = 10000)
    {
        while (true)
        {
            var f = await ReceiveFrameAsync(timeoutMs);
            if (match(f)) return f;
        }
    }

    public Task<Iax2Frame> WaitForIaxAsync(IaxControlSubclass subclass, int timeoutMs = 10000) =>
        WaitForFrameAsync(f => f.FrameType == FrameType.IaxControl && f.Subclass == (byte)subclass, timeoutMs);

    public Task<Iax2Frame> WaitForPingAsync(int timeoutMs = 10000) => WaitForIaxAsync(IaxControlSubclass.Ping, timeoutMs);

    /// <summary>Discards everything queued for us until the socket has been quiet for <paramref name="quietMs"/>.</summary>
    public async Task DrainAsync(int quietMs = 100)
    {
        try
        {
            while (true) await ReceiveFrameAsync(quietMs);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Sends a frame with the next sequence number (consumed only for reliable frames).</summary>
    public Task SendAsync(FrameType type, byte subclass, uint timestamp = 0, byte[]? payload = null)
    {
        byte seq = _oseq;
        if (Iax2Sequencing.IsReliable(type, subclass)) _oseq++;
        return SendRawAsync(type, subclass, seq, timestamp: timestamp, payload: payload);
    }

    /// <summary>Sends a frame with fully explicit header fields (for duplicates, gaps and spoofing).</summary>
    public async Task SendRawAsync(
        FrameType type, byte subclass, byte oseq,
        byte? iseq = null, uint timestamp = 0, byte[]? payload = null,
        ushort? dest = null, ushort? source = null)
    {
        payload ??= [];
        byte[] buf = new byte[FullHeaderSize + payload.Length];
        WriteFullFrame(buf, source ?? RemoteCall, dest ?? _sessionCall, timestamp, oseq, iseq ?? _iseq, type, subclass, payload);
        await _udp.SendAsync(buf, _session!);
    }

    public Task SendIaxAsync(IaxControlSubclass subclass, uint timestamp = 0) =>
        SendAsync(FrameType.IaxControl, (byte)subclass, timestamp);

    public Task SendControlAsync(ControlSubclass subclass) =>
        SendAsync(FrameType.Control, (byte)subclass);

    /// <summary>Starts a fresh sequence space (as a stateless CALLTOKEN challenge does).</summary>
    public void ResetSequence()
    {
        _oseq = 0;
        _iseq = 0;
    }

    /// <summary>Waits for the session's NEW and answers ACCEPT.</summary>
    public async Task AcceptCallAsync(int timeoutMs = 10000)
    {
        var neu = await ReceiveFrameAsync(timeoutMs);
        Assert.Equal((byte)IaxControlSubclass.New, neu.Subclass);
        await SendIaxAsync(IaxControlSubclass.Accept);
    }

    /// <summary>Creates a session, dials this remote and completes the ACCEPT handshake.</summary>
    public async Task<Iax2Session> ConnectSessionAsync(
        TimeSpan? keepAlive = null, int maxMissed = 3, Action<Iax2Session>? configure = null)
    {
        var s = new Iax2Session
        {
            LocalNodeNumber = "1234",
            RemoteNodeNumber = "2000",
            KeepAliveInterval = keepAlive ?? TimeSpan.FromSeconds(15),
            MaxMissedKeepAlives = maxMissed
        };
        configure?.Invoke(s);

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.StateChanged += st => { if (st == Iax2SessionState.Connected) connected.TrySetResult(); };

        await s.ConnectAsync(EndPoint);
        await AcceptCallAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return s;
    }

    public void Dispose() => _udp.Dispose();
}
