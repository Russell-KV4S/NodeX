using System.Net;
using System.Net.Sockets;

namespace NodeX.Core;

/// <summary>
/// One UDP socket shared by every IAX2 call, the IAX2 registration and the STUN network check.
/// <para>
/// Why: AllStarLink nodes accept a link only when it comes from the IP address the node registered from. On
/// carrier-grade NAT each separate socket (and the HTTPS registration's TCP connection) can leave through a
/// different public address, so the registry records one address and the far node sees the call come from
/// another, and hangs up ("does not match link IP"). Registering and calling from this one socket gives the far
/// node and the registry the same NAT mapping on any carrier that keeps one public address per socket.
/// </para>
/// Datagrams are handed to whoever subscribed to the sender's address; anything from an address nobody
/// subscribed to (scans, inbound link attempts) is dropped, which keeps the outbound-only design.
/// </summary>
public sealed class Iax2Endpoint : IDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<IPEndPoint, List<Action<byte[]>>> _subscribers = [];   // guarded by _gate
    private readonly HashSet<ushort> _callNumbers = [];                                 // guarded by _gate
    private readonly Task _receiveLoop;
    private int _users;                                                                 // guarded by _gate
    private bool _retired;                                                              // guarded by _gate
    private bool _disposed;                                                             // guarded by _gate

    /// <summary>Winsock SIO_UDP_CONNRESET control code.</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <param name="preferredLocalPort">Local port to try first (0 = any); falls back to any free port.</param>
    public Iax2Endpoint(int preferredLocalPort = 0)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 256 * 1024,
            SendBufferSize = 256 * 1024
        };
        if (preferredLocalPort is > 0 and <= 65535)
        {
            try { _socket.Bind(new IPEndPoint(IPAddress.Any, preferredLocalPort)); }
            catch (SocketException) { _socket.Bind(new IPEndPoint(IPAddress.Any, 0)); }
        }
        else
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        // See Iax2Session: an ICMP "port unreachable" must not surface as a receive error on Windows.
        if (OperatingSystem.IsWindows())
        {
            try { _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException) { }
        }

        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Local UDP port of the shared socket.</summary>
    public int LocalPort => (_socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <summary>True once the endpoint has been closed.</summary>
    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>Sends one datagram from the shared socket.</summary>
    public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> packet, EndPoint remote, CancellationToken cancellationToken = default) =>
        _socket.SendToAsync(packet, SocketFlags.None, remote, cancellationToken);

    /// <summary>
    /// Delivers every datagram from <paramref name="remote"/> to <paramref name="handler"/> (a private copy each),
    /// on the receive thread, until the returned handle is disposed. Several subscribers may share one remote
    /// address (two calls to nodes on the same server); each filters by call number itself.
    /// </summary>
    public IDisposable Subscribe(IPEndPoint remote, Action<byte[]> handler)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(handler);
        var key = Normalize(remote);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_subscribers.TryGetValue(key, out var list)) _subscribers[key] = list = [];
            list.Add(handler);
        }
        return new Subscription(this, key, handler);
    }

    /// <summary>
    /// Reserves a call number (1..32767) no other call or registration on this socket is using. Calls on one
    /// socket must not share a number: the far end and this endpoint tell them apart by it.
    /// </summary>
    public ushort AllocateCallNumber()
    {
        lock (_gate)
        {
            for (int i = 0; i < 1000; i++)
            {
                ushort n = (ushort)Random.Shared.Next(1, 0x7FFF);
                if (_callNumbers.Add(n)) return n;
            }
            throw new InvalidOperationException("No free IAX2 call number on this socket.");
        }
    }

    /// <summary>Returns a number from <see cref="AllocateCallNumber"/>.</summary>
    public void ReleaseCallNumber(ushort callNumber)
    {
        lock (_gate) _callNumbers.Remove(callNumber);
    }

    /// <summary>
    /// Counts a call or registrar that uses this socket. A <see cref="Retire">retired</see> endpoint closes when the
    /// last user releases it, so calls still running on an old socket after a network change are not cut off.
    /// Returns false when the endpoint is already closed.
    /// </summary>
    public bool AddUser()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            _users++;
            return true;
        }
    }

    /// <summary>Releases a use counted by <see cref="AddUser"/>.</summary>
    public void ReleaseUser()
    {
        bool close;
        lock (_gate)
        {
            if (_users > 0) _users--;
            close = _retired && _users == 0 && !_disposed;
        }
        if (close) Dispose();
    }

    /// <summary>
    /// No new users will come (the network changed and a fresh socket replaces this one): close as soon as nobody
    /// uses it any more.
    /// </summary>
    public void Retire()
    {
        bool close;
        lock (_gate)
        {
            _retired = true;
            close = _users == 0 && !_disposed;
        }
        if (close) Dispose();
    }

    private static IPEndPoint Normalize(IPEndPoint ep) =>
        ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    private void Unsubscribe(IPEndPoint key, Action<byte[]> handler)
    {
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(key, out var list)) return;
            list.Remove(handler);
            if (list.Count == 0) _subscribers.Remove(key);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        int consecutiveErrors = 0;

        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                // One bad receive (an ICMP error surfacing here on some platforms) must not stop every call on the
                // socket. Back off a little when errors repeat; the calls' own watchdogs decide if the path is dead.
                if (_cts.IsCancellationRequested) return;
                consecutiveErrors++;
                try { await Task.Delay(Math.Min(1000, 20 * consecutiveErrors), _cts.Token); }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
                continue;
            }

            if (result.ReceivedBytes <= 0 || result.RemoteEndPoint is not IPEndPoint from) continue;

            Action<byte[]>[] handlers;
            lock (_gate)
            {
                if (!_subscribers.TryGetValue(Normalize(from), out var list) || list.Count == 0) continue;
                handlers = list.ToArray();
            }

            foreach (var handler in handlers)
            {
                byte[] copy = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                try { handler(copy); }
                catch (Exception) { /* one subscriber must never take the shared socket down */ }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _subscribers.Clear();
        }
        // Not waiting for the receive loop: Dispose can run on that very thread (the last user released from a
        // handler). Closing the socket ends the loop; the token source holds no timer, so it is left to the GC.
        _cts.Cancel();
        _socket.Dispose();
    }

    /// <summary>Test seam: completes when the receive loop has ended.</summary>
    internal Task ReceiveLoop => _receiveLoop;

    private sealed class Subscription(Iax2Endpoint owner, IPEndPoint key, Action<byte[]> handler) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(key, handler);
        }
    }
}
