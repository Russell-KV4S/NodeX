using System.Runtime.CompilerServices;

namespace NodeX.Core;

/// <summary>
/// High-performance G.711 µ-law (PCMU) codec helper for 8000 Hz 16-bit linear mono PCM audio.
/// Implements standard ITU-T G.711 companding specifications with precomputed lookup tables
/// for zero-allocation transcoding.
/// </summary>
public static class G711Ulaw
{
    public const int SampleRate = 8000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int SamplesPer20MsFrame = 160;
    public const int BytesPer20MsFrame = 160;

    private const int Bias = 0x84; // 132
    private const int Clip = 32635;

    private static readonly short[] UlawToLinearTable = new short[256];
    private static readonly byte[] LinearToUlawTable = new byte[65536];

    static G711Ulaw()
    {
        // Precompute µ-law to Linear 16-bit PCM lookup table
        for (int i = 0; i < 256; i++)
        {
            byte uVal = (byte)~i;
            int sign = (uVal & 0x80) != 0 ? -1 : 1;
            int exponent = (uVal >> 4) & 0x07;
            int mantissa = uVal & 0x0F;

            int sample = ((mantissa << 3) + Bias) << exponent;
            sample -= Bias;

            UlawToLinearTable[i] = (short)(sign * sample);
        }

        // Precompute Linear 16-bit PCM to µ-law lookup table
        for (int i = 0; i < 65536; i++)
        {
            short pcm = (short)i;
            LinearToUlawTable[i] = EncodeSampleDirect(pcm);
        }
    }

    /// <summary>
    /// Encodes a single 16-bit linear PCM sample to an 8-bit µ-law byte via table lookup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte EncodeSample(short pcmSample)
    {
        return LinearToUlawTable[(ushort)pcmSample];
    }

    /// <summary>
    /// Decodes a single 8-bit µ-law byte to a 16-bit linear PCM sample via table lookup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short DecodeSample(byte ulawByte)
    {
        return UlawToLinearTable[ulawByte];
    }

    /// <summary>
    /// Encodes a span of 16-bit PCM samples into an output span of µ-law bytes.
    /// </summary>
    public static int Encode(ReadOnlySpan<short> pcmInput, Span<byte> ulawOutput)
    {
        int length = Math.Min(pcmInput.Length, ulawOutput.Length);
        for (int i = 0; i < length; i++)
        {
            ulawOutput[i] = EncodeSample(pcmInput[i]);
        }
        return length;
    }

    /// <summary>
    /// Decodes a span of µ-law bytes into an output span of 16-bit linear PCM samples.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> ulawInput, Span<short> pcmOutput)
    {
        int length = Math.Min(ulawInput.Length, pcmOutput.Length);
        for (int i = 0; i < length; i++)
        {
            pcmOutput[i] = DecodeSample(ulawInput[i]);
        }
        return length;
    }

    /// <summary>
    /// Encodes an array of 16-bit PCM samples into a newly allocated µ-law byte array.
    /// </summary>
    public static byte[] Encode(short[] pcmInput)
    {
        byte[] output = new byte[pcmInput.Length];
        Encode(pcmInput, output);
        return output;
    }

    /// <summary>
    /// Decodes an array of µ-law bytes into a newly allocated 16-bit PCM sample array.
    /// </summary>
    public static short[] Decode(byte[] ulawInput)
    {
        short[] output = new short[ulawInput.Length];
        Decode(ulawInput, output);
        return output;
    }

    /// <summary>
    /// Direct bit-manipulation encoder for ITU-T G.711 µ-law.
    /// </summary>
    private static byte EncodeSampleDirect(short pcm)
    {
        int sample = pcm; // widen to int so -32768 negates without overflow
        int sign = (sample >> 8) & 0x80;
        if (sign != 0)
        {
            sample = -sample;
        }

        if (sample > Clip)
        {
            sample = Clip;
        }

        sample += Bias;
        int exponent = 7;
        for (int expMask = 0x4000; (sample & expMask) == 0 && exponent > 0; expMask >>= 1)
        {
            exponent--;
        }

        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        byte ulawByte = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)~ulawByte;
    }
}
