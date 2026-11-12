namespace NodeX.Core;

/// <summary>Link mode prefix used in app_rpt <c>L</c> and <c>STATUS</c> text frames.</summary>
public enum AslRemoteLinkMode
{
    Transceive,
    Monitor,
    Connecting,
    Unknown
}

/// <summary>One node linked to the remote peer, parsed from an app_rpt <c>L</c> or <c>STATUS</c> text frame.</summary>
public sealed record AslRemoteLink(string NodeNumber, AslRemoteLinkMode Mode);

/// <summary>Kind of app_rpt <c>AST_FRAME_TEXT</c> message received on a node link.</summary>
public enum AslTextMessageKind
{
    Handshake,
    LinkList,
    Status,
    Other
}

/// <summary>Structured representation of an app_rpt <c>AST_FRAME_TEXT</c> payload.</summary>
public sealed record AslTextMessage(
    AslTextMessageKind Kind,
    string RawText,
    string? SourceNode,
    bool? IsKeyed,
    IReadOnlyList<AslRemoteLink> LinkedNodes);

/// <summary>
/// Parses AllStarLink <c>app_rpt</c> text frames (<c>!NEWKEY1!</c>, <c>L &lt;links&gt;</c>,
/// and <c>T &lt;node&gt; STATUS,...</c>).
/// </summary>
public static class AslTextParser
{
    public static AslTextMessage Parse(string? raw)
    {
        string clean = (raw ?? string.Empty).TrimEnd('\0').Trim();
        if (clean.Length == 0)
            return new AslTextMessage(AslTextMessageKind.Other, string.Empty, null, null, []);

        if (clean.StartsWith("!NEWKEY", StringComparison.OrdinalIgnoreCase))
            return new AslTextMessage(AslTextMessageKind.Handshake, clean, null, null, []);

        if (clean.Equals("L", StringComparison.Ordinal) || clean.StartsWith("L ", StringComparison.Ordinal))
        {
            string rest = clean.Length > 1 ? clean[1..].Trim() : string.Empty;
            var links = ParseLinkTokens(rest.Length == 0 ? [] : rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return new AslTextMessage(AslTextMessageKind.LinkList, clean, null, null, links);
        }

        if (clean.StartsWith("T ", StringComparison.Ordinal))
        {
            string afterT = clean[2..].TrimStart();
            int space = afterT.IndexOf(' ');
            if (space > 0)
            {
                string sourceNode = afterT[..space].Trim();
                string body = afterT[(space + 1)..].Trim();
                if (body.StartsWith("STATUS,", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = body.Split(',', StringSplitOptions.TrimEntries);
                    // Format: STATUS,<node>,<keyed 0|1>[,<link1>,<link2>...]
                    string statusNode = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : sourceNode;
                    bool? keyed = parts.Length > 2
                        ? parts[2] == "1" ? true : parts[2] == "0" ? false : null
                        : null;
                    var links = parts.Length > 3 ? ParseLinkTokens(parts[3..]) : [];
                    return new AslTextMessage(AslTextMessageKind.Status, clean, statusNode, keyed, links);
                }

                return new AslTextMessage(AslTextMessageKind.Other, clean, sourceNode, null, []);
            }
        }

        return new AslTextMessage(AslTextMessageKind.Other, clean, null, null, []);
    }

    /// <summary>
    /// Formats a short human-readable summary of a linked-node list (excluding <paramref name="excludeNode"/>),
    /// e.g. <c>"47923, 1997, 1996"</c> or <c>"47923 (RX), 1997 (+2 more)"</c>.
    /// </summary>
    public static string FormatLinkedNodes(
        IReadOnlyList<AslRemoteLink> links,
        string? excludeNode = null,
        int maxShown = 5)
    {
        if (links.Count == 0) return string.Empty;

        var filtered = new List<string>(links.Count);
        foreach (var l in links)
        {
            if (!string.IsNullOrEmpty(excludeNode) && string.Equals(l.NodeNumber, excludeNode, StringComparison.Ordinal))
                continue;

            string item = l.Mode switch
            {
                AslRemoteLinkMode.Monitor => $"{l.NodeNumber} (RX)",
                AslRemoteLinkMode.Connecting => $"{l.NodeNumber} (…)",
                _ => l.NodeNumber
            };
            filtered.Add(item);
        }

        if (filtered.Count == 0) return string.Empty;
        if (filtered.Count <= maxShown) return string.Join(", ", filtered);
        return $"{string.Join(", ", filtered.Take(maxShown))} (+{filtered.Count - maxShown} more)";
    }

    private static IReadOnlyList<AslRemoteLink> ParseLinkTokens(IEnumerable<string> tokens)
    {
        var result = new List<AslRemoteLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string rawToken in tokens)
        {
            if (string.IsNullOrWhiteSpace(rawToken)) continue;
            string token = rawToken.Trim();
            AslRemoteLinkMode mode = AslRemoteLinkMode.Unknown;
            string node = token;

            if (token.Length > 1 && char.IsLetter(token[0]))
            {
                mode = char.ToUpperInvariant(token[0]) switch
                {
                    'T' => AslRemoteLinkMode.Transceive,
                    'R' => AslRemoteLinkMode.Monitor,
                    'C' => AslRemoteLinkMode.Connecting,
                    _ => AslRemoteLinkMode.Unknown
                };
                node = token[1..].Trim();
            }

            if (node.Length == 0 || !node.All(char.IsAsciiDigit)) continue;
            if (seen.Add(node))
                result.Add(new AslRemoteLink(node, mode));
        }

        return result;
    }
}
