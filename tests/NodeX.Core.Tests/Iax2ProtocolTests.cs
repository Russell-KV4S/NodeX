using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

public class Iax2ProtocolTests
{
    [Fact]
    public void SpecConstants()
    {
        Assert.Equal(4569, DefaultPort);
        Assert.Equal(6, (byte)FrameType.IaxControl);
        Assert.Equal(4, (byte)FrameType.Control);
        Assert.Equal(1, (byte)IaxControlSubclass.New);
        Assert.Equal(4, (byte)IaxControlSubclass.Ack);
        Assert.Equal(7, (byte)IaxControlSubclass.Accept);
        Assert.Equal(30, (byte)IaxControlSubclass.Poke);
        Assert.Equal(0x28, (byte)IaxControlSubclass.CallToken);
        Assert.Equal(12, (byte)ControlSubclass.AstControlKey);
        Assert.Equal(13, (byte)ControlSubclass.AstControlUnkey);
        Assert.Equal(0x04u, (uint)CodecFormat.Ulaw);
        Assert.Equal(54, (byte)InformationElementId.CallToken);
    }

    [Fact]
    public void WriteFullFrame_ProducesExpectedWireBytes()
    {
        byte[] buf = new byte[12];
        int n = WriteFullFrame(buf, 0x1234, 0x0567, 0xAABBCCDD, 3, 4,
            FrameType.Control, (byte)ControlSubclass.AstControlKey);

        Assert.Equal(12, n);
        Assert.Equal(
            new byte[] { 0x92, 0x34, 0x05, 0x67, 0xAA, 0xBB, 0xCC, 0xDD, 3, 4, 4, 12 },
            buf);
    }

