using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

/// <summary>
/// Things that must not be able to wreck a call: a listener that throws, frames that arrive after the call has
/// ended, a stray call-token challenge, a HANGUP that gets lost, and an authentication request we cannot answer.
/// </summary>
public class Iax2SessionRobustnessTests
{
    private static bool IsHangup(Iax2Frame f) =>
        f.FrameType == FrameType.IaxControl && f.Subclass == (byte)IaxControlSubclass.Hangup;

    private static Task Settle() => Task.Delay(150);

    /// <summary>The session answers a PING only if its receive loop is still running.</summary>
    private static async Task AssertStillAnsweringAsync(FakeIaxRemote remote)
    {
        await remote.SendIaxAsync(IaxControlSubclass.Ping, timestamp: 4242);
        var pong = await remote.WaitForIaxAsync(IaxControlSubclass.Pong);
        Assert.Equal(4242u, pong.Timestamp);
    }

    private static async Task<int> CountHangupsUntilQuietAsync(FakeIaxRemote remote, int quietMs = 200)
    {
        int hangups = 0;
        try
        {
            while (true)
            {
                if (IsHangup(await remote.ReceiveFrameAsync(quietMs))) hangups++;
            }
        }
        catch (OperationCanceledException) { }
        return hangups;
    }

    // ----- listeners that throw -----

    [Fact]
    public async Task ListenerThatThrows_CostsOneFrame_NotTheReceiveLoop()
    {
        using var remote = new FakeIaxRemote();
        await using var s = await remote.ConnectSessionAsync();
        var errors = new List<string>();
        s.ErrorOccurred += m => { lock (errors) errors.Add(m); };
        int texts = 0;
        s.NodeMessageReceived += _ =>
        {
            Interlocked.Increment(ref texts);
            throw new InvalidOperationException("listener bug");
        };

        await remote.SendAsync(FrameType.Text, 0, payload: Encoding.ASCII.GetBytes("T 2000 STATUS\0"));
        await Wait.UntilAsync(() => Volatile.Read(ref texts) == 1);

        // The loop is still there: it answers a PING and delivers the next text frame.
        await AssertStillAnsweringAsync(remote);
        await remote.SendAsync(FrameType.Text, 0, payload: Encoding.ASCII.GetBytes("L \0"));
        await Wait.UntilAsync(() => Volatile.Read(ref texts) == 2);

        Assert.Equal(Iax2SessionState.Connected, s.State);
        lock (errors) Assert.Contains(errors, e => e.Contains("frame handling error") && e.Contains("listener bug"));
    }

