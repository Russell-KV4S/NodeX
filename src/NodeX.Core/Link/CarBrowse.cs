using NodeX.Core.Settings;

namespace NodeX.Core;

/// <summary>The picture shown beside a car browse row (the platform head maps it to a drawable).</summary>
public enum CarIcon
{
    None,
    /// <summary>A connected node the user transmits to.</summary>
    Transmit,
    /// <summary>A connected listen-only node.</summary>
    Listen,
    /// <summary>A favourite that is not connected.</summary>
    Favorite,
    Disconnect
}

/// <summary>One row on the car's browse screen. <see cref="MediaId"/> is the node number or command id for a playable row.</summary>
public sealed record CarBrowseItem(string MediaId, string Title, bool Playable, string? Subtitle = null, CarIcon Icon = CarIcon.None);

/// <summary>
/// Platform-neutral rules for the Android Auto listen-only media app: what the browse list shows, which favourite
/// Next/Previous picks and how a voice query maps to a favourite. A selected favourite is added alongside the links
/// already up; nothing is hung up to make room. Nothing here can transmit.
/// </summary>
public static class CarBrowse
{
    /// <summary>The browse root identifier.</summary>
    public const string RootId = "nodex_root";

    /// <summary>Media ID of the Active Links folder/tab.</summary>
    public const string ActiveFolderId = "nodex:active";

    /// <summary>Media ID of the Favourites folder/tab.</summary>
    public const string FavoritesFolderId = "nodex:favorites";

    /// <summary>Media id of the single non-playable row shown when there are no favourites.</summary>
    public const string NoFavoritesId = "nodex:no-favorites";

    /// <summary>Media id of the single non-playable row shown when there are no active links.</summary>
    public const string NoActiveId = "nodex:no-active";

    /// <summary>Prefix for disconnecting an individual node from the car screen.</summary>
    public const string DisconnectPrefix = "nodex:disconnect:";

    /// <summary>Prefix for a saved private node in the car favourites list (<c>nodex:private:1999@host:4569</c>).</summary>
    public const string PrivatePrefix = "nodex:private:";

    /// <summary>Media ID for disconnecting all nodes from the car screen.</summary>
    public const string DisconnectAllId = "nodex:disconnect:all";

    /// <summary>Returns the car media ID for a saved private node.</summary>
    public static string PrivateMediaId(PrivateNodeEntry entry) =>
        // FormatCanonical brackets IPv6 literals, so the id parses back (an unbracketed "2001:db8::5:4569" does not).
        PrivatePrefix + ManualNodeAddress.FormatCanonical(entry.Node, entry.Host, entry.Port);

    /// <summary>The saved private entry a private media id refers to, or null.</summary>
    public static PrivateNodeEntry? FindPrivateEntry(NodeSettings settings, string? mediaId)
    {
        if (!TryParsePrivateMediaId(mediaId, out var address) || address == null) return null;
        return (settings.PrivateNodes ?? []).FirstOrDefault(p => address.SameTargetAs(p.ToManualAddress()));
    }

