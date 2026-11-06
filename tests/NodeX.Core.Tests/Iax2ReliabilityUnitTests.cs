using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

public class Iax2SequencingTests
{
    [Theory]
    [InlineData(FrameType.Control, 12, true)]
    [InlineData(FrameType.Voice, 4, true)]
    [InlineData(FrameType.Dtmf, 49, true)]
    [InlineData(FrameType.Text, 0, true)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.New, true)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.Accept, true)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.Ping, true)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.Pong, true)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.Hangup, true)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.LagRq, true)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.Ack, false)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.Inval, false)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.TxCnt, false)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.TxAcc, false)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.Vnak, false)]
    [InlineData(FrameType.IaxControl, (byte)IaxControlSubclass.CallToken, false)]
    public void IsReliable_FollowsRfc5456Section7(FrameType type, byte subclass, bool expected) =>
        Assert.Equal(expected, Iax2Sequencing.IsReliable(type, subclass));

    [Theory]
    [InlineData(5, 5, "InOrder")]
    [InlineData(6, 5, "Future")]
    [InlineData(4, 5, "Duplicate")]
    [InlineData(0, 255, "Future")]      // next frame after wrap-around
    [InlineData(255, 0, "Duplicate")]   // frame from before the wrap
    [InlineData(0, 250, "Future")]      // 6 ahead across the wrap
    [InlineData(250, 0, "Duplicate")]
    [InlineData(121, 250, "Future")]    // 127 ahead: furthest "future"
    [InlineData(122, 250, "Duplicate")] // 128 ahead is treated as old
    [InlineData(0, 0, "InOrder")]
    [InlineData(255, 255, "InOrder")]
    public void Classify_UsesModulo256Arithmetic(int received, int expected, string order) =>
        Assert.Equal(order, Iax2Sequencing.Classify((byte)received, (byte)expected).ToString());

    [Theory]
    [InlineData(3, 5, true)]     // peer expects 5 next, so it has 3
    [InlineData(4, 5, true)]
    [InlineData(5, 5, false)]    // peer still expects 5: not received
    [InlineData(6, 5, false)]
    [InlineData(255, 0, true)]   // wrap: peer expects 0, so it has 255
    [InlineData(0, 0, false)]
    [InlineData(250, 5, true)]   // 11 behind across the wrap
    [InlineData(5, 250, false)]
    [InlineData(5, 3, false)]
    public void IsAcknowledgedBy_UsesModulo256Arithmetic(int seq, int peerNextExpected, bool expected) =>
        Assert.Equal(expected, Iax2Sequencing.IsAcknowledgedBy((byte)seq, (byte)peerNextExpected));
}

public class Iax2RetransmitQueueTests
{
    private static byte[] Packet(byte marker)
    {
        byte[] p = new byte[FullHeaderSize];
        p[11] = marker; // identifies the frame in assertions
        return p;
    }

    [Fact]
    public void Ack_ReleasesFramesBeforePeerNextExpected()
    {
        var q = new Iax2RetransmitQueue();
        for (byte i = 0; i < 3; i++) q.Add(i, Packet(i), 0, 100);

        Assert.Equal(2, q.AcknowledgeUpTo(2));
        Assert.Equal(1, q.Count);
        Assert.Equal(1, q.AcknowledgeUpTo(3));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Ack_HandlesSequenceWrap()
    {
        var q = new Iax2RetransmitQueue();
        foreach (byte s in new byte[] { 254, 255, 0, 1 }) q.Add(s, Packet(s), 0, 100);

        Assert.Equal(2, q.AcknowledgeUpTo(0));   // peer has 254 and 255
        Assert.Equal(2, q.Count);
        Assert.Equal(2, q.AcknowledgeUpTo(2));   // and now 0 and 1
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void StaleAck_ReleasesNothing()
    {
        var q = new Iax2RetransmitQueue();
        q.Add(10, Packet(10), 0, 100);
        Assert.Equal(0, q.AcknowledgeUpTo(5)); // peer is behind
        Assert.Equal(0, q.AcknowledgeUpTo(10));
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void CollectDue_WaitsForTimer_SetsRBit_AndDoublesTimeout()
    {
        var q = new Iax2RetransmitQueue();
        byte[] original = Packet(7);
        q.Add(7, original, nowMs: 0, timeoutMs: 100);

        Assert.Empty(q.CollectDue(99, 4, out bool exceeded));
        Assert.False(exceeded);

        var first = q.CollectDue(100, 4, out _);
        Assert.Single(first);
        Assert.Equal(0x80, first[0][2] & 0x80);
        Assert.Equal(0, original[2] & 0x80); // stored original is untouched

        Assert.Empty(q.CollectDue(299, 4, out _));   // next due = 100 + 200
        Assert.Single(q.CollectDue(300, 4, out _));
        Assert.Empty(q.CollectDue(699, 4, out _));   // next due = 300 + 400
        Assert.Single(q.CollectDue(700, 4, out _));
    }

    [Fact]
    public void Backoff_IsCappedAtTenSeconds()
    {
        var q = new Iax2RetransmitQueue();
        q.Add(0, Packet(0), 0, 8_000);

        Assert.Single(q.CollectDue(8_000, 4, out _)); // timeout would be 16 s; capped at 10 s
        Assert.Empty(q.CollectDue(17_999, 4, out _));
        Assert.Single(q.CollectDue(18_000, 4, out _));
    }

    [Fact]
    public void ExceedingRetryLimit_IsReported_WithoutFurtherPackets()
    {
        var q = new Iax2RetransmitQueue();
        q.Add(0, Packet(0), 0, 100);

        Assert.Single(q.CollectDue(100, 2, out bool e1));  // retransmission 1
        Assert.False(e1);
        Assert.Single(q.CollectDue(300, 2, out bool e2));  // retransmission 2
        Assert.False(e2);

        var none = q.CollectDue(700, 2, out bool e3);      // due again, limit reached
        Assert.Empty(none);
        Assert.True(e3);
    }

    [Fact]
    public void RetransmitFrom_ReturnsPendingFramesFromSequence_AcrossWrap()
    {
        var q = new Iax2RetransmitQueue();
        foreach (byte s in new byte[] { 250, 251, 252, 253, 254, 255, 0, 1 }) q.Add(s, Packet(s), 0, 100);

        var resent = q.RetransmitFrom(254, nowMs: 0);

        Assert.Equal(new byte[] { 254, 255, 0, 1 }, resent.Select(p => p[11]).ToArray());
        Assert.All(resent, p => Assert.Equal(0x80, p[2] & 0x80));
    }

    [Fact]
    public void Clear_EmptiesQueue()
    {
        var q = new Iax2RetransmitQueue();
        q.Add(0, Packet(0), 0, 100);
        q.Clear();
        Assert.Equal(0, q.Count);
        Assert.Empty(q.CollectDue(10_000, 4, out bool exceeded));
        Assert.False(exceeded);
    }
}
