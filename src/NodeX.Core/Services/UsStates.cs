using System.Text.RegularExpressions;

namespace NodeX.Core;

/// <summary>
/// US states for the node search. The AllStarLink list has only one free-text "Location" column (no state, county
/// or city fields), written however each owner liked: "Austin, TX", "Houston/Texas", "Las Vegas NV", "Omaha,
/// Nebraska", or just a city. <see cref="Matches"/> recognises the common spellings; it cannot know a state that
/// the owner left out.
/// </summary>
public static class UsStates
{
    public static readonly IReadOnlyList<(string Code, string Name)> All =
    [
        ("AL", "Alabama"), ("AK", "Alaska"), ("AZ", "Arizona"), ("AR", "Arkansas"), ("CA", "California"),
        ("CO", "Colorado"), ("CT", "Connecticut"), ("DE", "Delaware"), ("DC", "District of Columbia"), ("FL", "Florida"),
        ("GA", "Georgia"), ("HI", "Hawaii"), ("ID", "Idaho"), ("IL", "Illinois"), ("IN", "Indiana"),
        ("IA", "Iowa"), ("KS", "Kansas"), ("KY", "Kentucky"), ("LA", "Louisiana"), ("ME", "Maine"),
        ("MD", "Maryland"), ("MA", "Massachusetts"), ("MI", "Michigan"), ("MN", "Minnesota"), ("MS", "Mississippi"),
        ("MO", "Missouri"), ("MT", "Montana"), ("NE", "Nebraska"), ("NV", "Nevada"), ("NH", "New Hampshire"),
        ("NJ", "New Jersey"), ("NM", "New Mexico"), ("NY", "New York"), ("NC", "North Carolina"), ("ND", "North Dakota"),
        ("OH", "Ohio"), ("OK", "Oklahoma"), ("OR", "Oregon"), ("PA", "Pennsylvania"), ("RI", "Rhode Island"),
        ("SC", "South Carolina"), ("SD", "South Dakota"), ("TN", "Tennessee"), ("TX", "Texas"), ("UT", "Utah"),
        ("VT", "Vermont"), ("VA", "Virginia"), ("WA", "Washington"), ("WV", "West Virginia"), ("WI", "Wisconsin"),
        ("WY", "Wyoming")
    ];

    public static string? NameOf(string code) =>
        All.Where(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)).Select(s => s.Name).FirstOrDefault();

    /// <summary>True when <paramref name="location"/> names the state, as its full name or its two-letter code.</summary>
    public static bool Matches(string location, string code)
    {
        string? name = NameOf(code);
        if (name == null || string.IsNullOrWhiteSpace(location)) return false;
        code = code.ToUpperInvariant();

        // Code: an upper-case token ("Las Vegas NV", "Memphis,TN") or any case straight after a comma ("Dallas, tx").
        if (Regex.IsMatch(location, $@"(?<![A-Za-z]){code}(?![A-Za-z])")) return true;
        if (Regex.IsMatch(location, $@",\s*{code}(?![A-Za-z])", RegexOptions.IgnoreCase)) return true;

        // Full name as whole words, but not "Kansas City" / "Oklahoma City", and not "West Virginia" for Virginia.
        string pattern = $@"(?<![A-Za-z]){Regex.Escape(name)}(?![A-Za-z])(?!\s+City(?![A-Za-z]))";
        if (code == "VA") pattern = @"(?<!West\s)" + pattern;
        if (code == "WA") pattern += @"(?!,?\s*D\.?C)";
        return Regex.IsMatch(location, pattern, RegexOptions.IgnoreCase);
    }
}
