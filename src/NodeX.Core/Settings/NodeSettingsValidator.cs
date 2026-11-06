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
        public const string Callsign = nameof(NodeSettings.Callsign);
        public const string DefaultTargetNode = nameof(NodeSettings.DefaultTargetNode);
        public const string FavoriteNodes = nameof(NodeSettings.FavoriteNodes);
        public const string RegistrationUrl = nameof(NodeSettings.RegistrationUrl);
        public const string IaxPort = nameof(NodeSettings.IaxPort);
        public const string MicGain = nameof(NodeSettings.MicGain);
        public const string TimeOutTimerSeconds = nameof(NodeSettings.TimeOutTimerSeconds);
        public const string IdReminderMinutes = nameof(NodeSettings.IdReminderMinutes);
    }

    /// <summary>ASL3's registration module stores the secret in an 80-byte buffer.</summary>
    public const int MaxPasswordLength = 79;

    [GeneratedRegex(@"^\d{3,7}$")]
    private static partial Regex NodeNumberPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,6}(/[A-Za-z0-9]{1,6})?$")]
    private static partial Regex CallsignPattern();

    public static bool IsValidNodeNumber(string? value) => value != null && NodeNumberPattern().IsMatch(value);

    /// <summary>Trims text fields, upper-cases the callsign and cleans the favourites list.</summary>
    public static NodeSettings Normalize(NodeSettings s) => s with
    {
        NodeNumber = (s.NodeNumber ?? "").Trim(),
        Callsign = (s.Callsign ?? "").Trim().ToUpperInvariant(),
        DefaultTargetNode = (s.DefaultTargetNode ?? "").Trim(),
        RegistrationUrl = (s.RegistrationUrl ?? "").Trim(),
        FavoriteNodes = (s.FavoriteNodes ?? [])
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList()
    };

    /// <summary>Returns every problem found; an empty list means the configuration is usable.</summary>
    public static IReadOnlyList<SettingsIssue> Validate(NodeSettings settings, string? password)
    {
        var s = Normalize(settings);
        var issues = new List<SettingsIssue>();

        if (s.NodeNumber.Length == 0)
            issues.Add(new(Fields.NodeNumber, "Enter your AllStarLink node number."));
        else if (!NodeNumberPattern().IsMatch(s.NodeNumber))
            issues.Add(new(Fields.NodeNumber, "Node number must be 3 to 7 digits."));

        if (string.IsNullOrEmpty(password))
            issues.Add(new(Fields.Password, "Enter your node password (set on the AllStarLink portal)."));
        else if (password.Length > MaxPasswordLength)
            issues.Add(new(Fields.Password, $"Password is too long (maximum {MaxPasswordLength} characters)."));

        if (s.Callsign.Length == 0 && !s.ListenOnly)
            issues.Add(new(Fields.Callsign, "Enter your callsign, or choose 'Listen only' if you don't have one."));
        else if (s.Callsign.Length > 0 && !CallsignPattern().IsMatch(s.Callsign))
            issues.Add(new(Fields.Callsign, "Callsign may contain only letters, digits and one '/'."));

        if (s.DefaultTargetNode.Length > 0 && !NodeNumberPattern().IsMatch(s.DefaultTargetNode))
            issues.Add(new(Fields.DefaultTargetNode, "Default target must be a 3 to 7 digit node number."));

        foreach (string fav in s.FavoriteNodes.Where(f => !NodeNumberPattern().IsMatch(f)))
            issues.Add(new(Fields.FavoriteNodes, $"Favourite '{fav}' is not a 3 to 7 digit node number."));

        if (!Uri.TryCreate(s.RegistrationUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            issues.Add(new(Fields.RegistrationUrl, "Registration URL must be an https:// address."));

        if (s.IaxPort is < 1 or > 65535)
            issues.Add(new(Fields.IaxPort, "IAX port must be between 1 and 65535."));

        if (float.IsNaN(s.MicGain) || s.MicGain is < 0.1f or > 8f)
            issues.Add(new(Fields.MicGain, "Microphone gain must be between 0.1 and 8."));

        if (s.TimeOutTimerSeconds is < 10 or > 600)
            issues.Add(new(Fields.TimeOutTimerSeconds, "Time-out timer must be between 10 and 600 seconds."));

        if (s.IdReminderMinutes is < 1 or > 60)
            issues.Add(new(Fields.IdReminderMinutes, "ID reminder interval must be between 1 and 60 minutes."));

        return issues;
    }
}
