using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using NodeX.Core.Audio;

namespace NodeX.Core.Cast;

/// <summary>
/// Ephemeral LAN-only HTTP audio + icon server used while casting via Chromecast's built-in Default Media Receiver
/// (<c>CC1AD845</c>). Binds strictly to the interface facing the target Chromecast with a per-session random path token.
/// </summary>
public sealed class CastLanAudioServer : IAsyncDisposable
{
    private const int MaxClients = 2;

    private readonly byte[]? _iconPng;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Channel<byte[]>> _clients = new();
    private readonly Task _acceptTask;
    private int _nextClientId;
    private bool _disposed;

    public CastLanAudioServer(IPAddress castTargetAddress, byte[]? iconPng = null, IPAddress? overrideBindAddress = null)
    {
        ArgumentNullException.ThrowIfNull(castTargetAddress);
        _iconPng = iconPng;

        IPAddress bindIp = overrideBindAddress ?? ResolveLocalAddressFacing(castTargetAddress);
        _listener = new TcpListener(bindIp, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://{bindIp}:{Port}/cast/{_token}";
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }
    public string BaseUrl { get; }
    public string LiveWavUrl => $"{BaseUrl}/live.wav";
    public string IconUrl => $"{BaseUrl}/icon.png";
    public bool HasIcon => _iconPng is { Length: > 0 };

    public void PushMixedFrame(ReadOnlySpan<short> pcm16)
    {
        if (_clients.IsEmpty) return;
        int samples = Math.Min(pcm16.Length, AudioFormat.SamplesPerFrame);
        byte[] bytes = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm16[i]);

        foreach (var ch in _clients.Values)
            ch.Writer.TryWrite(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        foreach (var ch in _clients.Values) ch.Writer.TryComplete();
        _clients.Clear();
        try { await _acceptTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    /// <summary>Builds a 44-byte RIFF/WAVE header configured for continuous 8 kHz mono 16-bit PCM streaming.</summary>
    internal static byte[] BuildStreamingWavHeader()
    {
        byte[] h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF", h.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4, 4), 0x7FFFFFFF);
        Encoding.ASCII.GetBytes("WAVE", h.AsSpan(8, 4));
        Encoding.ASCII.GetBytes("fmt ", h.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20, 2), 1);   // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22, 2), (ushort)AudioFormat.Channels);
        int bytesPerSample = AudioFormat.Channels * (AudioFormat.BitsPerSample / 8);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24, 4), (uint)AudioFormat.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28, 4), (uint)(AudioFormat.SampleRate * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32, 2), (ushort)bytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34, 2), (ushort)AudioFormat.BitsPerSample);
        Encoding.ASCII.GetBytes("data", h.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40, 4), 0x7FFFFF00);
        return h;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            string? requestLine = await ReadRequestLineAsync(stream, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine)) return;

            string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteSimpleResponseAsync(stream, client, 400, "Bad Request", ct).ConfigureAwait(false);
                return;
            }

            string method = parts[0].ToUpperInvariant();
            string path = parts[1].Split('?')[0];

            if (method is not ("GET" or "HEAD"))
            {
                await WriteSimpleResponseAsync(stream, client, 405, "Method Not Allowed", ct).ConfigureAwait(false);
                return;
            }

            if (path == $"/cast/{_token}/icon.png" && _iconPng is { Length: > 0 } png)
            {
                byte[] hdr = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\n" +
                    "Cache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(hdr, ct).ConfigureAwait(false);
                if (method == "GET") await stream.WriteAsync(png, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                SafeShutdownSend(client);
                return;
            }

            if (path != $"/cast/{_token}/live.wav" || _clients.Count >= MaxClients)
            {
                await WriteSimpleResponseAsync(stream, client, 404, "Not Found", ct).ConfigureAwait(false);
                return;
            }

            byte[] wavHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\n" +
                "Cache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");
            if (method == "HEAD")
            {
                await stream.WriteAsync(wavHeaders, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                SafeShutdownSend(client);
                return;
            }

            // Register the client channel before sending the WAV headers so any frame pushed immediately after
            // the client receives the HTTP/RIFF header is already queued rather than dropped while _clients is empty.
            int clientId = Interlocked.Increment(ref _nextClientId);
            var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(25)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _clients[clientId] = ch;
            try
            {
                await stream.WriteAsync(wavHeaders, ct).ConfigureAwait(false);
                await stream.WriteAsync(BuildStreamingWavHeader(), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                await foreach (byte[] pcm in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await stream.WriteAsync(pcm, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _clients.TryRemove(clientId, out var removed);
                removed?.Writer.TryComplete();
            }
        }
        catch
        {
            // Client disconnected
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var lineBuf = new List<byte>(256);
        byte[] one = new byte[1];
        string? firstLine = null;
        int totalBytes = 0;

        while (totalBytes < 8192)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return firstLine;
            totalBytes++;

            byte b = one[0];
            if (b == '\r') continue;
            if (b == '\n')
            {
                if (firstLine == null)
                {
                    firstLine = Encoding.ASCII.GetString(lineBuf.ToArray());
                    lineBuf.Clear();
                    continue;
                }
                if (lineBuf.Count == 0)
                    return firstLine;
                lineBuf.Clear();
                continue;
            }
            lineBuf.Add(b);
        }
        return firstLine;
    }

    private static async Task WriteSimpleResponseAsync(NetworkStream stream, TcpClient client, int code, string text, CancellationToken ct)
    {
        byte[] bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {text}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        SafeShutdownSend(client);
    }

    private static void SafeShutdownSend(TcpClient client)
    {
        try { client.Client.Shutdown(SocketShutdown.Send); }
        catch { }
    }

    private static IPAddress ResolveLocalAddressFacing(IPAddress target)
    {
        if (IPAddress.IsLoopback(target)) return IPAddress.Loopback;
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(target, CastProtocol.DefaultPort);
            if (s.LocalEndPoint is IPEndPoint ep && ep.Address.AddressFamily == AddressFamily.InterNetwork)
                return ep.Address;
        }
        catch
        {
            // Fallback to loopback below
        }
        return IPAddress.Loopback;
    }
}
