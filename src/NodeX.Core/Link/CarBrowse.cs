using NodeX.Core.Settings;

namespace NodeX.Core;

/// <summary>One row on the car's browse screen. <see cref="MediaId"/> is the node number or command id for a playable row.</summary>
public sealed record CarBrowseItem(string MediaId, string Title, bool Playable, string? Subtitle = null);

/// <summary>
/// Platform-neutral rules for the Android Auto listen-only media app: what the browse list shows, which favourite
/// Next/Previous picks, how a voice query maps to a favourite, and which link a new selection replaces. Nothing here
/// can transmit; the car only ever dials, switches and hangs up links.
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
            items.Add(new CarBrowseItem(node, TitleFor(settings, node), true, subtitle));
        }

        if (items.Count == 0)
            items.Add(new CarBrowseItem(NoFavoritesId, "No favourites yet. Add them in NodeX on your phone.", false));
        return items;
    }

    /// <summary>The favourites as browse rows, or one explanatory row when there are none.</summary>
    public static IReadOnlyList<CarBrowseItem> BuildItems(NodeSettings settings) =>
        BuildFavoritesItems(settings);

    /// <summary>
    /// Rows for the Active Links folder: shows each connected/reconnecting node with status, a disconnect row
    /// for each, and a disconnect-all row when multiple links exist.
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
            string title = TitleFor(settings, node);
            bool isTx = node == transmitNode;
            bool isTalking = talkingNodes?.Contains(node) == true;
            bool isConnected = conn.Link?.Session.State == Iax2SessionState.Connected;

            string role = isTx ? "Transmit" : "Listen-only";
            string status;
            if (!isConnected)
                status = $"{role} • " + (conn.Reconnect.Active ? "Reconnecting…" : "Connecting…");
            else if (isTalking)
                status = $"{role} • Hearing now";
            else
                status = $"{role} • Connected";

            // Row 1: The node itself (playable: selects it as now playing and makes it transmit node)
            items.Add(new CarBrowseItem(node, title, true, status));

            // Row 2: Disconnect action for this specific node
            items.Add(new CarBrowseItem(DisconnectPrefix + node, $"Disconnect {node}", true, $"Hang up {title}"));
        }

        if (items.Count == 0)
        {
            items.Add(new CarBrowseItem(NoActiveId, "No active links", false, "Select a favourite to connect"));
        }
        else if (connections.Count > 1)
        {
            items.Add(new CarBrowseItem(DisconnectAllId, "Disconnect all nodes", true, $"Hang up all {connections.Count} active links"));
        }

        return items;
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

    /// <summary>
    /// The node to hang up when the car selects <paramref name="selected"/>: the one that was now-playing, unless it is
    /// the same node or the selected node is already connected (in which case we are just switching focus).
    /// </summary>
    public static string? NodeToReplace(string? nowPlaying, string selected, bool selectedAlreadyConnected = false) =>
        selectedAlreadyConnected || string.IsNullOrEmpty(nowPlaying) || nowPlaying == selected ? null : nowPlaying;
}
