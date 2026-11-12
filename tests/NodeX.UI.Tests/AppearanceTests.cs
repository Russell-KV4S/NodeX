using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
        Assert.True(Ratio("TextPrimaryBrush", "CardBackgroundBrush") >= 4.5);
        Assert.True(Ratio("TextMutedBrush", "CardBackgroundBrush") >= 4.5);
        Assert.True(Ratio("OnRedBrush", "TransmitFillBrush") >= 4.5);
        Assert.True(Ratio("OnGreenBrush", "RxGreenBrush") >= 4.5);
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
    public async Task ViewsRender_WithThemesAndLargeText(string theme, double scale, int width, int height, bool settings)
    {
        Appearance.Apply(Application.Current!, new() { Theme = theme, TextScale = scale });
        using var vm = new MainViewModel();
        await vm.Settings.LoadAsync();
        vm.Settings.ThemeIndex = theme == "Light" ? 1 : theme == "HighContrast" ? 2 : 0;
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
                // The docked key and navigation must fit without text-size changes pushing them off screen.
                foreach (var name in new[] { "NavBar", "PttButton" })
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
}
