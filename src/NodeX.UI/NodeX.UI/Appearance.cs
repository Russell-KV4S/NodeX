using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using NodeX.Core.Settings;

namespace NodeX.UI;

/// <summary>Updates dynamic resources so existing screens change without recreating the UI.</summary>
public static class Appearance
{
    public static void Apply(Application app, NodeSettings settings)
    {
        bool light = settings.Theme == "Light";
        bool contrast = settings.Theme == "HighContrast";
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        string[] keys = ["ConsoleBackgroundBrush", "CardBackgroundBrush", "CardBorderBrush", "HeaderBackgroundBrush",
            "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush", "TxRedBrush", "RxGreenBrush",
            "AmberAccentBrush", "CyanAccentBrush", "DangerButtonBrush"];
        string[] colors = light
            ? ["#F3F5F9", "#FFFFFF", "#667085", "#E4E9F2", "#101828", "#344054", "#475467", "#B42318", "#067647", "#854A0E", "#006B80", "#B42318"]
            : contrast
                ? ["#000000", "#000000", "#FFFFFF", "#000000", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FF8585", "#66FF99", "#FFFF00", "#66EEFF", "#B91C1C"]
                : ["#0D1117", "#161B22", "#30363D", "#1F242C", "#F0F6FC", "#8B949E", "#848D97", "#EF4444", "#10B981", "#F59E0B", "#06B6D4", "#DC2626"];
        for (int i = 0; i < keys.Length; i++) app.Resources[keys[i]] = new SolidColorBrush(Color.Parse(colors[i]));
        app.Resources["StatusTextBrush"] = new SolidColorBrush(Color.Parse(light ? "#101828" : "#FFFFFF"));
        app.Resources["SelectedBackgroundBrush"] = new SolidColorBrush(Color.Parse(light ? "#DCE8F7" : contrast ? "#202020" : "#172B3D"));
        app.Resources["WarningBackgroundBrush"] = new SolidColorBrush(Color.Parse(light ? "#FFF3CD" : "#302514"));
        app.Resources["WarningTextBrush"] = new SolidColorBrush(Color.Parse(light ? "#713F12" : "#FDE68A"));
        app.Resources["ErrorBackgroundBrush"] = new SolidColorBrush(Color.Parse(light ? "#FEE4E2" : "#2A1215"));
        app.Resources["SuccessBackgroundBrush"] = new SolidColorBrush(Color.Parse(light ? "#D1FADF" : "#0F2A22"));
        app.Resources["RxBackgroundBrush"] = new SolidColorBrush(Color.Parse(light ? "#D1FADF" : "#16432B"));
        app.Resources["TxBackgroundBrush"] = new SolidColorBrush(Color.Parse(light ? "#FEE4E2" : "#462025"));
        app.Resources["OnGreenBrush"] = new SolidColorBrush(Color.Parse(light ? "#FFFFFF" : "#000000"));
        app.Resources["OnRedBrush"] = new SolidColorBrush(Color.Parse(contrast ? "#000000" : "#FFFFFF"));
        app.Resources["TransmitFillBrush"] = new SolidColorBrush(Color.Parse(contrast ? "#FF8585" : light ? "#B42318" : "#DC2626"));
        double scale = double.IsFinite(settings.TextScale) ? Math.Clamp(settings.TextScale, 1, 1.5) : 1;
        for (int size = 8; size <= 64; size++) app.Resources[$"FontSize{size}"] = size * scale;
        app.Resources["ControlMinHeight"] = Math.Max(44, 32 * scale);
        app.Resources["ControlContentThemeFontSize"] = 14 * scale;
    }
}
