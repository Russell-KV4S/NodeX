namespace NodeX.Core;

/// <summary>
/// Which linked nodes are being heard right now. A node counts as receiving when its KEY control frame is active OR
/// a voice frame arrived in the last <see cref="VoiceHold"/>: some nodes send audio without (or before) a KEY frame, so
/// following the control frames alone left them looking silent. Same rule as the Radio screen's per-node highlight, in
/// Core so the Android Auto service can use it without the UI. Thread-safe; frames arrive on network threads.
/// </summary>
public sealed class RxActivity : IDisposable
{
    /// <summary>How long after the last voice frame a node still counts as talking.</summary>
    public static readonly TimeSpan VoiceHold = TimeSpan.FromMilliseconds(500);

    private sealed class Watch
    {
        public volatile bool Keyed;
        public long LastVoiceTicks;
        public Action Detach = static () => { };
    }

    private readonly Dictionary<string, Watch> _watches = [];
    private readonly object _gate = new();
    private readonly Func<long> _clock;

    public RxActivity(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>Starts following <paramref name="link"/>. Following it again replaces the earlier watch.</summary>
    public void Attach(NodeLink link)
    {
        string node = link.Target.NodeNumber;
        var watch = new Watch();

        void OnKeyed(bool keyed) => watch.Keyed = keyed;
        void OnVoice(ushort _, ReadOnlyMemory<byte> __) => Interlocked.Exchange(ref watch.LastVoiceTicks, _clock());

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

    private static bool IsActive(Watch w, long now)
    {
        long last = Interlocked.Read(ref w.LastVoiceTicks);
        return w.Keyed || (last != 0 && now - last < VoiceHold.TotalMilliseconds);
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
