using System.Buffers.Binary;
using System.Text;

namespace NodeX.Core;

/// <summary>
/// Core constants, enumeration definitions, and binary frame serialization/deserialization
/// for the IAX2 protocol (RFC 5456) with Asterisk 20+ / AllStarLink (ASL3) extensions.
/// </summary>
public static class Iax2Protocol
{
    public const int DefaultPort = 4569;
    public const int FullHeaderSize = 12;
    public const int MiniHeaderSize = 4;
    public const ushort FullFrameFlag = 0x8000;

    #region Frame Types
    public enum FrameType : byte
    {
        Dtmf = 1,
        Voice = 2,
        Video = 3,
        Control = 4,
        Null = 5,
        IaxControl = 6,
        Text = 7,
        Image = 8,
        Html = 9,
        ComfortNoise = 10
    }
    #endregion

    #region IAX Control Subclasses (RFC 5456 + Asterisk/ASL CallToken)
    public enum IaxControlSubclass : byte
    {
        New = 1,
        Ping = 2,
        Pong = 3,
        Ack = 4,
        Hangup = 5,
        Reject = 6,
        Accept = 7,
        AuthReq = 8,
        AuthRep = 9,
        Inval = 0x0A,
        /// <summary>Lag request; MUST be answered with <see cref="LagRp"/> echoing its time-stamp.</summary>
        LagRq = 0x0B,
        LagRp = 0x0C,
        RegReq = 0x0D,
        RegAuth = 0x0E,
        RegAck = 0x0F,
        RegRej = 0x10,
        RegRel = 0x11,
        Vnak = 0x12,
        DpReq = 0x13,
        DpRep = 0x14,
        Dial = 0x15,
        TxReq = 0x16,
        TxCnt = 0x17,
        TxAcc = 0x18,
        TxReady = 0x19,
        TxRel = 0x1A,
        TxRej = 0x1B,
        Quelch = 0x1C,
        Unquelch = 0x1D,
        /// <summary>Out-of-call reachability probe (RFC 5456 6.7.1): dest call number MUST be 0.</summary>
        Poke = 0x1E,
        Mwi = 0x20,
        Unsupport = 0x21,
        Transfer = 0x22,
        /// <summary>IAX2 CallToken challenge for Asterisk 20+ / ASL3 (0x28)</summary>
        CallToken = 40
    }
    #endregion

    #region Control Frame Subclasses (RFC 5456 & AllStarLink PTT Signaling)
    public enum ControlSubclass : byte
    {
        Hangup = 1,
        Ring = 2,
        Ringing = 3,
        Answer = 4,
        Busy = 5,
        Congestion = 8,
        Flash = 9,
        Option = 0x0B,
        /// <summary>RFC 5456 "Key Radio" / Asterisk AST_CONTROL_RADIO_KEY (0x0C) - PTT keyed</summary>
        AstControlKey = 12,
        /// <summary>RFC 5456 "Unkey Radio" / Asterisk AST_CONTROL_RADIO_UNKEY (0x0D) - PTT unkeyed</summary>
        AstControlUnkey = 13,
        CallProgress = 14,
        CallProceeding = 15,
        Hold = 16,
        Unhold = 17
    }
    #endregion

    #region Codec Formats
    [Flags]
    public enum CodecFormat : uint
    {
        G723_1 = 1 << 0,
        Gsm = 1 << 1,
        /// <summary>G.711 µ-law (0x04, standard 8000 Hz for AllStarLink)</summary>
        Ulaw = 1 << 2,
        /// <summary>G.711 A-law</summary>
        Alaw = 1 << 3,
        G726 = 1 << 4,
        Adpcm = 1 << 5,
        SLinear = 1 << 6,
        Lpc10 = 1 << 7,
        G729A = 1 << 8,
        Speex = 1 << 9,
        Ilbc = 1 << 10
    }
    #endregion