    [Fact]
    public void WriteFullFrame_MasksCallNumbersTo15Bits()
    {
        byte[] buf = new byte[12];
        WriteFullFrame(buf, 0xFFFF, 0xFFFF, 0, 0, 0, FrameType.IaxControl, 1);
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xFF, buf[1]);
        Assert.Equal(0x7F, buf[2]);
        Assert.Equal(0xFF, buf[3]);
    }

    [Fact]
    public void FullFrame_RoundTrips()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        byte[] buf = new byte[64];
        int n = WriteFullFrame(buf, 100, 200, 123456, 7, 8, FrameType.Text, 0, payload);

        Assert.True(TryParseFrame(buf.AsSpan(0, n), out var f));
        Assert.True(f.IsFullFrame);
        Assert.Equal(100, f.SourceCallNumber);
        Assert.Equal(200, f.DestCallNumber);
        Assert.Equal(123456u, f.Timestamp);
        Assert.Equal(7, f.OSeqno);
        Assert.Equal(8, f.ISeqno);
        Assert.Equal(FrameType.Text, f.FrameType);
        Assert.Equal(payload, f.Payload);
    }

    [Fact]
    public void WriteFullFrame_ThrowsWhenBufferTooSmall()
    {
        Assert.Throws<ArgumentException>(() =>
            WriteFullFrame(new byte[11], 1, 1, 0, 0, 0, FrameType.Control, 0));
        Assert.Throws<ArgumentException>(() =>
            WriteFullFrame(new byte[13], 1, 1, 0, 0, 0, FrameType.Control, 0, new byte[2]));
    }

    [Fact]
    public void MiniFrame_WireFormatAndRoundTrip()
    {
        byte[] voice = new byte[160];
        for (int i = 0; i < voice.Length; i++) voice[i] = (byte)i;

        byte[] buf = new byte[164];
        int n = WriteMiniFrame(buf, 0x0102, 0xBEEF, voice);

        Assert.Equal(164, n);
        Assert.Equal(0x01, buf[0]);
        Assert.Equal(0x02, buf[1]);
        Assert.Equal(0xBE, buf[2]);
        Assert.Equal(0xEF, buf[3]);

        Assert.True(TryParseFrame(buf, out var f));
        Assert.False(f.IsFullFrame);
        Assert.Equal(0x0102, f.SourceCallNumber);
        Assert.Equal(0xBEEFu, f.Timestamp);
        Assert.Equal(FrameType.Voice, f.FrameType);
        Assert.Equal(voice, f.Payload);
    }

    [Fact]
    public void WriteMiniFrame_MasksSourceCallAndThrowsOnSmallBuffer()
    {
        byte[] buf = new byte[4];
        WriteMiniFrame(buf, 0xFFFF, 0, default);
        Assert.Equal(0x7F, buf[0]);
        Assert.Throws<ArgumentException>(() => WriteMiniFrame(new byte[5], 1, 0, new byte[2]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void TryParse_RejectsPacketsShorterThanMiniHeader(int len)
    {
        Assert.False(TryParseFrame(new byte[len], out _));
    }

    [Fact]
    public void TryParse_RejectsTruncatedFullFrame()
    {
        byte[] p = new byte[11];
        p[0] = 0x80;
        Assert.False(TryParseFrame(p, out _));
    }

    [Fact]
    public void TryParse_HeaderOnlyFullFrame_HasEmptyPayload()
    {
        byte[] buf = new byte[12];
        WriteFullFrame(buf, 1, 2, 0, 0, 0, FrameType.Control, 13);
        Assert.True(TryParseFrame(buf, out var f));
        Assert.Empty(f.Payload);
        Assert.Equal(13, f.Subclass);
    }

    [Fact]
    public void TryParse_ClearsRetransmitBitInDestCall()
    {
        byte[] buf = new byte[12];
        WriteFullFrame(buf, 1, 0x0042, 0, 0, 0, FrameType.IaxControl, 4);
        buf[2] |= 0x80;
        Assert.True(TryParseFrame(buf, out var f));
        Assert.Equal(0x0042, f.DestCallNumber);
    }

    [Fact]
    public void InformationElements_RoundTripAllTypes()
    {
        var ies = new[]
        {
            Iax2InformationElement.String(InformationElementId.CalledNumber, "1999"),
            Iax2InformationElement.String(InformationElementId.CallingNumber, "W1AW"),
            Iax2InformationElement.UInt32(InformationElementId.Format, (uint)CodecFormat.Ulaw),
            Iax2InformationElement.UInt16(InformationElementId.Version, 2),
            Iax2InformationElement.Byte(InformationElementId.Cause, 16),
            Iax2InformationElement.Raw(InformationElementId.CallToken, [9, 8, 7]),
        };

        byte[] buf = new byte[128];
        int n = WriteInformationElements(buf, ies);
        Assert.Equal(6 * 2 + 4 + 4 + 4 + 2 + 1 + 3, n);

        var parsed = ParseInformationElements(buf.AsSpan(0, n));
        Assert.Equal(6, parsed.Count);
        Assert.Equal("1999", parsed[0].AsString());
        Assert.Equal("W1AW", parsed[1].AsString());
        Assert.Equal(4u, parsed[2].AsUInt32());
        Assert.Equal(2, parsed[3].AsUInt16());
        Assert.Equal(16, parsed[4].Data[0]);
        Assert.Equal(InformationElementId.CallToken, parsed[5].Id);
        Assert.Equal(new byte[] { 9, 8, 7 }, parsed[5].Data);
    }

    [Fact]
    public void CallTokenIe_EncodesAsIdThenLength()
    {
        byte[] buf = new byte[8];
        int n = WriteInformationElements(buf,
            [Iax2InformationElement.Raw(InformationElementId.CallToken, [0xAA, 0xBB])]);
        Assert.Equal(4, n);
        Assert.Equal(new byte[] { 0x36, 2, 0xAA, 0xBB }, buf[..4]);
    }

    [Fact]
    public void EmptyCallTokenIe_ForInitialNew_IsTwoBytes()
    {
        byte[] buf = new byte[4];
        int n = WriteInformationElements(buf,
            [Iax2InformationElement.Raw(InformationElementId.CallToken, [])]);
        Assert.Equal(2, n);
        Assert.Equal(new byte[] { 0x36, 0 }, buf[..2]);
        var parsed = ParseInformationElements(buf.AsSpan(0, n));
        Assert.Single(parsed);
        Assert.Empty(parsed[0].Data);
    }

    [Fact]
    public void ParseInformationElements_StopsOnTruncatedElement()
    {
        byte[] payload = [1, 2, (byte)'A', (byte)'B', 2, 5, 0x01];
        var parsed = ParseInformationElements(payload);
        Assert.Single(parsed);
        Assert.Equal("AB", parsed[0].AsString());
    }

    [Fact]
    public void ParseInformationElements_EmptyOrDanglingByte_ReturnsEmpty()
    {
        Assert.Empty(ParseInformationElements([]));
        Assert.Empty(ParseInformationElements([1]));
    }

    [Fact]
    public void WriteInformationElements_ThrowsWhenBufferTooSmall()
    {
        Assert.Throws<ArgumentException>(() =>
            WriteInformationElements(new byte[3],
                [Iax2InformationElement.String(InformationElementId.CalledNumber, "1234")]));
    }

    [Fact]
    public void IeAccessors_ReturnZeroOnShortData()
    {
        var ie = Iax2InformationElement.Byte(InformationElementId.Cause, 1);
        Assert.Equal(0u, ie.AsUInt32());
        Assert.Equal(0, ie.AsUInt16());
    }

    [Fact]
    public void NewFrameWithIes_HandshakeShape_ParsesBack()
    {
        byte[] ies = new byte[64];
        int ieLen = WriteInformationElements(ies,
        [
            Iax2InformationElement.String(InformationElementId.CalledNumber, "2000"),
            Iax2InformationElement.String(InformationElementId.CallingNumber, "1234"),
            Iax2InformationElement.UInt32(InformationElementId.Format, 0x04),
            Iax2InformationElement.Raw(InformationElementId.CallToken, [1, 2, 3, 4]),
        ]);

        byte[] pkt = new byte[12 + ieLen];
        WriteFullFrame(pkt, 1, 0, 0, 0, 0, FrameType.IaxControl,
            (byte)IaxControlSubclass.New, ies.AsSpan(0, ieLen));

        Assert.True(TryParseFrame(pkt, out var f));
        Assert.Equal(FrameType.IaxControl, f.FrameType);
        Assert.Equal((byte)IaxControlSubclass.New, f.Subclass);
        var parsed = ParseInformationElements(f.Payload);
        Assert.Equal(4, parsed.Count);
        Assert.Equal("2000", parsed[0].AsString());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parsed[3].Data);
    }
}
