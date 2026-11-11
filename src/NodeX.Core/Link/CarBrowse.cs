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

    /// <summary>Media ID for disconnecting all nodes from the car screen.</summary>
    public const string DisconnectAllId = "nodex:disconnect:all";

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
            items.Add(new CarBrowseItem(node, CardTitle(settings, node), true, status, isTx ? CarIcon.Transmit : CarIcon.Listen));
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
        string label = TitleFor(settings, node);
        return label == node ? node : $"{node} · {label}";
    }

    /// <summary>The favourite's label, or the node number when it has none.</summary>
    public static string TitleFor(NodeSettings settings, string node)
    {
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
    /// one that contains the query (case-insensitive). Null when nothing or more than one favourite matches.
    /// </summary>
    public static string? FindFavorite(NodeSettings settings, string? query)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query)) return null;

        var favorites = (settings.FavoriteNodes ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

        string digits = new(query.Where(char.IsDigit).ToArray());
        if (digits.Length > 0 && favorites.Contains(digits)) return digits;

        var exact = favorites.Where(f => string.Equals(TitleFor(settings, f), query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];

        var partial = favorites.Where(f => TitleFor(settings, f).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }
}