    #region Information Element (IE) IDs
    public enum InformationElementId : byte
    {
        CalledNumber = 1,
        CallingNumber = 2,
        CallingName = 3,
        CallingAni = 4,
        CalledContext = 5,
        Username = 6,
        Password = 7,
        Capability = 8,
        Format = 9,
        Language = 10,
        Version = 11,
        AdsIcpE = 12,
        Dnid = 13,
        AuthMethods = 14,
        Challenge = 15,
        Md5Result = 16,
        RsaResult = 17,
        ApparentAddr = 18,
        Refresh = 19,
        DpStatus = 20,
        CallNo = 21,
        Cause = 22,
        IaxUnknown = 23,
        MsgCount = 24,
        AutoAnswer = 25,
        MusicOnHold = 26,
        TransferId = 27,
        RrDelay = 28,
        RrJitter = 29,
        RrLoss = 30,
        RrPkts = 31,
        RrDelay2 = 32,
        RrDropped = 33,
        RrOoo = 34,
        DateTime = 35,
        /// <summary>Hangup cause code (Q.931-style, 1 byte; 16 = normal clearing). Asterisk extension.</summary>
        CauseCode = 42,
        /// <summary>IAX2 Call Token IE (0x36 / 54) for Asterisk 20+ / ASL3 security handshakes</summary>
        CallToken = 54
    }
    #endregion

    #region Serialization & Parsing
    /// <summary>
    /// Writes an IAX2 Full Frame into the target destination buffer.
    /// </summary>
    public static int WriteFullFrame(
        Span<byte> destination,
        ushort sourceCallNumber,
        ushort destCallNumber,
        uint timestamp,
        byte oSeqno,
        byte iSeqno,
        FrameType frameType,
        byte subclass,
        ReadOnlySpan<byte> payload = default)
    {
        if (destination.Length < FullHeaderSize + payload.Length)
        {
            throw new ArgumentException("Destination buffer too small for IAX2 Full Frame.", nameof(destination));
        }

        // Byte 0-1: 'F' bit (1) + 15-bit Source Call Number
        BinaryPrimitives.WriteUInt16BigEndian(destination[0..2], (ushort)(FullFrameFlag | (sourceCallNumber & 0x7FFF)));

        // Byte 2-3: 'R' bit (0) + 15-bit Destination Call Number
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..4], (ushort)(destCallNumber & 0x7FFF));

        // Byte 4-7: 32-bit Timestamp (milliseconds)
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], timestamp);

        // Byte 8: Outgoing sequence number
        destination[8] = oSeqno;

        // Byte 9: Inbound expected sequence number
        destination[9] = iSeqno;

        // Byte 10: Frame type
        destination[10] = (byte)frameType;

        // Byte 11: Subclass
        destination[11] = subclass;

        // Byte 12+: Payload
        if (!payload.IsEmpty)
        {
            payload.CopyTo(destination[FullHeaderSize..]);
        }

        return FullHeaderSize + payload.Length;
    }

    /// <summary>
    /// Writes an IAX2 Mini Frame (streaming 20ms audio) into the target destination buffer.
    /// </summary>
    public static int WriteMiniFrame(
        Span<byte> destination,
        ushort sourceCallNumber,
        ushort timestamp16,
        ReadOnlySpan<byte> voicePayload)
    {
        if (destination.Length < MiniHeaderSize + voicePayload.Length)
        {
            throw new ArgumentException("Destination buffer too small for IAX2 Mini Frame.", nameof(destination));
        }

        // Byte 0-1: 'F' bit (0) + 15-bit Source Call Number
        BinaryPrimitives.WriteUInt16BigEndian(destination[0..2], (ushort)(sourceCallNumber & 0x7FFF));

        // Byte 2-3: 16-bit wrapping timestamp
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..4], timestamp16);

        // Byte 4+: Voice payload (e.g. 160 bytes G.711 µ-law for 20ms)
        if (!voicePayload.IsEmpty)
        {
            voicePayload.CopyTo(destination[MiniHeaderSize..]);
        }

        return MiniHeaderSize + voicePayload.Length;
    }

    /// <summary>
    /// Parses an incoming IAX2 datagram into an <see cref="Iax2Frame"/>.
    /// </summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> packet, out Iax2Frame frame)
    {
        frame = default;
        if (packet.Length < MiniHeaderSize)
        {
            return false;
        }

        ushort firstWord = BinaryPrimitives.ReadUInt16BigEndian(packet[0..2]);
        bool isFullFrame = (firstWord & FullFrameFlag) != 0;
        ushort sourceCall = (ushort)(firstWord & 0x7FFF);

        if (isFullFrame)
        {
            if (packet.Length < FullHeaderSize)
            {
                return false;
            }

            ushort secondWord = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
            ushort destCall = (ushort)(secondWord & 0x7FFF);
            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]);
            byte oSeqno = packet[8];
            byte iSeqno = packet[9];
            FrameType frameType = (FrameType)packet[10];
            byte subclass = packet[11];

            byte[] payload = packet.Length > FullHeaderSize ? packet[FullHeaderSize..].ToArray() : [];

            frame = new Iax2Frame(
                IsFullFrame: true,
                SourceCallNumber: sourceCall,
                DestCallNumber: destCall,
                Timestamp: timestamp,
                OSeqno: oSeqno,
                ISeqno: iSeqno,
                FrameType: frameType,
                Subclass: subclass,
                Payload: payload
            );
            return true;
        }
        else
        {
            ushort timestamp16 = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
            byte[] voicePayload = packet.Length > MiniHeaderSize ? packet[MiniHeaderSize..].ToArray() : [];

            frame = new Iax2Frame(
                IsFullFrame: false,
                SourceCallNumber: sourceCall,
                DestCallNumber: 0,
                Timestamp: timestamp16,
                OSeqno: 0,
                ISeqno: 0,
                FrameType: FrameType.Voice,
                Subclass: (byte)CodecFormat.Ulaw,
                Payload: voicePayload
            );
            return true;
        }
    }

    /// <summary>
    /// Writes a collection of Information Elements (IE) into the destination span.
    /// </summary>
    public static int WriteInformationElements(Span<byte> destination, ReadOnlySpan<Iax2InformationElement> elements)
    {
        int offset = 0;
        foreach (var ie in elements)
        {
            if (offset + 2 + ie.Data.Length > destination.Length)
            {
                throw new ArgumentException("Destination buffer too small for Information Elements.");
            }

            destination[offset++] = (byte)ie.Id;
            destination[offset++] = (byte)ie.Data.Length;
            ie.Data.CopyTo(destination[offset..]);
            offset += ie.Data.Length;
        }
        return offset;
    }

    /// <summary>
    /// Parses Information Elements from an IAX2 payload.
    /// </summary>
    public static List<Iax2InformationElement> ParseInformationElements(ReadOnlySpan<byte> payload)
    {
        var list = new List<Iax2InformationElement>();
        int offset = 0;

        while (offset + 2 <= payload.Length)
        {
            InformationElementId id = (InformationElementId)payload[offset++];
            int length = payload[offset++];

            if (offset + length > payload.Length)
            {
                break;
            }

            byte[] data = payload.Slice(offset, length).ToArray();
            list.Add(new Iax2InformationElement(id, data));
            offset += length;
        }

        return list;
    }
    #endregion
}

