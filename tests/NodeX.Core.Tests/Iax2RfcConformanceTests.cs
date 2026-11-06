using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

/// <summary>Checks protocol constants against the tables in RFC 5456 (sections 8.3 and 8.4).</summary>
public class Iax2RfcConformanceTests
{
    [Theory]
    [InlineData(IaxControlSubclass.New, 0x01)]
    [InlineData(IaxControlSubclass.Ping, 0x02)]
    [InlineData(IaxControlSubclass.Pong, 0x03)]
    [InlineData(IaxControlSubclass.Ack, 0x04)]
    [InlineData(IaxControlSubclass.Hangup, 0x05)]
    [InlineData(IaxControlSubclass.Reject, 0x06)]
    [InlineData(IaxControlSubclass.Accept, 0x07)]
    [InlineData(IaxControlSubclass.AuthReq, 0x08)]
    [InlineData(IaxControlSubclass.AuthRep, 0x09)]
    [InlineData(IaxControlSubclass.Inval, 0x0A)]
    [InlineData(IaxControlSubclass.LagRq, 0x0B)]
    [InlineData(IaxControlSubclass.LagRp, 0x0C)]
    [InlineData(IaxControlSubclass.RegReq, 0x0D)]
    [InlineData(IaxControlSubclass.RegAuth, 0x0E)]
    [InlineData(IaxControlSubclass.RegAck, 0x0F)]
    [InlineData(IaxControlSubclass.RegRej, 0x10)]
    [InlineData(IaxControlSubclass.RegRel, 0x11)]
    [InlineData(IaxControlSubclass.Vnak, 0x12)]
    [InlineData(IaxControlSubclass.DpReq, 0x13)]
    [InlineData(IaxControlSubclass.DpRep, 0x14)]
    [InlineData(IaxControlSubclass.Dial, 0x15)]
    [InlineData(IaxControlSubclass.TxReq, 0x16)]
    [InlineData(IaxControlSubclass.TxCnt, 0x17)]
    [InlineData(IaxControlSubclass.TxAcc, 0x18)]
    [InlineData(IaxControlSubclass.TxReady, 0x19)]
    [InlineData(IaxControlSubclass.TxRel, 0x1A)]
    [InlineData(IaxControlSubclass.TxRej, 0x1B)]
    [InlineData(IaxControlSubclass.Quelch, 0x1C)]
    [InlineData(IaxControlSubclass.Unquelch, 0x1D)]
    [InlineData(IaxControlSubclass.Poke, 0x1E)]
    [InlineData(IaxControlSubclass.Mwi, 0x20)]
    [InlineData(IaxControlSubclass.Unsupport, 0x21)]
    [InlineData(IaxControlSubclass.Transfer, 0x22)]
    [InlineData(IaxControlSubclass.CallToken, 0x28)] // Asterisk 1.8+/ASL extension, not in the RFC table
    public void IaxSubclassValues_MatchRfc5456Section8_4(IaxControlSubclass subclass, int expected) =>
        Assert.Equal(expected, (byte)subclass);

    [Theory]
    [InlineData(ControlSubclass.Hangup, 0x01)]
    [InlineData(ControlSubclass.Ringing, 0x03)]
    [InlineData(ControlSubclass.Answer, 0x04)]
    [InlineData(ControlSubclass.Busy, 0x05)]
    [InlineData(ControlSubclass.Congestion, 0x08)]
    [InlineData(ControlSubclass.Flash, 0x09)]
    [InlineData(ControlSubclass.Option, 0x0B)]
    [InlineData(ControlSubclass.AstControlKey, 0x0C)]
    [InlineData(ControlSubclass.AstControlUnkey, 0x0D)]
    [InlineData(ControlSubclass.CallProgress, 0x0E)]
    [InlineData(ControlSubclass.CallProceeding, 0x0F)]
    [InlineData(ControlSubclass.Hold, 0x10)]
    [InlineData(ControlSubclass.Unhold, 0x11)]
    public void ControlSubclassValues_MatchRfc5456Section8_3(ControlSubclass subclass, int expected) =>
        Assert.Equal(expected, (byte)subclass);

    [Fact]
    public void IaxSubclasses_HaveNoDuplicateValues()
    {
        var values = Enum.GetValues<IaxControlSubclass>().Select(v => (byte)v).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void ControlSubclasses_HaveNoDuplicateValues()
    {
        var values = Enum.GetValues<ControlSubclass>().Select(v => (byte)v).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }
}

/// <summary>Loopback tests for how the session reacts to RFC-defined frames.</summary>
public class Iax2SessionFrameHandlingTests
{
    [Fact]
    public async Task Lagrq_IsAnswered_WithLagrpEchoingTimestamp()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();

        await remote.SendIaxAsync(IaxControlSubclass.LagRq, timestamp: 123456);
        var lagrp = await remote.WaitForIaxAsync(IaxControlSubclass.LagRp);

        Assert.Equal(123456u, lagrp.Timestamp);
    }

    [Fact]
    public async Task Poke_IsAnswered_WithPongEchoingTimestamp()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();

        await remote.SendIaxAsync(IaxControlSubclass.Poke, timestamp: 4242);
        var pong = await remote.WaitForIaxAsync(IaxControlSubclass.Pong);

        Assert.Equal(4242u, pong.Timestamp);
    }

    [Fact]
    public async Task RemoteKeyAndUnkey_RaiseRemoteKeyedChanged_OnlyForSubclasses12And13()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();

        var events = new List<bool>();
        var gotUnkey = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RemoteKeyedChanged += k =>
        {
            lock (events) events.Add(k);
            if (!k) gotUnkey.TrySetResult();
        };

        // 0x0A (reserved) and 0x0B (Option) previously aliased "RadioKey"/"RadioUnkey" in the repo.
        await remote.SendAsync(FrameType.Control, 0x0A);
        await remote.SendAsync(FrameType.Control, (byte)ControlSubclass.Option);
        await remote.SendControlAsync(ControlSubclass.AstControlKey);
        await remote.SendControlAsync(ControlSubclass.AstControlUnkey);

        await gotUnkey.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lock (events) Assert.Equal([true, false], events);
    }

    [Fact]
    public async Task RemoteBusy_DisconnectsSession_InsteadOfBeingTreatedAsAnswer()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += s => { if (s == Iax2SessionState.Disconnected) disconnected.TrySetResult(); };

        await remote.SendControlAsync(ControlSubclass.Busy); // 0x05; previously mis-mapped to Answer

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Iax2SessionState.Disconnected, session.State);
    }

    [Fact]
    public async Task RemoteCongestion_Disconnects()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += s => { if (s == Iax2SessionState.Disconnected) disconnected.TrySetResult(); };

        await remote.SendControlAsync(ControlSubclass.Congestion); // 0x08

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
