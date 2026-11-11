using System.Net;
using System.Net.Sockets;
using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

/// <summary>Loopback tests of reliable delivery, sequencing, handshake and sender validation.</summary>
public class Iax2ReliabilityTests
{
    private sealed class KeyEvents
    {
        private readonly List<bool> _events = [];
        public KeyEvents(Iax2Session s) => s.RemoteKeyedChanged += k => { lock (_events) _events.Add(k); };
        public bool[] Snapshot() { lock (_events) return [.. _events]; }

        public async Task WaitForCountAsync(int n, int timeoutMs = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Snapshot().Length < n)
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Expected {n} key events, got {Snapshot().Length}");
                await Task.Delay(5);
            }
        }
    }

    private static bool IsKey(Iax2Frame f) =>
        f.FrameType == FrameType.Control && f.Subclass == (byte)ControlSubclass.AstControlKey;

    private static Task Settle() => Task.Delay(150);

    // ----- handshake -----

    [Fact]
    public async Task FirstNew_AdvertisesCallTokenSupport_WithEmptyIe()
    {
        using var remote = new FakeIaxRemote();
        using var s = new Iax2Session { LocalNodeNumber = "1234", RemoteNodeNumber = "2000" };
        await s.ConnectAsync(remote.EndPoint);

        var neu = await remote.ReceiveFrameAsync();

        Assert.Equal((byte)IaxControlSubclass.New, neu.Subclass);
        var token = ParseInformationElements(neu.Payload).Single(i => i.Id == InformationElementId.CallToken);
        Assert.Empty(token.Data);
    }

    [Fact]
    public async Task CallTokenChallenge_ResendsNewWithToken_InFreshSequenceSpace_ThenConnects()
    {
        using var remote = new FakeIaxRemote();
        using var s = new Iax2Session { LocalNodeNumber = "1234", RemoteNodeNumber = "2000" };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.StateChanged += st => { if (st == Iax2SessionState.Connected) connected.TrySetResult(); };

        await s.ConnectAsync(remote.EndPoint);
        var first = await remote.ReceiveFrameAsync();
        Assert.Equal(0, first.OSeqno);

        byte[] token = [1, 2, 3, 4];
        byte[] ie = new byte[16];
        int n = WriteInformationElements(ie, [Iax2InformationElement.Raw(InformationElementId.CallToken, token)]);
        remote.ResetSequence(); // the challenge is stateless on the server side
        await remote.SendRawAsync(FrameType.IaxControl, (byte)IaxControlSubclass.CallToken,
            oseq: 0, iseq: 1, payload: ie[..n]);

        var second = await remote.WaitForIaxAsync(IaxControlSubclass.New);
        Assert.Equal(0, second.OSeqno);
        Assert.Equal(0, second.ISeqno);
        var echoed = ParseInformationElements(second.Payload).Single(i => i.Id == InformationElementId.CallToken);
        Assert.Equal(token, echoed.Data);

        await remote.SendIaxAsync(IaxControlSubclass.Accept);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Iax2SessionState.Connected, s.State);
    }

    // ----- reliable delivery -----

    [Fact]
    public async Task LostKeyFrame_IsRetransmittedWithRBit_UntilAcknowledged()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync(
            configure: s => s.InitialRetransmitTimeout = TimeSpan.FromMilliseconds(50));

        await session.SetPttAsync(true);
        var original = await remote.WaitForFrameAsync(IsKey);
        Assert.False(remote.LastWasRetransmit);

        var resent = await remote.WaitForFrameAsync(IsKey);   // we did not acknowledge the first one
        Assert.True(remote.LastWasRetransmit);
        Assert.Equal(original.OSeqno, resent.OSeqno);

        // Any frame from the peer carrying the right ISeqno acknowledges it.
        await remote.SendControlAsync(ControlSubclass.Option);
        await remote.DrainAsync(150);

        int extra = 0;
        try
        {
            while (true)
            {
                if (IsKey(await remote.ReceiveFrameAsync(400))) extra++;
            }
        }
        catch (OperationCanceledException) { }
        Assert.Equal(0, extra);
    }

    [Fact]
    public async Task UnacknowledgedFrame_ExceedingRetryLimit_DisconnectsWithError()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync(configure: s =>
        {
            s.InitialRetransmitTimeout = TimeSpan.FromMilliseconds(30);
            s.MaxRetransmits = 2;
        });

        var dead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? error = null;
        session.ErrorOccurred += e => error = e;
        session.StateChanged += st => { if (st == Iax2SessionState.Disconnected) dead.TrySetResult(); };

        await session.SetPttAsync(true); // remote stays silent from here on

        await dead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("acknowledgment", error);
    }

    // ----- inbound sequencing -----

    [Fact]
    public async Task FutureFrame_TriggersVnak_AndIsNotProcessed_UntilGapIsFilled()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();
        var keys = new KeyEvents(session);

        byte expected = remote.NextOutboundSeq;           // 1: ACCEPT consumed 0
        await remote.SendRawAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey,
            oseq: (byte)(expected + 1));                  // skips one frame

        var vnak = await remote.WaitForIaxAsync(IaxControlSubclass.Vnak);
        Assert.Equal(expected, vnak.ISeqno);
        await Settle();
        Assert.Empty(keys.Snapshot());

        // Fill the gap, then the previously dropped frame is accepted in order.
        await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.Option);
        await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey);
        await keys.WaitForCountAsync(1);
        Assert.Equal([true], keys.Snapshot());
    }

    [Fact]
    public async Task DuplicateFrame_IsAcknowledgedAgain_ButProcessedOnce()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();
        var keys = new KeyEvents(session);

        byte seq = remote.NextOutboundSeq;
        await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey);
        await keys.WaitForCountAsync(1);
        await remote.DrainAsync(100);                     // discard the first ACK

        await remote.SendRawAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey, oseq: seq); // retransmission
        await remote.WaitForIaxAsync(IaxControlSubclass.Ack);                                        // re-ACKed

        await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.AstControlUnkey);
        await keys.WaitForCountAsync(2);
        Assert.Equal([true, false], keys.Snapshot());
    }

    [Fact]
    public async Task SequenceNumbers_WrapPast255_WithoutDroppingFrames()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();
        var keys = new KeyEvents(session);

        for (int i = 0; i < 300; i++)
        {
            await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.Option);
            if (i % 50 == 49) await Task.Delay(20); // keep the loopback buffer from overflowing
        }
        await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey);

        await keys.WaitForCountAsync(1);
        Assert.Equal(Iax2SessionState.Connected, session.State);
    }

    // ----- sender / call-number validation -----

    [Fact]
    public async Task PacketsFromAnotherEndpoint_AreIgnored()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();
        var keys = new KeyEvents(session);

        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] Forge(FrameType t, byte sub)
        {
            byte[] b = new byte[FullHeaderSize];
            WriteFullFrame(b, FakeIaxRemote.RemoteCall, remote.SessionCall, 0, remote.NextOutboundSeq, 0, t, sub);
            return b;
        }

        await attacker.SendAsync(Forge(FrameType.Control, (byte)ControlSubclass.AstControlKey), remote.SessionEndPoint);
        await attacker.SendAsync(Forge(FrameType.IaxControl, (byte)IaxControlSubclass.Hangup), remote.SessionEndPoint);
        await Settle();

        Assert.Empty(keys.Snapshot());
        Assert.Equal(Iax2SessionState.Connected, session.State);

        await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey); // genuine peer still works
        await keys.WaitForCountAsync(1);
        Assert.Equal([true], keys.Snapshot());
    }

    [Fact]
    public async Task FramesForAnotherCall_OrFromAnotherCallNumber_AreIgnored()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();
        var keys = new KeyEvents(session);
        byte seq = remote.NextOutboundSeq;

        await remote.SendRawAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey,
            oseq: seq, dest: (ushort)(remote.SessionCall + 1));            // addressed to someone else
        await remote.SendRawAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey,
            oseq: seq, source: (ushort)(FakeIaxRemote.RemoteCall + 1));    // not our peer's call number
        await Settle();
        Assert.Empty(keys.Snapshot());

        await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.AstControlKey);
        await keys.WaitForCountAsync(1);
        Assert.Equal([true], keys.Snapshot());
    }
}
