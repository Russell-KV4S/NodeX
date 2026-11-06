namespace NodeX.Core;

/// <summary>
/// Modulo-256 sequence-number rules from RFC 5456 section 7: which frames are reliable,
/// how to classify an inbound OSeqno against the expected ISeqno, and what a peer's ISeqno acknowledges.
/// </summary>
internal static class Iax2Sequencing
{
    public enum Order
    {
        /// <summary>The frame carries exactly the expected sequence number.</summary>
        InOrder,
        /// <summary>A gap: earlier frames are missing, so the frame is ignored and a VNAK sent.</summary>
        Future,
        /// <summary>Already received (peer retransmission): ignored, but acknowledged again.</summary>
        Duplicate
    }

    /// <summary>
    /// Whether a full frame consumes a sequence number and must be acknowledged/retransmitted.
    /// RFC 5456 7: everything except ACK, INVAL, TXCNT, TXACC and VNAK. CALLTOKEN (an Asterisk
    /// extension) is a stateless challenge reply and is likewise not sequenced.
    /// </summary>
    public static bool IsReliable(Iax2Protocol.FrameType frameType, byte subclass)
    {
        if (frameType != Iax2Protocol.FrameType.IaxControl) return true;

        return (Iax2Protocol.IaxControlSubclass)subclass switch
        {
            Iax2Protocol.IaxControlSubclass.Ack => false,
            Iax2Protocol.IaxControlSubclass.Inval => false,
            Iax2Protocol.IaxControlSubclass.TxCnt => false,
            Iax2Protocol.IaxControlSubclass.TxAcc => false,
            Iax2Protocol.IaxControlSubclass.Vnak => false,
            Iax2Protocol.IaxControlSubclass.CallToken => false,
            _ => true
        };
    }

    /// <summary>Classifies a received OSeqno against the next expected one, with 8-bit wrap-around.</summary>
    public static Order Classify(byte received, byte expected)
    {
        byte diff = unchecked((byte)(received - expected));
        if (diff == 0) return Order.InOrder;
        return diff < 128 ? Order.Future : Order.Duplicate;
    }

    /// <summary>
    /// True when a peer whose next-expected inbound sequence number is <paramref name="peerNextExpected"/>
    /// has received our frame <paramref name="seq"/> (i.e. seq lies just before it, modulo 256).
    /// </summary>
    public static bool IsAcknowledgedBy(byte seq, byte peerNextExpected)
    {
        byte distance = unchecked((byte)(peerNextExpected - seq));
        return distance >= 1 && distance <= 128;
    }
}
