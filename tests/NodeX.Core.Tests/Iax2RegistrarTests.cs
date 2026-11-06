using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

/// <summary>Drives <see cref="Iax2Registrar"/> against a scripted fake registry on loopback.</summary>
public class Iax2RegistrarTests
{
    private sealed class FakeRegistry : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
        public void Dispose() => _udp.Dispose();

        public async Task<(Iax2Frame frame, IPEndPoint from)> ReceiveAsync()
        {
            while (true)
            {
                var r = await _udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                if (TryParseFrame(r.Buffer, out var f) && f.IsFullFrame
                    && !(f.FrameType == FrameType.IaxControl && f.Subclass == (byte)IaxControlSubclass.Ack))
                    return (f, r.RemoteEndPoint);
            }
        }

        public Task SendAsync(IPEndPoint to, ushort dest, byte subclass, byte oseq, byte iseq, params Iax2InformationElement[] ies)
        {
            byte[] ieBuf = new byte[300];
            int n = WriteInformationElements(ieBuf, ies);
            byte[] buf = new byte[FullHeaderSize + n];
            WriteFullFrame(buf, 77, dest, 0, oseq, iseq, FrameType.IaxControl, subclass, ieBuf.AsSpan(0, n));
            return _udp.SendAsync(buf, to).AsTask();
        }
    }

    private static string Str(Iax2Frame f, InformationElementId id) =>
        Encoding.ASCII.GetString(ParseInformationElements(f.Payload).First(i => i.Id == id).Data);

    [Fact]
    public async Task Registers_WithMd5Challenge_AndReadsApparentAddress()
    {
        using var registry = new FakeRegistry();
        using var reg = new Iax2Registrar("523802", "s3cret", registry.EndPoint) { ReplyTimeout = TimeSpan.FromSeconds(2) };

        var task = reg.RegisterOnceAsync();

        var (req1, from) = await registry.ReceiveAsync();
        Assert.Equal((byte)IaxControlSubclass.RegReq, req1.Subclass);
        Assert.Equal("523802", Str(req1, InformationElementId.Username));
        Assert.DoesNotContain(ParseInformationElements(req1.Payload), i => i.Id == InformationElementId.Md5Result);

        await registry.SendAsync(from, req1.SourceCallNumber, (byte)IaxControlSubclass.RegAuth, 0, 1,
            Iax2InformationElement.UInt16(InformationElementId.AuthMethods, 2),
            Iax2InformationElement.String(InformationElementId.Challenge, "12345678"));

        var (req2, _) = await registry.ReceiveAsync();
        Assert.Equal((byte)IaxControlSubclass.RegReq, req2.Subclass);
        string expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("12345678s3cret"))).ToLowerInvariant();
        Assert.Equal(expected, Str(req2, InformationElementId.Md5Result));
        Assert.Equal(1, req2.OSeqno);

        byte[] addr = [0, 2, 0x11, 0xD9, 203, 0, 113, 5, 0, 0, 0, 0, 0, 0, 0, 0];   // 203.0.113.5:4569
        await registry.SendAsync(from, req2.SourceCallNumber, (byte)IaxControlSubclass.RegAck, 1, 2,
            Iax2InformationElement.Raw(InformationElementId.ApparentAddr, addr),
            Iax2InformationElement.UInt16(InformationElementId.Refresh, 45));

        Assert.True(await task);
        Assert.True(reg.IsRegistered);
        Assert.Equal(IPAddress.Parse("203.0.113.5"), reg.PerceivedAddress);
        Assert.Equal(4569, reg.PerceivedPort);
        Assert.Equal(45, reg.RefreshSeconds);   // server asked for a shorter refresh
    }

    [Fact]
    public async Task EchoesCallToken_ThenRegisters()
    {
        using var registry = new FakeRegistry();
        using var reg = new Iax2Registrar("523802", "pw", registry.EndPoint) { ReplyTimeout = TimeSpan.FromSeconds(2) };
        var task = reg.RegisterOnceAsync();

        var (req1, from) = await registry.ReceiveAsync();
        await registry.SendAsync(from, req1.SourceCallNumber, (byte)IaxControlSubclass.CallToken, 0, 0,
            Iax2InformationElement.Raw(InformationElementId.CallToken, [9, 8, 7]));

        var (req2, _) = await registry.ReceiveAsync();
        Assert.Equal(new byte[] { 9, 8, 7 },
            ParseInformationElements(req2.Payload).First(i => i.Id == InformationElementId.CallToken).Data);

        await registry.SendAsync(from, req2.SourceCallNumber, (byte)IaxControlSubclass.RegAck, 0, 1);
        Assert.True(await task);
    }

    [Fact]
    public async Task Rejected_ReportsFailure()
    {
        using var registry = new FakeRegistry();
        using var reg = new Iax2Registrar("523802", "pw", registry.EndPoint) { ReplyTimeout = TimeSpan.FromSeconds(2) };
        var task = reg.RegisterOnceAsync();

        var (req1, from) = await registry.ReceiveAsync();
        await registry.SendAsync(from, req1.SourceCallNumber, (byte)IaxControlSubclass.RegRej, 0, 1,
            Iax2InformationElement.String(InformationElementId.Cause, "Registration Refused"));

        Assert.False(await task);
        Assert.Contains("Registration Refused", reg.LastStatusMessage);
        Assert.False(reg.IsRegistered);
    }

    [Fact]
    public async Task NoReply_ReportsFailure()
    {
        using var registry = new FakeRegistry();
        using var reg = new Iax2Registrar("523802", "pw", registry.EndPoint) { ReplyTimeout = TimeSpan.FromMilliseconds(150) };

        Assert.False(await reg.RegisterOnceAsync());
        Assert.Contains("No reply", reg.LastStatusMessage);
    }
}
