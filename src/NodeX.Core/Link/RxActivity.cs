namespace NodeX.Core;

/// <summary>
/// Which linked nodes are being heard right now. A node that announces its transmissions with KEY/UNKEY control frames
/// is heard only while keyed. A node that never sends KEY (East Coast Reflector) is heard while it sends voice frames
/// that actually contain sound: silent frames (a hub streaming idle audio) do not count. Same rule as the Radio screen's
/// per-node highlight, in Core so the Android Auto service can use it without the UI. Thread-safe; frames arrive on
/// network threads.
/// </summary>
public sealed class RxActivity : IDisposable
{
    /// <summary>How long after the last voice frame with sound a node with no KEY still counts as talking.</summary>
    public static readonly TimeSpan VoiceHold = TimeSpan.FromMilliseconds(2000);

    /// <summary>
    /// RMS level (dBFS) a voice frame must exceed to count as sound. Busy hubs stream low hiss/noise between overs
    /// that a -50 dBFS peak test let through (2026-11-21: three hubs lit with nothing audible); ordinary ASL speech
    /// sits around -30 to -15 dBFS RMS and courtesy tones higher, so -40 dBFS separates them with margin.
    /// </summary>
    public const double SoundRmsDbfs = -40.0;

    private sealed class Watch
    {
        public NodeLink? Link;
        public volatile bool Keyed;
        public long LastVoiceTicks;
        public long LastSoundTicks;
        public long KeyedAtTicks;
        public Action Detach = static () => { };
    }

    private readonly Dictionary<string, Watch> _watches = [];
    private readonly HashSet<string> _keySenders = [];
    private readonly object _gate = new();
    private readonly Func<long> _clock;

