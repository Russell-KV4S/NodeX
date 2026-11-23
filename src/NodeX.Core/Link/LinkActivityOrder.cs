namespace NodeX.Core;

/// <summary>
/// Orders linked node cards either dynamically by most recent activity (unmuted RX or local TX) or in fixed
/// connect order. Extracted to Core so ordering and tie-breaking are deterministic and directly unit-tested.
/// </summary>
public static class LinkActivityOrder
{
    /// <summary>One linked node's connect index and monotonic activity sequence (0 = no activity yet).</summary>
    public readonly record struct Entry(string Node, int ConnectIndex, long LastActivitySeq);

    /// <summary>
    /// Returns node numbers in display order. When <paramref name="sortByActivity"/> is true, nodes with higher
    /// positive <see cref="Entry.LastActivitySeq"/> appear first, and nodes with no activity yet stay at the bottom
    /// in connect order. When false, links stay in connect order.
    /// </summary>
    public static IReadOnlyList<string> Order(IReadOnlyList<Entry> entries, bool sortByActivity)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count <= 1) return entries.Count == 0 ? [] : [entries[0].Node];

        if (!sortByActivity)
        {
            return entries
                .OrderBy(e => e.ConnectIndex)
                .Select(e => e.Node)
                .ToArray();
        }

        return entries
            .OrderByDescending(e => Math.Max(0L, e.LastActivitySeq))
            .ThenBy(e => e.ConnectIndex)
            .Select(e => e.Node)
            .ToArray();
    }
}
