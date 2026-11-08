using NodeX.Core.Settings;

namespace NodeX.Core;

/// <summary>One row on the car's browse screen. <see cref="MediaId"/> is the node number for a playable row.</summary>
public sealed record CarBrowseItem(string MediaId, string Title, bool Playable);

/// <summary>
/// Platform-neutral rules for the Android Auto listen-only media app: what the browse list shows, which favourite
/// Next/Previous picks, how a voice query maps to a favourite, and which link a new selection replaces. Nothing here
/// can transmit; the car only ever dials, switches and hangs up links.
/// </summary>
public static class CarBrowse
{
    /// <summary>Media id of the single non-playable row shown when there are no favourites.</summary>
    public const string NoFavoritesId = "nodex:no-favorites";

    /// <summary>The favourites as browse rows, or one explanatory row when there are none.</summary>
    public static IReadOnlyList<CarBrowseItem> BuildItems(NodeSettings settings)
    {
        var items = new List<CarBrowseItem>();
        foreach (string node in settings.FavoriteNodes ?? [])
        {
            if (string.IsNullOrWhiteSpace(node)) continue;
            items.Add(new CarBrowseItem(node, TitleFor(settings, node), true));
        }

        if (items.Count == 0)
            items.Add(new CarBrowseItem(NoFavoritesId, "No favourites yet. Add them in NodeX on your phone.", false));
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
    /// the same node. Links started on the phone are never returned because only the car's own now-playing node is passed in.
    /// </summary>
    public static string? NodeToReplace(string? nowPlaying, string selected) =>
        string.IsNullOrEmpty(nowPlaying) || nowPlaying == selected ? null : nowPlaying;
}