    public RxActivity(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>
    /// Raised when a node starts receiving (either via KEY or on a voice-only rising edge after silence).
    /// May fire on network/audio threads.
    /// </summary>
    public event Action<string>? NodeBecameActive;

    /// <summary>
    /// Marks a node that has been seen sending KEY. Kept across reconnects, so a hub that streams frames while idle is
    /// not lit by them just because a fresh call has not seen its first KEY yet.
    /// </summary>
    public const long KnownKeySender = 1;

    /// <summary>Starts following <paramref name="link"/>. Following it again replaces the earlier watch.</summary>
    public void Attach(NodeLink link)
    {
        string node = link.Target.NodeNumber;
        var watch = new Watch { Link = link };

        void OnKeyed(bool keyed)
        {
            if (keyed)
            {
                Interlocked.Exchange(ref watch.KeyedAtTicks, _clock());
                lock (_gate) _keySenders.Add(node);
                watch.Keyed = true;
                try { NodeBecameActive?.Invoke(node); } catch (Exception) { }
            }
            else
            {
                Interlocked.Exchange(ref watch.LastSoundTicks, 0);
                watch.Keyed = false;
            }
        }
        void OnVoice(ushort _, ReadOnlyMemory<byte> payload)
        {
            long now = _clock();
            Interlocked.Exchange(ref watch.LastVoiceTicks, now);
            if (HasSound(payload.Span))
            {
                long prevSound = Interlocked.Exchange(ref watch.LastSoundTicks, now);
                if (Interlocked.Read(ref watch.KeyedAtTicks) == 0
                    && (prevSound == 0 || now - prevSound >= VoiceHold.TotalMilliseconds))
                {
                    try { NodeBecameActive?.Invoke(node); } catch (Exception) { }
                }
            }
        }

        link.Session.RemoteKeyedChanged += OnKeyed;
        link.Session.VoiceFrameReceived += OnVoice;
        watch.Detach = () =>
        {
            link.Session.RemoteKeyedChanged -= OnKeyed;
            link.Session.VoiceFrameReceived -= OnVoice;
        };

        Watch? old;
        lock (_gate)
        {
            if (_keySenders.Contains(node)) watch.KeyedAtTicks = KnownKeySender;
            _watches.TryGetValue(node, out old);
            _watches[node] = watch;
        }
        old?.Detach();
    }

    /// <summary>Stops following the node's link.</summary>
    public void Detach(string node)
    {
        Watch? w;
        lock (_gate)
        {
            if (!_watches.Remove(node, out w)) return;
        }
        w.Detach();
    }

    /// <summary>Stops following <paramref name="link"/> only if it is still the active watch for its node.</summary>
    public void Detach(NodeLink link)
    {
        string node = link.Target.NodeNumber;
        Watch? w;
        lock (_gate)
        {
            if (!_watches.TryGetValue(node, out var current) || !ReferenceEquals(current.Link, link)) return;
            _watches.Remove(node, out w);
        }
        w?.Detach();
    }

    /// <summary>Nodes heard right now, in the order they were attached.</summary>
    public IReadOnlyList<string> Talking()
    {
        long now = _clock();
        lock (_gate)
            return _watches.Where(kv => IsActive(kv.Value, now)).Select(kv => kv.Key).ToArray();
    }

    public bool IsReceiving(string node)
    {
        long now = _clock();
        lock (_gate)
            return _watches.TryGetValue(node, out var w) && IsActive(w, now);
    }

    private static bool IsActive(Watch w, long now) =>
        IsHeard(w.Keyed, Interlocked.Read(ref w.KeyedAtTicks), Interlocked.Read(ref w.LastVoiceTicks),
            Interlocked.Read(ref w.LastSoundTicks), now);

    /// <summary>How long a KEY with no voice frames at all is believed. A node that keys carries voice continuously, so
    /// silence this long means the UNKEY was lost or never sent and the node must not stay lit forever.</summary>
    public static readonly TimeSpan KeyStale = TimeSpan.FromSeconds(3);

    /// <summary>True when a µ-law voice payload is louder than background noise (RMS above <see cref="SoundRmsDbfs"/>).</summary>
    public static bool HasSound(ReadOnlySpan<byte> ulaw) => RmsDbfs(ulaw) > SoundRmsDbfs;

    /// <summary>RMS level of a µ-law payload in dBFS (16-bit full scale); negative infinity for silence or no samples.</summary>
    public static double RmsDbfs(ReadOnlySpan<byte> ulaw)
    {
        if (ulaw.IsEmpty) return double.NegativeInfinity;
        double sum = 0;
        foreach (byte b in ulaw)
        {
            int s = G711Ulaw.DecodeSample(b);
            sum += (double)s * s;
        }
        double rms = Math.Sqrt(sum / ulaw.Length);
        return rms <= 0 ? double.NegativeInfinity : 20 * Math.Log10(rms / 32768.0);
    }

    /// <summary>
    /// The single "is this node being heard" rule, shared with the Radio screen. <paramref name="lastVoiceTicks"/> is
    /// the last voice frame of any kind (keeps a KEY alive through pauses); <paramref name="lastSoundTicks"/> the last
    /// frame with sound (the only thing that lights a node that never sends KEY). <paramref name="keyedAtTicks"/> is
    /// non-zero once the node is known to send KEY (<see cref="KnownKeySender"/> after a reconnect).
    /// </summary>
    public static bool IsHeard(bool keyed, long keyedAtTicks, long lastVoiceTicks, long lastSoundTicks, long now)
    {
        if (keyedAtTicks != 0)
        {
            // This node has sent a KEY frame, so it announces its transmissions and its UNKEY is believed. Voice frames
            // after UNKEY (a hub relaying silence or noise) must not keep it lit.
            if (!keyed) return false;
            return now - Math.Max(keyedAtTicks, lastVoiceTicks) < KeyStale.TotalMilliseconds;
        }
        return lastSoundTicks != 0 && now - lastSoundTicks < VoiceHold.TotalMilliseconds;
    }

    public void Dispose()
    {
        List<Watch> all;
        lock (_gate)
        {
            all = [.. _watches.Values];
            _watches.Clear();
        }
        foreach (var w in all) w.Detach();
    }
}
