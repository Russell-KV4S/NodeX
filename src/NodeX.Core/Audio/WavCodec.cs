using System.Buffers.Binary;

namespace NodeX.Core.Audio;

/// <summary>
/// Minimal 16-bit signed mono PCM WAV encoder/decoder for saved NodeX audio clips (8000 Hz by default).
/// Uses standard uncompressed PCM (format tag 1) so exported clips play natively on Android, iOS, Windows, macOS, and Linux.
/// </summary>
public static class WavCodec
{
    public const int HeaderBytes = 44;

    public static byte[] EncodePcm16(ReadOnlySpan<short> samples, int sampleRate = AudioFormat.SampleRate)
    {
        int dataBytes = samples.Length * sizeof(short);
        byte[] wav = new byte[HeaderBytes + dataBytes];
        WriteHeader(wav.AsSpan(0, HeaderBytes), dataBytes, sampleRate);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(HeaderBytes + i * 2, 2), samples[i]);
        return wav;
    }

    public static void WritePcm16(Stream stream, ReadOnlySpan<short> samples, int sampleRate = AudioFormat.SampleRate)
    {
        byte[] wav = EncodePcm16(samples, sampleRate);
        stream.Write(wav, 0, wav.Length);
    }

    /// <summary>
    /// Reads a seekable 16-bit mono PCM WAV stream without loading its audio payload into memory,
    /// returning the sample rate and the byte range of the <c>data</c> chunk.
    /// </summary>
    public static bool TryReadPcm16DataChunk(Stream stream, out int sampleRate, out long dataOffset, out int dataBytes)
    {
        sampleRate = 0;
        dataOffset = 0;
        dataBytes = 0;
        if (!stream.CanSeek || stream.Length < HeaderBytes) return false;

        stream.Position = 0;
        Span<byte> riff = stackalloc byte[12];
        if (ReadExact(stream, riff) < 12
            || riff[0] != (byte)'R' || riff[1] != (byte)'I' || riff[2] != (byte)'F' || riff[3] != (byte)'F'
            || riff[8] != (byte)'W' || riff[9] != (byte)'A' || riff[10] != (byte)'V' || riff[11] != (byte)'E')
            return false;

        ushort audioFormat = 0;
        ushort channels = 0;
        ushort bitsPerSample = 0;
        bool hasFmt = false;
        bool hasData = false;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> fmtBuf = stackalloc byte[16];
        long len = stream.Length;

        while (stream.Position + 8 <= len)
        {
            if (ReadExact(stream, chunkHeader) < 8) return false;
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.Slice(4, 4));
            if (chunkSize < 0 || stream.Position + chunkSize > len) return false;

            long nextPos = stream.Position + chunkSize + (chunkSize & 1);
            if (chunkHeader[0] == (byte)'f' && chunkHeader[1] == (byte)'m' && chunkHeader[2] == (byte)'t' && chunkHeader[3] == (byte)' '
                && chunkSize >= 16)
            {
                if (ReadExact(stream, fmtBuf) < 16) return false;
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.Slice(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmtBuf.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.Slice(14, 2));
                hasFmt = true;
            }
            else if (chunkHeader[0] == (byte)'d' && chunkHeader[1] == (byte)'a' && chunkHeader[2] == (byte)'t' && chunkHeader[3] == (byte)'a')
            {
                dataOffset = stream.Position;
                dataBytes = chunkSize - (chunkSize & 1); // ensure even PCM16 byte count
                hasData = true;
            }

            if (hasFmt && hasData) break;
            if (nextPos > len) return false;
            stream.Position = nextPos;
        }

        return hasFmt && hasData && audioFormat == 1 && channels == 1 && bitsPerSample == 16 && sampleRate > 0;
    }

    public static short[] DecodePcm16(ReadOnlySpan<byte> wav, out int sampleRate)
    {
        if (wav.Length < HeaderBytes
            || wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F'
            || wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
            throw new InvalidDataException("Not a valid WAV file.");

        ushort audioFormat = 0;
        ushort channels = 0;
        ushort bitsPerSample = 0;
        sampleRate = 0;
        ReadOnlySpan<byte> data = default;
        bool hasFmt = false;
        bool hasData = false;

        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(pos + 4, 4));
            pos += 8;
            if (chunkSize < 0 || pos + chunkSize > wav.Length)
                throw new InvalidDataException("WAV chunk is truncated.");

            if (id[0] == (byte)'f' && id[1] == (byte)'m' && id[2] == (byte)'t' && id[3] == (byte)' ' && chunkSize >= 16)
            {
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(pos, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(pos + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(pos + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(pos + 14, 2));
                hasFmt = true;
            }
            else if (id[0] == (byte)'d' && id[1] == (byte)'a' && id[2] == (byte)'t' && id[3] == (byte)'a')
            {
                data = wav.Slice(pos, chunkSize);
                hasData = true;
            }

            pos += chunkSize + (chunkSize & 1);
        }

        if (!hasFmt || !hasData || audioFormat != 1 || channels != 1 || bitsPerSample != 16 || sampleRate <= 0)
            throw new InvalidDataException("Unsupported WAV format (expected 16-bit mono PCM).");

        int count = data.Length / 2;
        short[] samples = new short[count];
        for (int i = 0; i < count; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
        return samples;
    }

    public static void WriteHeader(Span<byte> header, int dataBytes, int sampleRate)
    {
        if (header.Length < HeaderBytes)
            throw new ArgumentException($"Header buffer must be at least {HeaderBytes} bytes.", nameof(header));

        // RIFF header
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), 36 + dataBytes);
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';
        // fmt chunk
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), 16);                  // PCM fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 1);                  // PCM format
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), 1);                  // mono
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), sampleRate);          // sample rate
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), sampleRate * 2);      // byte rate
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), 2);                  // block align
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), 16);                 // bits per sample
        // data chunk
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), dataBytes);
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
