using NodeX.Core;

namespace NodeX.Core.Tests;

public class G711UlawTests
{
    [Theory]
    [InlineData(0, 0xFF)]        // positive zero
    [InlineData(-1, 0x7F)]       // negative zero
    [InlineData(32767, 0x80)]    // max positive (clipped)
    [InlineData(-32768, 0x00)]   // min negative (regression: negation overflow)
    [InlineData(-32767, 0x00)]
    public void EncodeSample_KnownValues(int pcm, int expected)
    {
        Assert.Equal((byte)expected, G711Ulaw.EncodeSample((short)pcm));
    }

    [Theory]
    [InlineData(0xFF, 0)]
    [InlineData(0x7F, 0)]
    [InlineData(0x80, 32124)]
    [InlineData(0x00, -32124)]
    public void DecodeSample_KnownValues(int ulaw, int expected)
    {
        Assert.Equal((short)expected, G711Ulaw.DecodeSample((byte)ulaw));
    }

    [Fact]
    public void Decode_ThenEncode_IsIdentityForEveryCode()
    {
        for (int i = 0; i < 256; i++)
        {
            byte code = (byte)i;
            short pcm = G711Ulaw.DecodeSample(code);
            byte back = G711Ulaw.EncodeSample(pcm);
            // 0x7F and 0xFF both decode to 0; canonical re-encode of 0 is 0xFF.
            if (code == 0x7F) Assert.Equal(0xFF, back);
            else Assert.Equal(code, back);
        }
    }

    [Fact]
    public void RoundTrip_ErrorWithinQuantizationStep_ForAllSamples()
    {
        for (int i = short.MinValue; i <= short.MaxValue; i++)
        {
            short pcm = (short)i;
            short decoded = G711Ulaw.DecodeSample(G711Ulaw.EncodeSample(pcm));

            if (Math.Abs((int)pcm) > 1024) Assert.Equal(Math.Sign(pcm), Math.Sign(decoded));
            Assert.InRange(Math.Abs(pcm - decoded), 0, 1100);
        }
    }

    [Fact]
    public void Encode_IsMonotonicInMagnitude()
    {
        short prev = G711Ulaw.DecodeSample(G711Ulaw.EncodeSample(0));
        for (int i = 1; i <= short.MaxValue; i++)
        {
            short cur = G711Ulaw.DecodeSample(G711Ulaw.EncodeSample((short)i));
            Assert.True(cur >= prev, $"Not monotonic at {i}");
            prev = cur;
        }
    }

    [Fact]
    public void Encode_IsAntisymmetric()
    {
        for (int i = 1; i <= short.MaxValue; i++)
        {
            short pos = G711Ulaw.DecodeSample(G711Ulaw.EncodeSample((short)i));
            short neg = G711Ulaw.DecodeSample(G711Ulaw.EncodeSample((short)-i));
            Assert.Equal(-pos, neg);
        }
    }

    [Fact]
    public void SpanEncodeDecode_ProcessesFullFrameAndReturnsCount()
    {
        short[] pcm = new short[G711Ulaw.SamplesPer20MsFrame];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin(i * 0.2) * 12000);

        byte[] ulaw = new byte[G711Ulaw.BytesPer20MsFrame];
        Assert.Equal(160, G711Ulaw.Encode(pcm, ulaw));

        short[] decoded = new short[160];
        Assert.Equal(160, G711Ulaw.Decode(ulaw, decoded));

        for (int i = 0; i < pcm.Length; i++)
            Assert.InRange(Math.Abs(pcm[i] - decoded[i]), 0, 400);
    }

    [Fact]
    public void ArrayOverloads_MatchSampleFunctions()
    {
        short[] pcm = [0, 100, -100, 5000, -5000, 32767, -32768];
        byte[] viaArray = G711Ulaw.Encode(pcm);
        Assert.Equal(pcm.Length, viaArray.Length);
        for (int i = 0; i < pcm.Length; i++)
            Assert.Equal(G711Ulaw.EncodeSample(pcm[i]), viaArray[i]);

        short[] back = G711Ulaw.Decode(viaArray);
        for (int i = 0; i < back.Length; i++)
            Assert.Equal(G711Ulaw.DecodeSample(viaArray[i]), back[i]);
    }

    [Fact]
    public void EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, G711Ulaw.Encode(ReadOnlySpan<short>.Empty, Span<byte>.Empty));
        Assert.Equal(0, G711Ulaw.Decode(ReadOnlySpan<byte>.Empty, Span<short>.Empty));
        Assert.Empty(G711Ulaw.Encode(Array.Empty<short>()));
    }

    [Fact]
    public void Constants_MatchSpec()
    {
        Assert.Equal(8000, G711Ulaw.SampleRate);
        Assert.Equal(1, G711Ulaw.Channels);
        Assert.Equal(16, G711Ulaw.BitsPerSample);
        Assert.Equal(160, G711Ulaw.SamplesPer20MsFrame);
    }
}
