using System.Text.RegularExpressions;

namespace NodeX.Core.Settings;

/// <summary>A problem with one settings field.</summary>
public readonly record struct SettingsIssue(string Field, string Message);

/// <summary>Validation and normalisation for <see cref="NodeSettings"/> and the node password.</summary>
public static partial class NodeSettingsValidator
{
    public static class Fields
    {
        public const string NodeNumber = nameof(NodeSettings.NodeNumber);
        public const string Password = "Password";
        public const string DefaultTargetNode = nameof(NodeSettings.DefaultTargetNode);
        public const string AutoConnectNodes = nameof(NodeSettings.AutoConnectNodes);
        public const string FavoriteNodes = nameof(NodeSettings.FavoriteNodes);
        public const string PrivateNodes = nameof(NodeSettings.PrivateNodes);
        public const string RegistrationUrl = nameof(NodeSettings.RegistrationUrl);
        public const string IaxPort = nameof(NodeSettings.IaxPort);
        public const string MicGain = nameof(NodeSettings.MicGain);
        public const string RxGain = nameof(NodeSettings.RxGain);
        public const string TimeOutTimerSeconds = nameof(NodeSettings.TimeOutTimerSeconds);
        public const string IdReminderMinutes = nameof(NodeSettings.IdReminderMinutes);
    }

    /// <summary>ASL3's registration module stores the secret in an 80-byte buffer.</summary>
    public const int MaxPasswordLength = 79;

    [GeneratedRegex(@"^\d{3,7}$")]
    private static partial Regex NodeNumberPattern();

    public static bool IsValidNodeNumber(string? value) => value != null && NodeNumberPattern().IsMatch(value);

    /// <summary>Trims text fields and cleans the favourites list.</summary>
    public static NodeSettings Normalize(NodeSettings s) => s with
    {
        NodeNumber = (s.NodeNumber ?? "").Trim(),
        DefaultTargetNode = (s.DefaultTargetNode ?? "").Trim(),
        AutoConnectNodes = CleanAutoConnectNodes(s),
        RegistrationUrl = (s.RegistrationUrl ?? "").Trim(),
        FavoriteNodes = (s.FavoriteNodes ?? [])
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList(),
        FavoriteLabels = CleanLabels(s),
        PrivateNodes = CleanPrivateNodes(s)
    };

    public const int MaxFavoriteLabelLength = 40;

    /// <summary>Trims, lower-cases hosts, caps names and removes duplicate (Node, Host, Port) entries.</summary>
    private static List<PrivateNodeEntry> CleanPrivateNodes(NodeSettings s)
    {
        var result = new List<PrivateNodeEntry>();
        var seen = new HashSet<(string Node, string Host, int Port)>();
        foreach (var raw in s.PrivateNodes ?? [])
        {
            if (raw == null) continue;
            string node = (raw.Node ?? "").Trim();
            string host = (raw.Host ?? "").Trim().Trim('[', ']').ToLowerInvariant();
            int port = raw.Port;
            string? name = (raw.Name ?? "").Trim();
            if (name.Length > MaxFavoriteLabelLength) name = name[..MaxFavoriteLabelLength].TrimEnd();
            if (name.Length == 0) name = null;

            if (node.Length == 0 && host.Length == 0) continue;
            var key = (node, host, port);
            if (seen.Add(key))
                result.Add(new PrivateNodeEntry(node, host, port, name));
        }
        return result;
    }

