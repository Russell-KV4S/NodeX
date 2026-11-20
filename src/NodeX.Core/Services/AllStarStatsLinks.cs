using System;

namespace NodeX.Core;

/// <summary>
/// Web links to stats.allstarlink.org pages about a public node.
/// </summary>
public static class AllStarStatsLinks
{
    public const string BaseUrl = "https://stats.allstarlink.org/stats/";

    /// <summary>
    /// The node's "bubble chart" (network map image of what it is linked to), the same link the public node list
    /// shows as "Bubble": <c>https://stats.allstarlink.org/stats/{node}/networkMap</c>. Checked live on 2026-11-20
    /// (returns an image). Null for anything that is not a plain public node number (private / node@host entries).
    /// </summary>
    public static Uri? BubbleChart(string? node)
    {
        if (!IsPublicNodeNumber(node)) return null;
        return new Uri(BaseUrl + node!.Trim() + "/networkMap");
    }

    private static bool IsPublicNodeNumber(string? node)
    {
        if (string.IsNullOrWhiteSpace(node)) return false;
        string n = node.Trim();
        if (n.Length > 10) return false;
        foreach (char c in n)
            if (c is < '0' or > '9') return false;
        return true;
    }
}
