using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NodeX.Core.Settings;

namespace NodeX.Core;

/// <summary>
/// A parsed dial target: either a public AllStarLink node number (<see cref="IsPrivate"/> is false) or a
/// manually addressed/private node with an explicit host and port (<see cref="IsPrivate"/> is true).
/// </summary>
public sealed record ManualNodeAddress(
    string Node,
    string? Host = null,
    int Port = Iax2Protocol.DefaultPort,
    bool IsIpv6 = false)
{
    public bool IsPrivate => !string.IsNullOrEmpty(Host);

    /// <summary>Log/link-row form: <c>1999 @ host</c> (adds <c>:port</c> when non-default), or <c>27339</c> for public.</summary>
    public string DisplayAddress => IsPrivate ? FormatDisplay(Node, Host!, Port) : Node;

    /// <summary>Connect-box form: <c>1999@host</c> (adds <c>:port</c> when non-default), or <c>27339</c> for public.</summary>
    public string CanonicalInput => IsPrivate ? FormatCanonical(Node, Host!, Port) : Node;

    /// <summary>Host plus port when non-default (brackets IPv6 literals).</summary>
    public string HostDisplay => !IsPrivate ? "" : FormatHostPort(Host!, Port);

    public PrivateNodeEntry ToPrivateEntry(string? name = null) =>
        IsPrivate
            ? new PrivateNodeEntry(Node, Host!, Port, string.IsNullOrWhiteSpace(name) ? null : name.Trim())
            : throw new InvalidOperationException("Cannot create a private node entry from a public node number.");

    /// <summary>Returns true when two private addresses refer to the same (Node, Host, Port) triple.</summary>
    public bool SameTargetAs(ManualNodeAddress? other)
    {
        if (other == null) return false;
        if (!IsPrivate && !other.IsPrivate) return Node == other.Node;
        if (IsPrivate != other.IsPrivate) return false;
        return Node == other.Node
               && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
               && Port == other.Port;
    }

    public static string FormatDisplay(string node, string host, int port)
    {
        string n = (node ?? "").Trim();
        string hp = FormatHostPort(host, port);
        return hp.Length > 0 ? $"{n} @ {hp}" : n;
    }

    public static string FormatCanonical(string node, string host, int port)
    {
        string n = (node ?? "").Trim();
        string hp = FormatHostPort(host, port);
        return hp.Length > 0 ? $"{n}@{hp}" : n;
    }

    public static string FormatHostPort(string host, int port)
    {
        string h = (host ?? "").Trim().ToLowerInvariant();
        if (h.Length == 0) return "";
        string formattedHost = h.Contains(':') && !h.StartsWith('[') ? $"[{h}]" : h;
        return port > 0 && port != Iax2Protocol.DefaultPort ? $"{formattedHost}:{port}" : formattedHost;
    }

    public static bool IsIpv6Literal(string host) =>
        !string.IsNullOrWhiteSpace(host)
        && IPAddress.TryParse(host.Trim().Trim('[', ']'), out var ip)
        && ip.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>
    /// Validates a bare host string (DNS name, IPv4 literal, or IPv6 literal with or without brackets).
    /// </summary>
    public static bool IsValidHost(string? host, out string normalized, out bool isIpv6)
    {
        normalized = "";
        isIpv6 = false;
        if (string.IsNullOrWhiteSpace(host)) return false;

        string h = host.Trim();
        if (h.StartsWith('[') && h.EndsWith(']'))
        {
            string inner = h[1..^1].Trim();
            if (IPAddress.TryParse(inner, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                normalized = v6.ToString().ToLowerInvariant();
                isIpv6 = true;
                return true;
            }
            return false;
        }

        if (h.Contains(':'))
        {
            if (IPAddress.TryParse(h, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                normalized = v6.ToString().ToLowerInvariant();
                isIpv6 = true;
                return true;
            }
            return false;
        }

        h = h.ToLowerInvariant();
        if (h.Length > 253) return false;

        // IPv4 literal (strict dotted quad: four decimal octets, not "12345" which IPAddress.TryParse accepts).
        if (h.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            string[] parts = h.Split('.');
            if (parts.Length != 4) return false;
            foreach (string part in parts)
            {
                if (part.Length is 0 or > 3 || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    return false;
            }
            if (IPAddress.TryParse(h, out var v4) && v4.AddressFamily == AddressFamily.InterNetwork)
            {
                normalized = v4.ToString();
                return true;
            }
            return false;
        }

        // DNS hostname (single-label LAN names like "shackpi" or multi-label names like "club.ddns.net").
        if (h.StartsWith('.') || h.EndsWith('.')) return false;
        string[] labels = h.Split('.');
        bool hasLetter = false;
        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            foreach (char c in label)
            {
                if (char.IsAsciiLetterLower(c)) hasLetter = true;
                else if (!char.IsAsciiDigit(c) && c != '-') return false;
            }
        }
        if (!hasLetter) return false;

        normalized = h;
        return true;
    }

    private const string ExpectedForms =
        "Expected a node number (e.g. 1999), 1999@host[:port], 1999@[ipv6]:port, or a rpt.conf line (radio@host:port/1999).";

    /// <summary>
    /// Parses Connect-box or private-node address input per §4.9:
    /// <list type="bullet">
    /// <item><c>1999</c> (public node)</item>
    /// <item><c>1999@host</c>, <c>1999@host:4570</c>, <c>1999@[2001:db8::5]:4569</c></item>
    /// <item><c>radio@host:port/1999</c>, <c>radio@host:port/1999,NONE</c>, <c>radio@host:port/1999,&lt;ip&gt;</c>, with optional <c>1999 =</c> stanza prefix</item>
    /// </list>
    /// Spaces around <c>@</c>, <c>:</c>, <c>/</c> and <c>=</c> are tolerated.
    /// </summary>
    public static bool TryParse(string? input, out ManualNodeAddress? address, out string? error)
    {
        address = null;
        error = null;

        string raw = (input ?? "").Trim();
        if (raw.Length == 0)
        {
            error = "Enter a node number or private address. " + ExpectedForms;
            return false;
        }

        // 1. Plain public node number (3-7 digits)
        if (NodeSettingsValidator.IsValidNodeNumber(raw))
        {
            address = new ManualNodeAddress(raw);
            return true;
        }

        // Check for optional rpt.conf stanza prefix "<node> = ..."
        string work = raw;
        string? prefixNode = null;
        int eqIdx = work.IndexOf('=');
        if (eqIdx >= 0)
        {
            string left = work[..eqIdx].Trim();
            string right = work[(eqIdx + 1)..].Trim();
            if (!NodeSettingsValidator.IsValidNodeNumber(left))
            {
                error = $"Invalid node number '{left}' before '='. Node numbers must be 3 to 7 digits. {ExpectedForms}";
                return false;
            }
            prefixNode = left;
            work = right;
        }

        // 2. rpt.conf form: radio@host[:port]/1999[,NONE|,<ip>]
        int slashIdx = work.IndexOf('/');
        if (slashIdx >= 0)
        {
            int atIdx = work.IndexOf('@');
            if (atIdx <= 0 || atIdx > slashIdx)
            {
                error = "Invalid rpt.conf address format. " + ExpectedForms;
                return false;
            }

            string user = work[..atIdx].Trim();
            if (!string.Equals(user, "radio", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Unsupported IAX user '{user}'. NodeX always logs in as IAX user 'radio' with no password.";
                return false;
            }

            string hostPortPart = work[(atIdx + 1)..slashIdx].Trim();
            string nodeAndTrail = work[(slashIdx + 1)..].Trim();
            int commaIdx = nodeAndTrail.IndexOf(',');
            string nodePart = (commaIdx >= 0 ? nodeAndTrail[..commaIdx] : nodeAndTrail).Trim();

            if (!NodeSettingsValidator.IsValidNodeNumber(nodePart))
            {
                error = $"Node number '{nodePart}' must be 3 to 7 digits. {ExpectedForms}";
                return false;
            }

            if (prefixNode != null && prefixNode != nodePart)
            {
                error = $"Stanza node number '{prefixNode}' does not match '/{nodePart}'.";
                return false;
            }

            if (!TryParseHostPort(hostPortPart, out string host, out int port, out bool isIpv6, out error))
                return false;

            address = new ManualNodeAddress(nodePart, host, port, isIpv6);
            return true;
        }

        if (prefixNode != null)
        {
            error = "A rpt.conf line with '=' must be in the form '1999 = radio@host:port/1999'. " + ExpectedForms;
            return false;
        }

        // 3. Direct form: 1999@host[:port] or 1999@[ipv6][:port]
        int directAt = work.IndexOf('@');
        if (directAt >= 0)
        {
            if (work.IndexOf('@', directAt + 1) >= 0)
            {
                error = "Only one '@' is allowed in a node address. " + ExpectedForms;
                return false;
            }

            string nodePart = work[..directAt].Trim();
            string hostPortPart = work[(directAt + 1)..].Trim();
            if (!NodeSettingsValidator.IsValidNodeNumber(nodePart))
            {
                error = $"Node number '{nodePart}' must be 3 to 7 digits. {ExpectedForms}";
                return false;
            }

            if (!TryParseHostPort(hostPortPart, out string host, out int port, out bool isIpv6, out error))
                return false;

            address = new ManualNodeAddress(nodePart, host, port, isIpv6);
            return true;
        }

        error = "Invalid node address. " + ExpectedForms;
        return false;
    }

    private static bool TryParseHostPort(
        string raw, out string host, out int port, out bool isIpv6, out string? error)
    {
        host = "";
        port = Iax2Protocol.DefaultPort;
        isIpv6 = false;
        error = null;

        string s = (raw ?? "").Trim();
        if (s.Length == 0)
        {
            error = "Enter a host name or IP address after '@'. " + ExpectedForms;
            return false;
        }

        // Bracketed IPv6 literal: [2001:db8::5] or [2001:db8::5]:4569 (tolerating spaces around ':')
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close < 0)
            {
                error = "Missing closing ']' around IPv6 address. " + ExpectedForms;
                return false;
            }

            string inner = s[1..close].Trim();
            if (!IPAddress.TryParse(inner, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = $"'{inner}' is not a valid IPv6 address. {ExpectedForms}";
                return false;
            }

            string tail = s[(close + 1)..].Trim();
            if (tail.Length > 0)
            {
                if (!tail.StartsWith(':'))
                {
                    error = "Expected ':port' after the IPv6 address. " + ExpectedForms;
                    return false;
                }
                if (!TryParsePort(tail[1..].Trim(), out port, out error))
                    return false;
            }

            host = v6.ToString().ToLowerInvariant();
            isIpv6 = true;
            return true;
        }

        // Non-bracketed host or host:port
        int colon = s.IndexOf(':');
        string hostPart = s;
        if (colon >= 0)
        {
            if (s.IndexOf(':', colon + 1) >= 0)
            {
                error = "IPv6 addresses must be enclosed in square brackets, e.g. 1999@[2001:db8::5]:4569.";
                return false;
            }
            hostPart = s[..colon].Trim();
            string portPart = s[(colon + 1)..].Trim();
            if (!TryParsePort(portPart, out port, out error))
                return false;
        }

        if (!IsValidHost(hostPart, out host, out isIpv6))
        {
            error = $"'{hostPart}' is not a valid host name or IP address. {ExpectedForms}";
            return false;
        }

        return true;
    }

    private static bool TryParsePort(string portPart, out int port, out string? error)
    {
        error = null;
        if (int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535)
        {
            return true;
        }

        port = Iax2Protocol.DefaultPort;
        error = $"Port '{portPart}' must be a number between 1 and 65535. {ExpectedForms}";
        return false;
    }

    // ----- Sharing (optional step 2): nodex://add?node=1999&host=<host>&port=4569&name=<text> -----

    /// <summary>Builds a shareable <c>nodex://add?...</c> link containing node, host, port and optional name (never any secret).</summary>
    public static string BuildShareUri(PrivateNodeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string node = Uri.EscapeDataString((entry.Node ?? "").Trim());
        string host = Uri.EscapeDataString((entry.Host ?? "").Trim().ToLowerInvariant());
        int port = entry.Port is >= 1 and <= 65535 ? entry.Port : Iax2Protocol.DefaultPort;
        string url = $"nodex://add?node={node}&host={host}&port={port}";
        if (!string.IsNullOrWhiteSpace(entry.Name))
            url += $"&name={Uri.EscapeDataString(entry.Name.Trim())}";
        return url;
    }

    /// <summary>
    /// Parses a <c>nodex://add?node=...&amp;host=...&amp;port=...&amp;name=...</c> share link.
    /// Pure parsing only: never connects and never saves on its own.
    /// </summary>
    public static bool TryParseShareUri(string? uriText, out PrivateNodeEntry? entry, out string? error)
    {
        entry = null;
        error = null;

        if (string.IsNullOrWhiteSpace(uriText)
            || !Uri.TryCreate(uriText.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "nodex", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "add", StringComparison.OrdinalIgnoreCase))
        {
            error = "Not a valid nodex://add private-node link.";
            return false;
        }

        var query = ParseQuery(uri.Query);
        string node = query.TryGetValue("node", out var n) ? n.Trim() : "";
        string hostRaw = query.TryGetValue("host", out var h) ? h.Trim() : "";
        string portRaw = query.TryGetValue("port", out var p) ? p.Trim() : "";
        string? name = query.TryGetValue("name", out var nm) && !string.IsNullOrWhiteSpace(nm) ? nm.Trim() : null;

        if (!NodeSettingsValidator.IsValidNodeNumber(node))
        {
            error = "The shared link does not contain a valid 3 to 7 digit node number.";
            return false;
        }

        if (!IsValidHost(hostRaw, out string host, out _))
        {
            error = "The shared link does not contain a valid host name or IP address.";
            return false;
        }

        int port = Iax2Protocol.DefaultPort;
        if (portRaw.Length > 0 && (!int.TryParse(portRaw, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            error = "The shared link has an invalid port (must be 1 to 65535).";
            return false;
        }

        if (name != null && name.Length > NodeSettingsValidator.MaxFavoriteLabelLength)
            name = name[..NodeSettingsValidator.MaxFavoriteLabelLength].TrimEnd();

        entry = new PrivateNodeEntry(node, host, port, name);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        string trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            string key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            string val = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = val;
        }
        return result;
    }

    /// <summary>
    /// Formats the <c>[nodes]</c> line a private server admin can paste into <c>rpt.conf</c> when that server
    /// does not pull the public AllStarLink node list.
    /// </summary>
    public static string FormatRptConfLine(string? localNode, IPAddress? perceivedAddress, int perceivedPort)
    {
        string node = !string.IsNullOrWhiteSpace(localNode) && NodeSettingsValidator.IsValidNodeNumber(localNode.Trim())
            ? localNode.Trim()
            : "<your-node>";
        string ip = perceivedAddress?.ToString() ?? "<your-public-ip>";
        int port = perceivedPort is >= 1 and <= 65535 ? perceivedPort : Iax2Protocol.DefaultPort;
        return $"{node} = radio@{ip}:{port}/{node},NONE";
    }
}
