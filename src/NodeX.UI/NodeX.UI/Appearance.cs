using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using NodeX.Core.Settings;

namespace NodeX.UI;

/// <summary>Updates dynamic resources so existing screens change without recreating the UI.</summary>
public static class Appearance
{
    private sealed record Palette(
        bool IsLight,
        string ConsoleBackground,
        string CardBackground,
        string CardBorder,
        string HeaderBackground,
        string TextPrimary,
        string TextSecondary,
        string TextMuted,
        string TxRed,
        string RxGreen,
        string AmberAccent,
        string CyanAccent,
        string DangerButton,
        string StatusText,
        string SelectedBackground,
        string WarningBackground,
        string WarningText,
        string ErrorBackground,
        string SuccessBackground,
        string RxBackground,
        string TxBackground,
        string OnGreen,
        string OnRed,
        string TransmitFill,
        string PrivateBackground,
        string PrivateBorder);

    private static readonly Palette DarkPalette = new(
        IsLight: false,
        ConsoleBackground: "#0D1117",
        CardBackground: "#161B22",
        CardBorder: "#30363D",
        HeaderBackground: "#1F242C",
        TextPrimary: "#F0F6FC",
        TextSecondary: "#8B949E",
        TextMuted: "#848D97",
        TxRed: "#EF4444",
        RxGreen: "#10B981",
        AmberAccent: "#F59E0B",
        CyanAccent: "#06B6D4",
        DangerButton: "#DC2626",
        StatusText: "#FFFFFF",
        SelectedBackground: "#172B3D",
        WarningBackground: "#302514",
        WarningText: "#FDE68A",
        ErrorBackground: "#2A1215",
        SuccessBackground: "#0F2A22",
        RxBackground: "#16432B",
        TxBackground: "#462025",
        OnGreen: "#000000",
        OnRed: "#FFFFFF",
        TransmitFill: "#DC2626",
        PrivateBackground: "#1A2338",
        PrivateBorder: "#06B6D4");

    private static readonly Palette LightPalette = new(
        IsLight: true,
        ConsoleBackground: "#F3F5F9",
        CardBackground: "#FFFFFF",
        CardBorder: "#667085",
        HeaderBackground: "#E4E9F2",
        TextPrimary: "#101828",
        TextSecondary: "#344054",
        TextMuted: "#475467",
        TxRed: "#B42318",
        RxGreen: "#067647",
        AmberAccent: "#854A0E",
        CyanAccent: "#006B80",
        DangerButton: "#B42318",
        StatusText: "#101828",
        SelectedBackground: "#DCE8F7",
        WarningBackground: "#FFF3CD",
        WarningText: "#713F12",
        ErrorBackground: "#FEE4E2",
        SuccessBackground: "#D1FADF",
        RxBackground: "#D1FADF",
        TxBackground: "#FEE4E2",
        OnGreen: "#FFFFFF",
        OnRed: "#FFFFFF",
        TransmitFill: "#B42318",
        PrivateBackground: "#E0F4F8",
        PrivateBorder: "#006B80");

    private static readonly Palette HighContrastPalette = new(
        IsLight: false,
        ConsoleBackground: "#000000",
        CardBackground: "#000000",
        CardBorder: "#FFFFFF",
        HeaderBackground: "#000000",
        TextPrimary: "#FFFFFF",
        TextSecondary: "#FFFFFF",
        TextMuted: "#FFFFFF",
        TxRed: "#FF8585",
        RxGreen: "#66FF99",
        AmberAccent: "#FFFF00",
        CyanAccent: "#66EEFF",
        DangerButton: "#B91C1C",
        StatusText: "#FFFFFF",
        SelectedBackground: "#202020",
        WarningBackground: "#302514",
        WarningText: "#FDE68A",
        ErrorBackground: "#2A1215",
        SuccessBackground: "#0F2A22",
        RxBackground: "#16432B",
        TxBackground: "#462025",
        OnGreen: "#000000",
        OnRed: "#000000",
        TransmitFill: "#FF8585",
        PrivateBackground: "#102030",
        PrivateBorder: "#66EEFF");

