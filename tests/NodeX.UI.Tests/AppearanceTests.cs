using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NodeX.Core.Settings;
using NodeX.UI;
using NodeX.UI.ViewModels;
using NodeX.UI.Views;

namespace NodeX.Core.Tests;

public class AppearanceTests
{
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    [InlineData("Amoled")]
    [InlineData("Nord")]
    [InlineData("Terminal")]
    [InlineData("Sepia")]
    public void KeyedPtt_UsesTheThemeFillAndReadableText(string theme)
    {
        Appearance.Apply(Application.Current!, new() { Theme = theme, TextScale = 1.5 });
        using var vm = new MainViewModel { IsTransmitting = true };
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 420, Height = 860 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            var face = view.FindControl<Border>("PttFace")!;
            Assert.Contains("keyed", face.Classes);
            Assert.Equal(((ISolidColorBrush)Application.Current!.Resources["TransmitFillBrush"]!).Color,
                ((ISolidColorBrush)face.Background!).Color);
            var headline = face.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "PUSH TO TALK");
            Assert.Equal(((ISolidColorBrush)Application.Current.Resources["OnRedBrush"]!).Color,
                ((ISolidColorBrush)headline.Foreground!).Color);
            var output = Environment.GetEnvironmentVariable("NODEX_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = window.CaptureRenderedFrame();
                using var file = File.Create(Path.Combine(output, $"Keyed-{theme}.png"));
                bitmap!.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    [InlineData("Amoled")]
    [InlineData("Nord")]
    [InlineData("Terminal")]
    [InlineData("Sepia")]
    public void TextAndTransmitColors_HaveReadableContrast(string theme)
    {
        Appearance.Apply(Application.Current!, new() { Theme = theme });
        Color ColorOf(string key) => ((ISolidColorBrush)Application.Current!.Resources[key]!).Color;
        double Luminance(Color c)
        {
            double Linear(byte channel) { double s = channel / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }
        double Ratio(string text, string background)
        {
            double a = Luminance(ColorOf(text)), b = Luminance(ColorOf(background));
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
        Assert.True(Ratio("TextPrimaryBrush", "CardBackgroundBrush") >= 4.5, $"{theme}: TextPrimary on Card");
        Assert.True(Ratio("TextSecondaryBrush", "CardBackgroundBrush") >= 4.5, $"{theme}: TextSecondary on Card");
        Assert.True(Ratio("TextMutedBrush", "CardBackgroundBrush") >= 4.5, $"{theme}: TextMuted on Card");
        Assert.True(Ratio("TextMutedBrush", "HeaderBackgroundBrush") >= 4.5, $"{theme}: TextMuted on Header");
        Assert.True(Ratio("WarningTextBrush", "WarningBackgroundBrush") >= 4.5, $"{theme}: WarningText on WarningBackground");
        Assert.True(Ratio("OnRedBrush", "TransmitFillBrush") >= 4.5, $"{theme}: OnRed on TransmitFill");
        Assert.True(Ratio("OnGreenBrush", "RxGreenBrush") >= 4.5, $"{theme}: OnGreen on RxGreen");
    }

    [AvaloniaFact]
    public void SystemTheme_FollowsPlatformLightAndDark()
    {
        var app = Application.Current!;
        var settings = new NodeSettings { Theme = AppThemes.System };

        Appearance.Apply(app, settings, PlatformThemeVariant.Light);
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        Assert.Equal(Color.Parse("#FFFFFF"), ((ISolidColorBrush)app.Resources["CardBackgroundBrush"]!).Color);

        Appearance.Apply(app, settings, PlatformThemeVariant.Dark);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal(Color.Parse("#161B22"), ((ISolidColorBrush)app.Resources["CardBackgroundBrush"]!).Color);

        Assert.Equal(AppThemes.Light, Appearance.ResolveEffectiveTheme(AppThemes.System, PlatformThemeVariant.Light));
        Assert.Equal(AppThemes.Dark, Appearance.ResolveEffectiveTheme(AppThemes.System, PlatformThemeVariant.Dark));
        Assert.Equal(AppThemes.Dark, Appearance.ResolveEffectiveTheme(AppThemes.System, null));
        Assert.Equal(AppThemes.Nord, Appearance.ResolveEffectiveTheme(AppThemes.Nord, PlatformThemeVariant.Light));
    }

    [AvaloniaFact]
    public void ThemeAndTextSize_UpdateExistingControls()
    {
        var view = new SettingsView();
        var window = new Window { Content = view, Width = 420, Height = 860 };
        window.Show();
        try
        {
            Appearance.Apply(Application.Current!, new() { Theme = "Light", TextScale = 1.5 });
            Dispatcher.UIThread.RunJobs();
            var title = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Station settings");
            Assert.Equal(33, title.FontSize);
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            Assert.Equal(21d, Application.Current.Resources["FontSize14"]);
            Assert.Equal(Color.Parse("#FFFFFF"), ((ISolidColorBrush)Application.Current.Resources["CardBackgroundBrush"]!).Color);
            Appearance.Apply(Application.Current, new() { Theme = "HighContrast" });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
            Assert.Equal(Color.Parse("#FFFFFF"), ((ISolidColorBrush)Application.Current.Resources["CardBorderBrush"]!).Color);
            Assert.Equal(14d, Application.Current.Resources["FontSize14"]);
            Assert.Equal(22, title.FontSize);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Dark", 1.0, 1050, 760, false)]
    [InlineData("Light", 1.5, 1050, 760, false)]
    [InlineData("HighContrast", 1.5, 420, 860, false)]
    [InlineData("Light", 1.5, 420, 860, true)]
    [InlineData("HighContrast", 1.5, 860, 420, false)]
    [InlineData("Amoled", 1.0, 420, 860, false)]
    [InlineData("Nord", 1.0, 420, 860, false)]
    [InlineData("Terminal", 1.0, 420, 860, false)]
    [InlineData("Sepia", 1.0, 420, 860, false)]
    public async Task ViewsRender_WithThemesAndLargeText(string theme, double scale, int width, int height, bool settings)
    {
        Appearance.Apply(Application.Current!, new() { Theme = theme, TextScale = scale });
        using var vm = new MainViewModel();
        await vm.Settings.LoadAsync();
        vm.Settings.ThemeIndex = AppThemes.IndexOf(theme);
        vm.Settings.TextScale = scale;
        vm.IsSettingsOpen = settings;
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(100); // let font fallback invalidate measurement before the captured frame
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            using var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            Assert.Equal(width, bitmap.PixelSize.Width);
            Assert.Equal(height, bitmap.PixelSize.Height);
            Assert.True(view.Bounds.Width > 0);
            if (!settings)
            {
                // The docked key, meters and navigation must fit without text-size changes pushing them off screen.
                foreach (var name in new[] { "NavBar", "PttButton", "MetersGrid" })
                {
                    var control = view.FindControl<Control>(name)!;
                    if (!control.IsEffectivelyVisible) continue;
                    var origin = control.TranslatePoint(default, window)!.Value;
                    Assert.True(origin.Y + control.Bounds.Height <= height + 1, $"{name} extends below the window");
                }
            }
            var output = Environment.GetEnvironmentVariable("NODEX_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var file = File.Create(Path.Combine(output, $"{theme}-{scale:F1}-{width}x{height}{(settings ? "-settings" : "")}.png"));
                bitmap.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(420, 860, true, false)]
    [InlineData(860, 420, true, true)]
    [InlineData(1050, 760, false, false)]
    public void AudioMeters_LiveInsidePttFrame_AndStayPinnedWhenDocked(int width, int height, bool expectDocked, bool expectSingleRowMeters)
    {
        Appearance.Apply(Application.Current!, new() { Theme = "Dark", TextScale = 1.0 });
        var previousSpeakerSetter = AppServices.SpeakerphoneSetter;
        AppServices.SpeakerphoneSetter = _ => true;
        using var vm = new MainViewModel
        {
            IsSpeakerOn = true,
            MicAudioLevel = 0.25,
            RxAudioLevel = 0.68,
        };
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

            var pttCard = view.FindControl<Border>("PttCard")!;
            var pttDock = view.FindControl<Border>("PttDock")!;
            var bodyScroll = view.FindControl<ScrollViewer>("BodyScroll")!;
            var metersGrid = view.FindControl<Grid>("MetersGrid")!;
            var micBar = view.FindControl<ProgressBar>("MicMeterBar")!;
            var spkBar = view.FindControl<ProgressBar>("SpkMeterBar")!;
            var pttButton = view.FindControl<Button>("PttButton")!;
            var speakerButton = view.FindControl<Button>("SpeakerButton")!;

            Assert.True(metersGrid.IsEffectivelyVisible);
            Assert.True(micBar.IsEffectivelyVisible);
            Assert.True(spkBar.IsEffectivelyVisible);
            Assert.True(speakerButton.IsEffectivelyVisible);

            // MIC/SPK meters share the same PttCard frame as PTT and Speaker.
            Assert.Contains(pttCard, metersGrid.GetVisualAncestors());
            Assert.Contains(pttCard, pttButton.GetVisualAncestors());
            Assert.Contains(pttCard, speakerButton.GetVisualAncestors());

            // Meters sit above the PTT button so a thumb pressing PTT does not cover them.
            var metersOrigin = metersGrid.TranslatePoint(default, window)!.Value;
            var pttOrigin = pttButton.TranslatePoint(default, window)!.Value;
            var speakerOrigin = speakerButton.TranslatePoint(default, window)!.Value;
            Assert.True(metersOrigin.Y + metersGrid.Bounds.Height <= pttOrigin.Y + 1);
            Assert.True(pttOrigin.X + pttButton.Bounds.Width <= speakerOrigin.X + 1,
                $"PTT ({pttOrigin.X:F1}..{pttOrigin.X + pttButton.Bounds.Width:F1}) overlaps Speaker ({speakerOrigin.X:F1}..{speakerOrigin.X + speakerButton.Bounds.Width:F1})");

            if (expectSingleRowMeters)
            {
                Assert.Single(metersGrid.RowDefinitions);
            }
            else
            {
                Assert.Equal(2, metersGrid.RowDefinitions.Count);
            }

            if (expectDocked)
            {
                Assert.True(pttDock.IsVisible);
                Assert.Same(pttCard, pttDock.Child);
                Assert.DoesNotContain(bodyScroll, metersGrid.GetVisualAncestors());

                // Scrolling the body must not move the docked meters.
                var beforeY = metersOrigin.Y;
                bodyScroll.Offset = new Vector(0, 200);
                Dispatcher.UIThread.RunJobs();
                var afterY = metersGrid.TranslatePoint(default, window)!.Value.Y;
                Assert.Equal(beforeY, afterY, precision: 1);
            }
            else
            {
                Assert.False(pttDock.IsVisible);
                Assert.Contains(bodyScroll, metersGrid.GetVisualAncestors());
            }

            var output = Environment.GetEnvironmentVariable("NODEX_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = window.CaptureRenderedFrame();
                using var file = File.Create(Path.Combine(output, $"MetersInPtt-{width}x{height}.png"));
                bitmap!.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            AppServices.SpeakerphoneSetter = previousSpeakerSetter;
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(420, 860)]
    [InlineData(1050, 760)]
    public void RxGainCard_RendersInPhoneAndDesktopLayouts(int width, int height)
    {
        Appearance.Apply(Application.Current!, new() { Theme = "Dark", TextScale = 1.0 });
        using var vm = new MainViewModel { RxGain = 2.0, RxAutoLevel = true };
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

            var card = view.FindControl<Border>("RxGainCard")!;
            var slider = view.FindControl<Slider>("RxGainSlider")!;
            var autoBtn = view.FindControl<Button>("RxAutoLevelButton")!;

            Assert.True(card.IsEffectivelyVisible);
            Assert.True(slider.IsEffectivelyVisible);
            Assert.True(autoBtn.IsEffectivelyVisible);
            Assert.Contains("active", autoBtn.Classes);

            var cardOrigin = card.TranslatePoint(default, window)!.Value;
            Assert.True(cardOrigin.X >= 0 && cardOrigin.X + card.Bounds.Width <= width + 1);
            Assert.True(cardOrigin.Y >= 0 && cardOrigin.Y + card.Bounds.Height <= height + 1);
        }
        finally { window.Close(); }
    }
}