    [Fact]
    public async Task StateListenerThatThrows_DoesNotHideTheChangeFromOtherListeners()
    {
        using var remote = new FakeIaxRemote();
        await using var s = new Iax2Session { LocalNodeNumber = "1234", RemoteNodeNumber = "2000" };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        s.StateChanged += _ => throw new InvalidOperationException("listener bug");   // subscribed first
        s.StateChanged += st =>
        {
            if (st == Iax2SessionState.Connected) connected.TrySetResult();
            if (st == Iax2SessionState.Disconnected) ended.TrySetResult();
        };

        await s.ConnectAsync(remote.EndPoint);
        await remote.AcceptCallAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await AssertStillAnsweringAsync(remote);

        // The one that matters most: the runtime cleans a link up when it hears Disconnected.
        await remote.SendIaxAsync(IaxControlSubclass.Hangup);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Iax2DisconnectReason.RemoteHangup, s.DisconnectReason);
    }

    // ----- a call that has ended stays ended -----

    [Fact]
    public async Task FramesAfterTheCallEnded_CannotBringItBack_OrReportItTwice()
    {
        using var remote = new FakeIaxRemote();
        await using var s = await remote.ConnectSessionAsync();
        var states = new List<Iax2SessionState>();
        s.StateChanged += st => { lock (states) states.Add(st); };
        int voice = 0;
        s.VoiceFrameReceived += (_, _) => Interlocked.Increment(ref voice);

        await remote.SendIaxAsync(IaxControlSubclass.Hangup);
        await Wait.UntilAsync(() => s.State == Iax2SessionState.Disconnected);

        // Late or repeated frames from the far end: an ANSWER, an ACCEPT, some audio.
        await remote.SendControlAsync(ControlSubclass.Answer);
        await remote.SendIaxAsync(IaxControlSubclass.Accept);
        await remote.SendAsync(FrameType.Voice, (byte)CodecFormat.Ulaw, payload: new byte[160]);
        await Settle();

        Assert.Equal(Iax2SessionState.Disconnected, s.State);
        Assert.Equal(Iax2DisconnectReason.RemoteHangup, s.DisconnectReason);
        Assert.Equal(0, Volatile.Read(ref voice));

        // Our own hang-up afterwards changes nothing either (first reason wins, one Disconnected event).
        await s.DisconnectAsync();
        Assert.Equal(Iax2DisconnectReason.RemoteHangup, s.DisconnectReason);
        lock (states) Assert.Equal([Iax2SessionState.Disconnected], states);
    }

    [Fact]
    public async Task Session_IsSingleUse_AfterItHasEnded()
    {
        using var remote = new FakeIaxRemote();
        await using var s = await remote.ConnectSessionAsync(configure: x => x.HangupAckWait = TimeSpan.Zero);
        await s.DisconnectAsync();
        Assert.Equal(Iax2SessionState.Disconnected, s.State);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.ConnectAsync(remote.EndPoint));
        Assert.Contains("single-use", ex.Message);
        Assert.Equal(Iax2SessionState.Disconnected, s.State);
        Assert.Equal(Iax2DisconnectReason.LocalRequest, s.DisconnectReason);
    }

    [Fact]
    public async Task CallTokenChallenge_InTheMiddleOfACall_IsIgnored()
    {
        using var remote = new FakeIaxRemote();
        await using var s = await remote.ConnectSessionAsync();
        await remote.DrainAsync();

        // CALLTOKEN is exempt from the call-number check (it is stateless), so it must be refused on state:
        // acting on it mid-call would reset the sequence numbers and send a second NEW.
        byte[] token = [(byte)InformationElementId.CallToken, 4, 1, 2, 3, 4];
        await remote.SendRawAsync(FrameType.IaxControl, (byte)IaxControlSubclass.CallToken, oseq: 0, payload: token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remote.WaitForIaxAsync(IaxControlSubclass.New, 400));
        Assert.Equal(Iax2SessionState.Connected, s.State);
        await AssertStillAnsweringAsync(remote);
    }

    // ----- HANGUP is delivered, not just sent -----

    [Fact]
    public async Task Hangup_IsResentUntilAcknowledged_ThenTheCallEndsAtOnce()
    {
        using var remote = new FakeIaxRemote();
        await using var s = await remote.ConnectSessionAsync(configure: x => x.HangupAckWait = TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();

        var hangingUp = s.DisconnectAsync();

        var first = await remote.WaitForFrameAsync(IsHangup);
        Assert.False(remote.LastWasRetransmit);
        var second = await remote.WaitForFrameAsync(IsHangup);          // the first one "was lost": it comes again
        Assert.True(remote.LastWasRetransmit);
        Assert.Equal(first.OSeqno, second.OSeqno);
        Assert.False(hangingUp.IsCompleted);
        Assert.Equal(Iax2SessionState.Disconnecting, s.State);

        await remote.SendIaxAsync(IaxControlSubclass.Ack);
        await hangingUp.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");    // did not sit out the 10 s
        Assert.Equal(Iax2SessionState.Disconnected, s.State);
        Assert.Equal(Iax2DisconnectReason.LocalRequest, s.DisconnectReason);
    }

    [Fact]
    public async Task Hangup_NeverAcknowledged_StillEndsTheCall_AfterAShortBoundedWait()
    {
        using var remote = new FakeIaxRemote();
        await using var s = await remote.ConnectSessionAsync(configure: x => x.HangupAckWait = TimeSpan.FromMilliseconds(350));
        var sw = Stopwatch.StartNew();

        await s.DisconnectAsync();

        Assert.InRange(sw.ElapsedMilliseconds, 300, 3000);
        Assert.Equal(Iax2SessionState.Disconnected, s.State);
        Assert.Equal(Iax2DisconnectReason.LocalRequest, s.DisconnectReason);
        Assert.InRange(await CountHangupsUntilQuietAsync(remote), 2, 6);     // sent, then re-sent about every 100 ms
    }

    [Fact]
    public async Task HangupAckWaitZero_SendsHangupOnce_AndDoesNotWait()
    {
        using var remote = new FakeIaxRemote();
        await using var s = await remote.ConnectSessionAsync(configure: x => x.HangupAckWait = TimeSpan.Zero);

        await s.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(Iax2SessionState.Disconnected, s.State);
        Assert.Equal(1, await CountHangupsUntilQuietAsync(remote, quietMs: 350));
    }

    [Fact]
    public async Task Dispose_KeepsListeningLongEnoughToSeeTheHangupAcknowledged()
    {
        using var remote = new FakeIaxRemote();
        var s = await remote.ConnectSessionAsync(configure: x => x.HangupAckWait = TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();

        var disposing = s.DisposeAsync().AsTask();
        await remote.WaitForFrameAsync(IsHangup);
        await remote.SendIaxAsync(IaxControlSubclass.Ack);

        // If disposal stopped the receive loop before hanging up, the ACK would go unseen and this would take 10 s.
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Equal(Iax2DisconnectReason.LocalRequest, s.DisconnectReason);
    }

    // ----- authentication -----

    private static byte[] AuthRequest(string? challenge)
    {
        var ies = new List<Iax2InformationElement>
        {
            Iax2InformationElement.String(InformationElementId.Username, "radio"),
            Iax2InformationElement.UInt16(InformationElementId.AuthMethods, 0x0003)   // plaintext | MD5 offered
        };
        if (challenge != null) ies.Add(Iax2InformationElement.String(InformationElementId.Challenge, challenge));

        byte[] buffer = new byte[128];
        int n = WriteInformationElements(buffer, ies.ToArray());
        return buffer[..n];
    }

    [Fact]
    public async Task AuthRequest_WithAChallenge_IsAnsweredWithItsMd5_NeverThePassword()
    {
        using var remote = new FakeIaxRemote();
        await using var s = new Iax2Session
        {
            LocalNodeNumber = "1234", RemoteNodeNumber = "2000", Username = "radio", Password = "Sup3rSekret"
        };
        await s.ConnectAsync(remote.EndPoint);
        await remote.ReceiveFrameAsync();                                   // NEW

        await remote.SendAsync(FrameType.IaxControl, (byte)IaxControlSubclass.AuthReq, payload: AuthRequest("24680"));
        var reply = await remote.WaitForIaxAsync(IaxControlSubclass.AuthRep);

        var ies = ParseInformationElements(reply.Payload);
        string expected = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("24680" + "Sup3rSekret")));
        Assert.Equal(expected, ies.Single(i => i.Id == InformationElementId.Md5Result).AsString());
        Assert.DoesNotContain(ies, i => i.Id == InformationElementId.Password);
        Assert.DoesNotContain("Sup3rSekret", Encoding.Latin1.GetString(reply.Payload));
    }

    [Theory]
    [InlineData(null, "Sup3rSekret")]     // no challenge offered: only the plaintext method would work
    [InlineData("24680", "")]             // a challenge, but this link has no password (the normal case)
    public async Task AuthRequest_ThatCannotBeAnsweredSafely_EndsTheCall_AndPutsNoPasswordOnTheWire(
        string? challenge, string password)
    {
        using var remote = new FakeIaxRemote();
        await using var s = new Iax2Session
        {
            LocalNodeNumber = "1234", RemoteNodeNumber = "2000", Username = "radio", Password = password
        };
        var errors = new List<string>();
        s.ErrorOccurred += m => { lock (errors) errors.Add(m); };
        await s.ConnectAsync(remote.EndPoint);
        await remote.ReceiveFrameAsync();                                   // NEW

        await remote.SendAsync(FrameType.IaxControl, (byte)IaxControlSubclass.AuthReq, payload: AuthRequest(challenge));
        await Wait.UntilAsync(() => s.State == Iax2SessionState.Disconnected);

        Assert.Equal(Iax2DisconnectReason.RemoteRejected, s.DisconnectReason);
        lock (errors) Assert.Contains(errors, e => e.Contains("password"));

        // Everything the session sent after the request: no AUTHREP, no Password element, no password text.
        try
        {
            while (true)
            {
                var f = await remote.ReceiveFrameAsync(250);
                Assert.False(f.FrameType == FrameType.IaxControl && f.Subclass == (byte)IaxControlSubclass.AuthRep);
                if (password.Length > 0) Assert.DoesNotContain(password, Encoding.Latin1.GetString(f.Payload));
            }
        }
        catch (OperationCanceledException) { }
    }

    // ----- socket errors -----

    [Fact]
    public async Task DiallingAClosedPort_EndsAsUnanswered_NotAsAReceiveError()
    {
        // Nothing listens here, so the OS answers each packet with ICMP "port unreachable". On Windows that
        // used to surface as an exception from the receive call and end the receive loop.
        IPEndPoint closed;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            closed = (IPEndPoint)probe.Client.LocalEndPoint!;

        await using var s = new Iax2Session
        {
            LocalNodeNumber = "1234", RemoteNodeNumber = "2000",
            InitialRetransmitTimeout = TimeSpan.FromMilliseconds(40), MaxRetransmits = 3
        };
        var errors = new List<string>();
        s.ErrorOccurred += m => { lock (errors) errors.Add(m); };

        await s.ConnectAsync(closed);
        await Wait.UntilAsync(() => s.State == Iax2SessionState.Disconnected);

        Assert.Equal(Iax2DisconnectReason.LinkLost, s.DisconnectReason);
        lock (errors)
        {
            Assert.Contains(errors, e => e.Contains("no acknowledgment"));
            Assert.DoesNotContain(errors, e => e.Contains("receive error"));
        }
    }
}