    /// <summary>Trims, canonicalizes valid addresses, and deduplicates startup auto-connect entries.</summary>
    private static List<string> CleanAutoConnectNodes(NodeSettings s)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in s.AutoConnectNodes ?? [])
        {
            string trimmed = (raw ?? "").Trim();
            if (trimmed.Length == 0) continue;
            string item = ManualNodeAddress.TryParse(trimmed, out var parsed, out _) && parsed != null
                ? parsed.CanonicalInput
                : trimmed;
            if (seen.Add(item)) result.Add(item);
        }
        return result;
    }

    /// <summary>Keeps only non-empty labels for nodes that are still favourites, trimmed and length-capped.</summary>
    private static Dictionary<string, string> CleanLabels(NodeSettings s)
    {
        var favorites = (s.FavoriteNodes ?? []).Select(n => (n ?? "").Trim()).ToHashSet();
        var result = new Dictionary<string, string>();
        foreach (var (node, label) in s.FavoriteLabels ?? [])
        {
            string key = (node ?? "").Trim();
            string text = (label ?? "").Trim();
            if (text.Length > MaxFavoriteLabelLength) text = text[..MaxFavoriteLabelLength].TrimEnd();
            if (text.Length > 0 && favorites.Contains(key)) result[key] = text;
        }
        return result;
    }

    /// <summary>Returns every problem found; an empty list means the configuration is usable.</summary>
    public static IReadOnlyList<SettingsIssue> Validate(NodeSettings settings, string? password)
    {
        var s = Normalize(settings);
        var issues = new List<SettingsIssue>();

        if (!AppThemes.IsValid(s.Theme))
            issues.Add(new(nameof(NodeSettings.Theme), "Choose a valid theme."));
        if (!double.IsFinite(s.TextScale) || s.TextScale < 1 || s.TextScale > 1.5)
            issues.Add(new(nameof(NodeSettings.TextScale), "Text size must be between 100% and 150%."));

        if (s.NodeNumber.Length == 0)
            issues.Add(new(Fields.NodeNumber, "Enter your AllStarLink node number."));
        else if (!NodeNumberPattern().IsMatch(s.NodeNumber))
            issues.Add(new(Fields.NodeNumber, "Node number must be 3 to 7 digits."));

        if (string.IsNullOrEmpty(password))
            issues.Add(new(Fields.Password, "Enter your node password (set on the AllStarLink portal)."));
        else if (password.Length > MaxPasswordLength)
            issues.Add(new(Fields.Password, $"Password is too long (maximum {MaxPasswordLength} characters)."));

        if (s.DefaultTargetNode.Length > 0 && !NodeNumberPattern().IsMatch(s.DefaultTargetNode))
            issues.Add(new(Fields.DefaultTargetNode, "Default target must be a 3 to 7 digit node number."));

        if (s.AutoConnectNodes.Count > NodeRuntime.MaxLinks)
            issues.Add(new(Fields.AutoConnectNodes, $"Choose at most {NodeRuntime.MaxLinks} auto-connect nodes."));

        foreach (string target in s.AutoConnectNodes)
        {
            if (!ManualNodeAddress.TryParse(target, out var parsed, out var error) || parsed == null)
                issues.Add(new(Fields.AutoConnectNodes, $"Auto-connect entry '{target}' is invalid: {error}"));
            else if (parsed.IsIpv6)
                issues.Add(new(Fields.AutoConnectNodes, $"Auto-connect entry '{target}' uses IPv6, which is not supported yet."));
        }

        if (s.AudioHistoryMaxClips is < Audio.AudioHistoryStore.MinMaxClips or > Audio.AudioHistoryStore.MaxAllowedClips)
            issues.Add(new(nameof(NodeSettings.AudioHistoryMaxClips),
                $"Audio history limit must be between {Audio.AudioHistoryStore.MinMaxClips} and {Audio.AudioHistoryStore.MaxAllowedClips} clips."));

        foreach (string fav in s.FavoriteNodes.Where(f => !NodeNumberPattern().IsMatch(f)))
            issues.Add(new(Fields.FavoriteNodes, $"Favourite '{fav}' is not a 3 to 7 digit node number."));

        foreach (var priv in s.PrivateNodes)
        {
            if (!NodeNumberPattern().IsMatch(priv.Node))
                issues.Add(new(Fields.PrivateNodes, $"Private node '{priv.Node}' must be a 3 to 7 digit node number."));
            if (!ManualNodeAddress.IsValidHost(priv.Host, out _, out _))
                issues.Add(new(Fields.PrivateNodes, $"Private node '{priv.Node}' has an invalid host '{priv.Host}'."));
            if (priv.Port is < 1 or > 65535)
                issues.Add(new(Fields.PrivateNodes, $"Private node '{priv.Node}' port must be between 1 and 65535."));
        }

        if (!Uri.TryCreate(s.RegistrationUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            issues.Add(new(Fields.RegistrationUrl, "Registration URL must be an https:// address."));

        if (s.IaxPort is < 1 or > 65535)
            issues.Add(new(Fields.IaxPort, "IAX port must be between 1 and 65535."));

        if (float.IsNaN(s.MicGain) || s.MicGain is < 0.1f or > 8f)
            issues.Add(new(Fields.MicGain, "Microphone gain must be between 0.1 and 8."));

        if (!float.IsFinite(s.RxGain) || s.RxGain is < 0.25f or > 8f)
            issues.Add(new(Fields.RxGain, "Receive gain must be between 0.25 and 8."));

        if (s.TimeOutTimerSeconds is < 10 or > 600)
            issues.Add(new(Fields.TimeOutTimerSeconds, "Time-out timer must be between 10 and 600 seconds."));

        if (s.IdReminderMinutes is < 1 or > 60)
            issues.Add(new(Fields.IdReminderMinutes, "ID reminder interval must be between 1 and 60 minutes."));

        return issues;
    }
}
