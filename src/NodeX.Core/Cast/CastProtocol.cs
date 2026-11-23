using System.Buffers.Binary;
using System.Text;

namespace NodeX.Core.Cast;

public enum CastPayloadType
{
    String = 0,
    Binary = 1
}

/// <summary>One CASTV2 protobuf envelope (<c>extensions.api.cast_channel.CastMessage</c>).</summary>
public sealed record CastMessage(
    string SourceId,
    string DestinationId,
    string Namespace,
    CastPayloadType PayloadType,
    string? PayloadUtf8 = null,
    byte[]? PayloadBinary = null,
    int ProtocolVersion = 0);

/// <summary>
/// Wire codec and constants for Google Cast CASTV2 channels (4-byte big-endian length prefix + protobuf payload).
/// </summary>
public static class CastProtocol
{
    public const int DefaultPort = 8009;
    public const int MaxFrameBytes = 256 * 1024;

    /// <summary>Google's built-in Default Media Receiver (available on all Chromecast devices with zero registration).</summary>
    public const string DefaultMediaReceiverAppId = "CC1AD845";

    public const string DefaultSenderId = "sender-0";
    public const string DefaultReceiverId = "receiver-0";

    public const string NsConnection = "urn:x-cast:com.google.cast.tp.connection";
    public const string NsHeartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string NsReceiver = "urn:x-cast:com.google.cast.receiver";
    public const string NsMedia = "urn:x-cast:com.google.cast.media";

    /// <summary>NodeX's custom CASTV2 namespace for live station/link state and µ-law audio packets.</summary>
    public const string NsNodeX = "urn:x-cast:com.kv4s.nodex";

    public static byte[] Encode(CastMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var buf = new List<byte>(128 + (message.PayloadUtf8?.Length ?? message.PayloadBinary?.Length ?? 0));

        // Field 1: protocol_version (varint, wire type 0)
        WriteTag(buf, 1, 0);
        WriteVarint(buf, (ulong)Math.Max(0, message.ProtocolVersion));

        // Field 2: source_id (string, wire type 2)
        WriteStringField(buf, 2, message.SourceId ?? DefaultSenderId);

        // Field 3: destination_id (string, wire type 2)
        WriteStringField(buf, 3, message.DestinationId ?? DefaultReceiverId);

        // Field 4: namespace (string, wire type 2)
        WriteStringField(buf, 4, message.Namespace);

        // Field 5: payload_type (varint, wire type 0)
        WriteTag(buf, 5, 0);
        WriteVarint(buf, (ulong)message.PayloadType);

        // Field 6 / 7: payload_utf8 or payload_binary (wire type 2)
        if (message.PayloadType == CastPayloadType.String && message.PayloadUtf8 != null)
        {
            WriteStringField(buf, 6, message.PayloadUtf8);
        }
        else if (message.PayloadType == CastPayloadType.Binary && message.PayloadBinary != null)
        {
            WriteTag(buf, 7, 2);
            WriteVarint(buf, (ulong)message.PayloadBinary.Length);
            buf.AddRange(message.PayloadBinary);
        }

        return buf.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out CastMessage? message)
    {
        message = null;
        int protocolVersion = 0;
        string? sourceId = null;
        string? destinationId = null;
        string? ns = null;
        int payloadType = -1;
        string? payloadUtf8 = null;
        byte[]? payloadBinary = null;

        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadVarint(data, ref pos, out ulong rawTag)) return false;
            int field = (int)(rawTag >> 3);
            int wire = (int)(rawTag & 0x07);

            switch (field, wire)
            {
                case (1, 0):
                    if (!TryReadVarint(data, ref pos, out ulong ver)) return false;
                    protocolVersion = (int)ver;
                    break;
                case (2, 2):
                    if (!TryReadLengthDelimited(data, ref pos, out var srcBytes)) return false;
                    sourceId = Encoding.UTF8.GetString(srcBytes);
                    break;
                case (3, 2):
                    if (!TryReadLengthDelimited(data, ref pos, out var dstBytes)) return false;
                    destinationId = Encoding.UTF8.GetString(dstBytes);
                    break;
                case (4, 2):
                    if (!TryReadLengthDelimited(data, ref pos, out var nsBytes)) return false;
                    ns = Encoding.UTF8.GetString(nsBytes);
                    break;
                case (5, 0):
                    if (!TryReadVarint(data, ref pos, out ulong pt)) return false;
                    payloadType = (int)pt;
                    break;
                case (6, 2):
                    if (!TryReadLengthDelimited(data, ref pos, out var utf8Bytes)) return false;
                    payloadUtf8 = Encoding.UTF8.GetString(utf8Bytes);
                    break;
                case (7, 2):
                    if (!TryReadLengthDelimited(data, ref pos, out var binBytes)) return false;
                    payloadBinary = binBytes.ToArray();
                    break;
                default:
                    if (!SkipField(data, ref pos, wire)) return false;
                    break;
            }
        }

        if (sourceId == null || destinationId == null || ns == null || payloadType is not (0 or 1))
            return false;

        message = new CastMessage(
            sourceId,
            destinationId,
            ns,
            (CastPayloadType)payloadType,
            payloadUtf8,
            payloadBinary,
            protocolVersion);
        return true;
    }

    public static async Task WriteFrameAsync(Stream stream, CastMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = Encode(message);
        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException($"Cast frame too large ({payload.Length} bytes).");

        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CastMessage?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] lenBuf = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await stream.ReadAsync(lenBuf.AsMemory(read, 4 - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return null;
                throw new EndOfStreamException("Unexpected end of Cast stream while reading length header.");
            }
            read += n;
        }

        uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
        if (len == 0 || len > MaxFrameBytes)
            throw new InvalidDataException($"Invalid Cast frame length: {len}.");

        byte[] payload = new byte[len];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return TryDecode(payload, out var message) && message != null
            ? message
            : throw new InvalidDataException("Malformed CastMessage protobuf payload.");
    }

    private static void WriteTag(List<byte> buf, int fieldNumber, int wireType) =>
        WriteVarint(buf, (uint)((fieldNumber << 3) | wireType));

    private static void WriteStringField(List<byte> buf, int fieldNumber, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(buf, fieldNumber, 2);
        WriteVarint(buf, (ulong)bytes.Length);
        buf.AddRange(bytes);
    }

    private static void WriteVarint(List<byte> buf, ulong value)
    {
        while (value >= 0x80)
        {
            buf.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < data.Length && shift < 64)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static bool TryReadLengthDelimited(ReadOnlySpan<byte> data, ref int pos, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryReadVarint(data, ref pos, out ulong len) || len > (ulong)(data.Length - pos))
            return false;
        slice = data.Slice(pos, (int)len);
        pos += (int)len;
        return true;
    }

    private static bool SkipField(ReadOnlySpan<byte> data, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0:
                return TryReadVarint(data, ref pos, out _);
            case 1:
                if (data.Length - pos < 8) return false;
                pos += 8;
                return true;
            case 2:
                return TryReadLengthDelimited(data, ref pos, out _);
            case 5:
                if (data.Length - pos < 4) return false;
                pos += 4;
                return true;
            default:
                return false;
        }
    }
}