    private static readonly Palette AmoledPalette = new(
        IsLight: false,
        ConsoleBackground: "#000000",
        CardBackground: "#0A0D12",
        CardBorder: "#232A33",
        HeaderBackground: "#05070A",
        TextPrimary: "#F3F6FA",
        TextSecondary: "#9AA4B2",
        TextMuted: "#858F9D",
        TxRed: "#EF4444",
        RxGreen: "#10B981",
        AmberAccent: "#F59E0B",
        CyanAccent: "#22D3EE",
        DangerButton: "#DC2626",
        StatusText: "#FFFFFF",
        SelectedBackground: "#132234",
        WarningBackground: "#261D0D",
        WarningText: "#FDE68A",
        ErrorBackground: "#260D10",
        SuccessBackground: "#09221A",
        RxBackground: "#0E3321",
        TxBackground: "#38141A",
        OnGreen: "#000000",
        OnRed: "#FFFFFF",
        TransmitFill: "#DC2626",
        PrivateBackground: "#0D192B",
        PrivateBorder: "#22D3EE");

    private static readonly Palette NordPalette = new(
        IsLight: false,
        ConsoleBackground: "#2E3440",
        CardBackground: "#3B4252",
        CardBorder: "#4C566A",
        HeaderBackground: "#272C36",
        TextPrimary: "#ECEFF4",
        TextSecondary: "#D0D7E3",
        TextMuted: "#ABB5C7",
        TxRed: "#E06C75",
        RxGreen: "#A3BE8C",
        AmberAccent: "#EBCB8B",
        CyanAccent: "#88C0D0",
        DangerButton: "#B53D47",
        StatusText: "#ECEFF4",
        SelectedBackground: "#434C5E",
        WarningBackground: "#3F3828",
        WarningText: "#F4DFB0",
        ErrorBackground: "#412B30",
        SuccessBackground: "#2B3E33",
        RxBackground: "#31493B",
        TxBackground: "#4F2E34",
        OnGreen: "#000000",
        OnRed: "#FFFFFF",
        TransmitFill: "#B53D47",
        PrivateBackground: "#2F3E4E",
        PrivateBorder: "#88C0D0");

    private static readonly Palette TerminalPalette = new(
        IsLight: false,
        ConsoleBackground: "#070B08",
        CardBackground: "#0D150F",
        CardBorder: "#1F4028",
        HeaderBackground: "#0A110C",
        TextPrimary: "#D8FFE1",
        TextSecondary: "#86C797",
        TextMuted: "#69A97A",
        TxRed: "#FF5252",
        RxGreen: "#00E676",
        AmberAccent: "#FACC15",
        CyanAccent: "#38F28B",
        DangerButton: "#C81E1E",
        StatusText: "#D8FFE1",
        SelectedBackground: "#13291B",
        WarningBackground: "#29230A",
        WarningText: "#FEF08A",
        ErrorBackground: "#2D1114",
        SuccessBackground: "#0B2B17",
        RxBackground: "#103D21",
        TxBackground: "#3E1519",
        OnGreen: "#000000",
        OnRed: "#FFFFFF",
        TransmitFill: "#C81E1E",
        PrivateBackground: "#0E241C",
        PrivateBorder: "#38F28B");

    private static readonly Palette SepiaPalette = new(
        IsLight: true,
        ConsoleBackground: "#EFE6D5",
        CardBackground: "#FBF6EC",
        CardBorder: "#B8A68A",
        HeaderBackground: "#E5D9C3",
        TextPrimary: "#2A2118",
        TextSecondary: "#4E3F30",
        TextMuted: "#5F4F3E",
        TxRed: "#A6261B",
        RxGreen: "#1F6E43",
        AmberAccent: "#8A4B08",
        CyanAccent: "#0F6270",
        DangerButton: "#A6261B",
        StatusText: "#2A2118",
        SelectedBackground: "#E4D8C1",
        WarningBackground: "#F9E8B8",
        WarningText: "#613807",
        ErrorBackground: "#F8D8D2",
        SuccessBackground: "#D5ECD9",
        RxBackground: "#D5ECD9",
        TxBackground: "#F8D8D2",
        OnGreen: "#FFFFFF",
        OnRed: "#FFFFFF",
        TransmitFill: "#A6261B",
        PrivateBackground: "#DCEAE8",
        PrivateBorder: "#0F6270");

