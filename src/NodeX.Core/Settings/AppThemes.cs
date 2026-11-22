namespace NodeX.Core.Settings;

/// <summary>
/// Supported appearance themes. <see cref="System"/> is the default and follows the OS dark/light setting.
/// </summary>
public static class AppThemes
{
    public const string System = "System";
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string HighContrast = "HighContrast";
    public const string Amoled = "Amoled";
    public const string Nord = "Nord";
    public const string Terminal = "Terminal";
    public const string Sepia = "Sepia";

    /// <summary>Ordered theme keys matching the Theme picker in Settings.</summary>
    public static readonly IReadOnlyList<string> All =
        [System, Dark, Light, HighContrast, Amoled, Nord, Terminal, Sepia];

    public static bool IsValid(string? theme)
    {
        if (theme is null) return false;
        for (int i = 0; i < All.Count; i++)
            if (All[i] == theme) return true;
        return false;
    }

    public static int IndexOf(string? theme)
    {
        if (theme is not null)
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i] == theme) return i;
        }
        return 0;
    }

    public static string FromIndex(int index) =>
        index >= 0 && index < All.Count ? All[index] : System;
}