    /// <summary>Every playable favourite id: public node numbers first, then private media ids.</summary>
    public static IReadOnlyList<string> FavoriteMediaIds(NodeSettings settings) =>
        (settings.FavoriteNodes ?? []).Where(f => !string.IsNullOrWhiteSpace(f))
            .Concat((settings.PrivateNodes ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p.Node) && !string.IsNullOrWhiteSpace(p.Host))
                .Select(PrivateMediaId))
            .ToList();

    /// <summary>
    /// What "play NodeX" with no name plays: the last played favourite if it is still saved (public number or private
    /// id; an older private id with different spelling is matched by address), else the first favourite, else null.
    /// </summary>
    public static string? LastFavorite(NodeSettings settings, string? lastPlayed)
    {
        var ids = FavoriteMediaIds(settings);
        if (!string.IsNullOrEmpty(lastPlayed))
        {
            if (ids.Contains(lastPlayed)) return lastPlayed;
            if (FindPrivateEntry(settings, lastPlayed) is { } entry) return PrivateMediaId(entry);
        }
        return ids.Count > 0 ? ids[0] : null;
    }

    /// <summary>
    /// Title for a linked node on the now-playing card or in "now talking": a private link's name (else
    /// <c>1999 @ host</c>), or the public favourite label. Uses the connection, not the number alone, so a private
    /// 1999 never shows a public favourite's label.
    /// </summary>
    public static string LinkTitle(NodeSettings settings, string node, IEnumerable<NodeConnection>? connections)
    {
        var conn = connections?.FirstOrDefault(c => c.Node == node);
        if (conn is { IsPrivate: true })
            return !string.IsNullOrWhiteSpace(conn.PrivateName) ? conn.PrivateName.Trim() : conn.DisplayNode;
        return TitleFor(settings, node);
    }

    /// <summary>Card heading for a linked node: <c>1999 @ host · Club Hub</c> for private links, else <see cref="CardTitle"/>.</summary>
    public static string LinkCardTitle(NodeSettings settings, string node, IEnumerable<NodeConnection>? connections)
    {
        var conn = connections?.FirstOrDefault(c => c.Node == node);
        if (conn is { IsPrivate: true })
            return !string.IsNullOrWhiteSpace(conn.PrivateName) ? $"{conn.DisplayNode} · {conn.PrivateName.Trim()}" : conn.DisplayNode;
        return CardTitle(settings, node);
    }

    /// <summary>True when <paramref name="mediaId"/> names a private favourite; parses its address if so.</summary>
    public static bool TryParsePrivateMediaId(string? mediaId, out ManualNodeAddress? address)
    {
        address = null;
        if (string.IsNullOrEmpty(mediaId) || !mediaId.StartsWith(PrivatePrefix, StringComparison.Ordinal))
            return false;
        return ManualNodeAddress.TryParse(mediaId[PrivatePrefix.Length..], out address, out _) && address?.IsPrivate == true;
    }

    /// <summary>Total count of saved favourites (public + private) for the root folder subtitle.</summary>
    public static int TotalFavoritesCount(NodeSettings settings) =>
        (settings.FavoriteNodes ?? []).Count(f => !string.IsNullOrWhiteSpace(f))
        + (settings.PrivateNodes ?? []).Count(p => !string.IsNullOrWhiteSpace(p.Node) && !string.IsNullOrWhiteSpace(p.Host));

    /// <summary>Top-level navigation tabs for Android Auto.</summary>
    public static IReadOnlyList<CarBrowseItem> BuildRootItems(int activeCount, int favoritesCount)
    {
        return
        [
            new CarBrowseItem(ActiveFolderId, "Active Links", false, activeCount > 0 ? $"{activeCount} connected" : "None connected"),
            new CarBrowseItem(FavoritesFolderId, "Favourites", false, favoritesCount > 0 ? $"{favoritesCount} saved" : "None saved")
        ];
    }

    /// <summary>The favourites as browse rows with connection badges, or one explanatory row when there are none.</summary>
    public static IReadOnlyList<CarBrowseItem> BuildFavoritesItems(
        NodeSettings settings,
        IReadOnlyList<string>? connectedNodes = null,
        string? transmitNode = null)
    {
        var items = new List<CarBrowseItem>();
        var connectedSet = connectedNodes != null ? new HashSet<string>(connectedNodes) : [];

        foreach (string node in settings.FavoriteNodes ?? [])
        {
            if (string.IsNullOrWhiteSpace(node)) continue;
            bool isConnected = connectedSet.Contains(node);
            string subtitle = isConnected
                ? (node == transmitNode ? $"Node {node} • Connected (Transmit)" : $"Node {node} • Connected (Listen)")
                : $"Node {node}";
            var icon = !isConnected ? CarIcon.Favorite : node == transmitNode ? CarIcon.Transmit : CarIcon.Listen;
            items.Add(new CarBrowseItem(node, TitleFor(settings, node), true, subtitle, icon));
        }

        foreach (var priv in settings.PrivateNodes ?? [])
        {
            if (string.IsNullOrWhiteSpace(priv.Node) || string.IsNullOrWhiteSpace(priv.Host)) continue;
            bool isConnected = connectedSet.Contains(priv.Node);
            string title = !string.IsNullOrWhiteSpace(priv.Name) ? $"{priv.Name.Trim()} (Private)" : $"{priv.DisplayAddress} (Private)";
            string subtitle = isConnected
                ? (priv.Node == transmitNode
                    ? $"Private · {priv.DisplayAddress} • Connected (Transmit)"
                    : $"Private · {priv.DisplayAddress} • Connected (Listen)")
                : $"Private · {priv.DisplayAddress}";
            var icon = !isConnected ? CarIcon.Favorite : priv.Node == transmitNode ? CarIcon.Transmit : CarIcon.Listen;
            items.Add(new CarBrowseItem(PrivateMediaId(priv), title, true, subtitle, icon));
        }

        if (items.Count == 0)
            items.Add(new CarBrowseItem(NoFavoritesId, "No favourites yet. Add them in NodeX on your phone.", false));
        return items;
    }

    /// <summary>The favourites as browse rows, or one explanatory row when there are none.</summary>
    public static IReadOnlyList<CarBrowseItem> BuildItems(NodeSettings settings) =>
        BuildFavoritesItems(settings);

    /// <summary>
    /// Rows for the Active Links folder: one row per connected/reconnecting node ("523801 · UL Cloud Node" with a
    /// status line such as "TRANSMIT HERE · Connected · Hearing now"), and a disconnect-all row when several links
    /// exist. Selecting a row only shows that node on the car's now-playing screen, where its Mute, Disconnect and
    /// "Transmit here" buttons are; it never dials, hangs up or changes the transmit node.
    /// </summary>
    public static IReadOnlyList<CarBrowseItem> BuildActiveItems(
        NodeSettings settings,
        IReadOnlyList<NodeConnection> connections,
        string? transmitNode,
        IReadOnlySet<string>? talkingNodes = null)
    {
        var items = new List<CarBrowseItem>();
        foreach (var conn in connections)
        {
            string node = conn.Node;
            bool isTx = node == transmitNode;
            bool isTalking = talkingNodes?.Contains(node) == true;
            bool isConnected = conn.Link?.Session.State == Iax2SessionState.Connected;

            string status = StatusLine(isTx, isConnected, conn.Reconnect.Active, isTalking, conn.Muted);
            string title = conn.IsPrivate
                ? (!string.IsNullOrWhiteSpace(conn.PrivateName)
                    ? $"{conn.DisplayNode} · {conn.PrivateName} (Private)"
                    : $"{conn.DisplayNode} (Private)")
                : CardTitle(settings, node);
            items.Add(new CarBrowseItem(node, title, true, status, isTx ? CarIcon.Transmit : CarIcon.Listen));
        }

        if (items.Count == 0)
        {
            items.Add(new CarBrowseItem(NoActiveId, "No active links", false, "Select a favourite to connect"));
        }
        else if (connections.Count > 1)
        {
            items.Add(new CarBrowseItem(DisconnectAllId, "Disconnect all nodes", true, $"Hang up all {connections.Count} active links", CarIcon.Disconnect));
        }

        return items;
    }

    /// <summary>
    /// One node's status as the phone's card shows it, on one line: "TRANSMIT HERE · Connected · Hearing now",
    /// "Listen only · Reconnecting…", "Listen only · Connected · Muted".
    /// </summary>
    public static string StatusLine(bool isTransmit, bool connected, bool reconnecting, bool hearing, bool muted)
    {
        var parts = new List<string> { isTransmit ? "TRANSMIT HERE" : "Listen only" };
        parts.Add(connected ? "Connected" : reconnecting ? "Reconnecting…" : "Connecting…");
        if (connected && hearing) parts.Add("Hearing now");
        if (muted) parts.Add("Muted");
        return string.Join(" · ", parts);
    }

    /// <summary>"523801 · UL Cloud Node" like the phone's card heading, or just the node number when it has no label.</summary>
    public static string CardTitle(NodeSettings settings, string node)
    {
        if (FindPrivateEntry(settings, node) is { } priv)
            return string.IsNullOrWhiteSpace(priv.Name) ? priv.DisplayAddress : $"{priv.DisplayAddress} · {priv.Name.Trim()}";
        string label = TitleFor(settings, node);
        return label == node ? node : $"{node} · {label}";
    }

    /// <summary>The favourite's label, or the node number when it has none.</summary>
    public static string TitleFor(NodeSettings settings, string node)
    {
        if (FindPrivateEntry(settings, node) is { } priv)
            return string.IsNullOrWhiteSpace(priv.Name) ? priv.DisplayAddress : priv.Name.Trim();
        if (settings.FavoriteLabels != null
            && settings.FavoriteLabels.TryGetValue(node, out string? label)
            && !string.IsNullOrWhiteSpace(label))
            return label.Trim();
        return node;
    }

    /// <summary>
    /// The favourite <paramref name="step"/> places after <paramref name="current"/>, wrapping round. When
    /// <paramref name="current"/> is not a favourite, forward gives the first and backward the last. Null if there are none.
    /// </summary>
    public static string? Neighbor(IReadOnlyList<string> favorites, string? current, int step)
    {
        var list = favorites.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (list.Count == 0) return null;

        int at = current == null ? -1 : list.IndexOf(current);
        if (at < 0) return step >= 0 ? list[0] : list[^1];

        int next = ((at + step) % list.Count + list.Count) % list.Count;
        return list[next];
    }

    /// <summary>
    /// Resolves a spoken or typed query to a favourite: an exact node number, else a label that matches exactly, else
    /// one that contains the query (case-insensitive). Private nodes take part by name (or <c>1999 @ host</c>) and
    /// return their private media id; a spoken number matches a public favourite first, then a single private node
    /// with that number. Null when nothing or more than one favourite matches.
    /// </summary>
    public static string? FindFavorite(NodeSettings settings, string? query)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query)) return null;

        var favorites = FavoriteMediaIds(settings).ToList();

        string digits = new(query.Where(char.IsDigit).ToArray());
        if (digits.Length > 0)
        {
            if (favorites.Contains(digits)) return digits;
            var byNumber = (settings.PrivateNodes ?? []).Where(p => p.Node == digits).ToList();
            if (byNumber.Count == 1) return PrivateMediaId(byNumber[0]);
            // Several private nodes share that number: ambiguous. Without this, a "1999 @ host" title (the private
            // fallback label) would partially match below and pick one of them at random order.
            if (byNumber.Count > 1) return null;
        }

        var exact = favorites.Where(f => string.Equals(TitleFor(settings, f), query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];

        var partial = favorites.Where(f => TitleFor(settings, f).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }
}
