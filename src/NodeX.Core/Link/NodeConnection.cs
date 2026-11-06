namespace NodeX.Core;

/// <summary>
/// One node the user wants to be linked to, kept across failures. It outlives the individual <see cref="NodeLink"/>
/// (a link is single-use): while the connection is auto-reconnecting <see cref="Link"/> is null or a fresh attempt.
/// <see cref="NodeRuntime"/> owns and mutates these; everything public here is read-only for UIs.
/// </summary>
public sealed class NodeConnection
{
    private NodeLink? _link;
    private volatile bool _wanted = true;

    internal NodeConnection(string node) => Node = node;

    /// <summary>The remote node number.</summary>
    public string Node { get; }

    /// <summary>Directory data for the node, once resolved.</summary>
    public NodeInfo? Info { get; internal set; }

    /// <summary>The current call, or null between attempts.</summary>
    public NodeLink? Link
    {
        get => Volatile.Read(ref _link);
        internal set => Volatile.Write(ref _link, value);
    }

    /// <summary>What auto-reconnect is doing for this node.</summary>
    public ReconnectStatus Reconnect { get; internal set; } = ReconnectStatus.Inactive;

    /// <summary>True for the one node the user's voice goes to; every other link is listen-only.</summary>
    public bool IsTransmitTarget { get; internal set; }

    /// <summary>True when received audio from this node is silenced (it is still received and shown).</summary>
    public bool Muted { get; internal set; }

    // ----- supervisor state, used only by NodeRuntime -----

    /// <summary>The user wants this node linked (true until they hang up or recovery gives up).</summary>
    internal bool Wanted
    {
        get => _wanted;
        set => _wanted = value;
    }

    /// <summary>Consecutive "remote hung up right after ACCEPT" outcomes.</summary>
    internal int EarlyHangups;

    internal readonly object ReconnectLock = new();
    internal CancellationTokenSource? ReconnectCts;
    internal Task ReconnectTask = Task.CompletedTask;

    /// <summary>Clears <see cref="Link"/> only if it is still <paramref name="link"/>.</summary>
    internal bool TryClearLink(NodeLink link) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _link, null, link), link);
}