    /// <summary>
    /// Resolves a stored theme setting to a concrete palette key (<see cref="AppThemes.System"/> resolves to
    /// <see cref="AppThemes.Light"/> when the OS is in light mode, otherwise <see cref="AppThemes.Dark"/>).
    /// </summary>
    public static string ResolveEffectiveTheme(string? theme, PlatformThemeVariant? systemVariant) =>
        theme == AppThemes.System
            ? (systemVariant == PlatformThemeVariant.Light ? AppThemes.Light : AppThemes.Dark)
            : AppThemes.IsValid(theme) ? theme! : AppThemes.Dark;

    public static void Apply(Application app, NodeSettings settings, PlatformThemeVariant? systemVariantOverride = null)
    {
        PlatformThemeVariant? systemVariant = systemVariantOverride
            ?? app.PlatformSettings?.GetColorValues().ThemeVariant;

        string effective = ResolveEffectiveTheme(settings.Theme, systemVariant);
        Palette palette = effective switch
        {
            AppThemes.Light => LightPalette,
            AppThemes.HighContrast => HighContrastPalette,
            AppThemes.Amoled => AmoledPalette,
            AppThemes.Nord => NordPalette,
            AppThemes.Terminal => TerminalPalette,
            AppThemes.Sepia => SepiaPalette,
            _ => DarkPalette
        };

        app.RequestedThemeVariant = palette.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

        SetBrush(app, "ConsoleBackgroundBrush", palette.ConsoleBackground);
        SetBrush(app, "CardBackgroundBrush", palette.CardBackground);
        SetBrush(app, "CardBorderBrush", palette.CardBorder);
        SetBrush(app, "HeaderBackgroundBrush", palette.HeaderBackground);
        SetBrush(app, "TextPrimaryBrush", palette.TextPrimary);
        SetBrush(app, "TextSecondaryBrush", palette.TextSecondary);
        SetBrush(app, "TextMutedBrush", palette.TextMuted);
        SetBrush(app, "TxRedBrush", palette.TxRed);
        SetBrush(app, "RxGreenBrush", palette.RxGreen);
        SetBrush(app, "AmberAccentBrush", palette.AmberAccent);
        SetBrush(app, "CyanAccentBrush", palette.CyanAccent);
        SetBrush(app, "DangerButtonBrush", palette.DangerButton);
        SetBrush(app, "StatusTextBrush", palette.StatusText);
        SetBrush(app, "SelectedBackgroundBrush", palette.SelectedBackground);
        SetBrush(app, "WarningBackgroundBrush", palette.WarningBackground);
        SetBrush(app, "WarningTextBrush", palette.WarningText);
        SetBrush(app, "ErrorBackgroundBrush", palette.ErrorBackground);
        SetBrush(app, "SuccessBackgroundBrush", palette.SuccessBackground);
        SetBrush(app, "RxBackgroundBrush", palette.RxBackground);
        SetBrush(app, "TxBackgroundBrush", palette.TxBackground);
        SetBrush(app, "OnGreenBrush", palette.OnGreen);
        SetBrush(app, "OnRedBrush", palette.OnRed);
        SetBrush(app, "TransmitFillBrush", palette.TransmitFill);
        SetBrush(app, "PrivateBackgroundBrush", palette.PrivateBackground);
        SetBrush(app, "PrivateBorderBrush", palette.PrivateBorder);

        double scale = double.IsFinite(settings.TextScale) ? Math.Clamp(settings.TextScale, 1, 1.5) : 1;
        for (int size = 8; size <= 64; size++) app.Resources[$"FontSize{size}"] = size * scale;
        app.Resources["ControlMinHeight"] = Math.Max(44, 32 * scale);
        app.Resources["ControlContentThemeFontSize"] = 14 * scale;
    }

    private static void SetBrush(Application app, string key, string hex) =>
        app.Resources[key] = new SolidColorBrush(Color.Parse(hex));
}
