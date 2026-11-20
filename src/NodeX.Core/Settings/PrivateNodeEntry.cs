namespace NodeX.Core.Settings;

/// <summary>
/// A manually addressed (private) node saved in settings: node number, host (DNS name or IP), IAX port and an
/// optional friendly name. Holds no secret (NodeX always logs in as IAX user <c>radio</c> with no password).
/// </summary>
public sealed record PrivateNodeEntry
{
    public string Node { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = Iax2Protocol.DefaultPort;
    public string? Name { get; init; }

    public PrivateNodeEntry() { }

    public PrivateNodeEntry(string node, string host, int port = Iax2Protocol.DefaultPort, string? name = null)
    {
        Node = node;
        Host = host;
        Port = port;
        Name = name;
    }

    /// <summary>Display form for link rows and logs: <c>1999 @ host</c> (or <c>1999 @ host:4570</c> for non-default port).</summary>
    public string DisplayAddress => ManualNodeAddress.FormatDisplay(Node, Host, Port);

    /// <summary>Compact form accepted by <see cref="ManualNodeAddress.TryParse"/>: <c>1999@host</c> or <c>1999@host:4570</c>.</summary>
    public string CanonicalInput => ManualNodeAddress.FormatCanonical(Node, Host, Port);

    /// <summary>Identity key for deduplication and matching: (Node, lower-cased Host, Port).</summary>
    public (string Node, string Host, int Port) Key =>
        ((Node ?? "").Trim(), (Host ?? "").Trim().ToLowerInvariant(), Port);

    public ManualNodeAddress ToManualAddress() =>
        new((Node ?? "").Trim(), (Host ?? "").Trim().ToLowerInvariant(), Port,
            ManualNodeAddress.IsIpv6Literal((Host ?? "").Trim()));
}
