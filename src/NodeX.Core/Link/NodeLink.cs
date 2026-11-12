using System.Diagnostics;
using NodeX.Core.Audio;
using NodeX.Core.Settings;

namespace NodeX.Core;

/// <summary>
/// One live connection to a remote ASL node: the IAX2 session, the PTT state machine and the audio
/// engine, created together and torn down together. A link is single-use; reconnecting means
/// creating a new one (an <see cref="Iax2Session"/> owns sockets and background loops).
/// </summary>
public sealed class NodeLink : IAsyncDisposable
{
    /// <summary>
    /// IAX2 user that ASL nodes accept for node-to-node links ([radio] in iax.conf). It has no secret:
    /// the called node validates the caller in its dialplan. The node password is used only for registration.
    /// </summary>
    public const string LinkUsername = "radio";

    private readonly IAudioDeviceService _device;
    private int _audioStarted;
    private volatile bool _disposed;

    /// <summary>Set by the runtime when it starts dealing with this call's end, so that happens exactly once.</summary>
    internal int EndHandled;

    public NodeLink(
        NodeSettings settings,
        NodeInfo target,
        IAudioDeviceService device,
        NodeAudioEngineOptions? baseAudioOptions = null,
        Action<Iax2Session>? configureSession = null,
        TransmitPolicy? transmitPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(target);
        _device = device ?? throw new ArgumentNullException(nameof(device));
        Target = target;

        Session = new Iax2Session
        {
            LocalNodeNumber = settings.NodeNumber,
            RemoteNodeNumber = target.NodeNumber,
            Username = LinkUsername
        };

        configureSession?.Invoke(Session);

        Ptt = new NodePttController(Session)
        {
            Mode = settings.TogglePtt ? PttMode.Toggle : PttMode.Momentary,
            TimeOutTimerLimit = TimeSpan.FromSeconds(settings.TimeOutTimerSeconds),
            Policy = transmitPolicy
        };

        Audio = new NodeAudioEngine(Session, device, (baseAudioOptions ?? new NodeAudioEngineOptions()) with
        {
            MicGain = settings.MicGain,
            MicProcessing = settings.MicProcessing
        });

        Session.StateChanged += OnSessionStateChanged;
        Session.NodeMessageReceived += OnNodeMessageReceived;
        Session.ErrorOccurred += m => Error?.Invoke(m);
        Audio.ErrorOccurred += m => Error?.Invoke($"Audio: {m}");
    }

    public NodeInfo Target { get; }

    /// <summary>Other nodes currently linked to this remote peer (from app_rpt <c>L</c> / <c>STATUS</c> text frames, excluding our own node).</summary>
    public IReadOnlyList<AslRemoteLink> RemoteLinkedNodes { get; private set; } = [];

    /// <summary>Short summary of <see cref="RemoteLinkedNodes"/> for UI display.</summary>
    public string RemoteLinkedSummary => AslTextParser.FormatLinkedNodes(RemoteLinkedNodes, Session.LocalNodeNumber);

    /// <summary>Raised when an app_rpt text frame arrives and is parsed.</summary>
    public event Action<AslTextMessage>? TelemetryReceived;

    /// <summary>True once the call has reached Connected at least once (as opposed to never being answered).</summary>
    public bool WasConnected { get; private set; }

    private long _connectedAt;
    private long _endedAt;

    /// <summary>How long the call was (or has been) in the Connected state; zero if it never connected.</summary>
    public TimeSpan ConnectedDuration
    {
        get
        {
            long start = Volatile.Read(ref _connectedAt);
            if (start == 0) return TimeSpan.Zero;
            long end = Volatile.Read(ref _endedAt);
            return end != 0 ? Stopwatch.GetElapsedTime(start, end) : Stopwatch.GetElapsedTime(start);
        }
    }

    /// <summary>Why the call ended; meaningful once the session is Disconnected.</summary>
    public Iax2DisconnectReason DisconnectReason => Session.DisconnectReason;

    /// <summary>Reason text/code the remote gave when it hung up or rejected, or empty.</summary>
    public string RemoteCause => Session.RemoteCause;
    public Iax2Session Session { get; }
    public NodePttController Ptt { get; }
    public NodeAudioEngine Audio { get; }

    /// <summary>Session or audio errors, already formatted for display.</summary>
    public event Action<string>? Error;

    /// <summary>Starts the call; the returned task completes once the first packet is sent.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        Session.ConnectAsync(Target.EndPoint, cancellationToken);

    private void OnSessionStateChanged(Iax2SessionState state)
    {
        if (state == Iax2SessionState.Connected)
        {
            WasConnected = true;
            Interlocked.CompareExchange(ref _connectedAt, Stopwatch.GetTimestamp(), 0);
        }
        else if (state == Iax2SessionState.Disconnected)
        {
            Interlocked.CompareExchange(ref _endedAt, Stopwatch.GetTimestamp(), 0);
        }

        // Audio runs only while the call is up. Start it off the receive thread: opening the
        // microphone can block, and it must not stall packet processing.
        if (state == Iax2SessionState.Connected && Interlocked.Exchange(ref _audioStarted, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!_disposed) Audio.Start();
                }
                catch (Exception ex)
                {
                    Error?.Invoke($"Audio could not start: {ex.Message}");
                }
            });
        }
    }

    private void OnNodeMessageReceived(string raw)
    {
        var parsed = AslTextParser.Parse(raw);
        if (parsed.Kind is AslTextMessageKind.LinkList or AslTextMessageKind.Status)
        {
            RemoteLinkedNodes = parsed.LinkedNodes
                .Where(l => !string.Equals(l.NodeNumber, Session.LocalNodeNumber, StringComparison.Ordinal))
                .ToArray();
        }
        TelemetryReceived?.Invoke(parsed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Session.StateChanged -= OnSessionStateChanged;
        Session.NodeMessageReceived -= OnNodeMessageReceived;
        await Ptt.DisposeAsync();      // unkeys first, while the session can still send
        await Audio.DisposeAsync();
        await Session.DisposeAsync();
        await _device.DisposeAsync();
    }
}
