namespace NodeX.Core;

/// <summary>Short, human-readable elapsed times for badges ("5 min", "2 h 05 min", "1 d 4 h").</summary>
public static class DurationText
{
    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (elapsed.TotalMinutes < 1) return "< 1 min";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} min";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} h {elapsed.Minutes:00} min";
        return $"{(int)elapsed.TotalDays} d {elapsed.Hours} h";
    }
}