/// <summary>
/// Represents a parsed IAX2 full or mini frame.
/// </summary>
public readonly record struct Iax2Frame(
    bool IsFullFrame,
    ushort SourceCallNumber,
    ushort DestCallNumber,
    uint Timestamp,
    byte OSeqno,
    byte ISeqno,
    Iax2Protocol.FrameType FrameType,
    byte Subclass,
    byte[] Payload
);

/// <summary>
/// Represents an IAX2 Information Element (IE).
/// </summary>
public sealed class Iax2InformationElement
{
    public Iax2Protocol.InformationElementId Id { get; }
    public byte[] Data { get; }

    public Iax2InformationElement(Iax2Protocol.InformationElementId id, byte[] data)
    {
        Id = id;
        Data = data;
    }

    public static Iax2InformationElement String(Iax2Protocol.InformationElementId id, string value)
    {
        return new Iax2InformationElement(id, Encoding.UTF8.GetBytes(value));
    }

    public static Iax2InformationElement UInt32(Iax2Protocol.InformationElementId id, uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new Iax2InformationElement(id, bytes);
    }

    public static Iax2InformationElement UInt16(Iax2Protocol.InformationElementId id, ushort value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return new Iax2InformationElement(id, bytes);
    }

    public static Iax2InformationElement Byte(Iax2Protocol.InformationElementId id, byte value)
    {
        return new Iax2InformationElement(id, [value]);
    }

    public static Iax2InformationElement Raw(Iax2Protocol.InformationElementId id, byte[] rawBytes)
    {
        return new Iax2InformationElement(id, rawBytes);
    }

    public string AsString() => Encoding.UTF8.GetString(Data);

    public uint AsUInt32() => Data.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(Data) : 0;

    public ushort AsUInt16() => Data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(Data) : (ushort)0;
}
