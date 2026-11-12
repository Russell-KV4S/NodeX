using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using NodeX.UI.iOS.Audio;
using NodeX.UI.iOS.Security;

namespace NodeX.UI.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the
// User Interface of the application, as well as listening (and optionally responding) to
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        ConfigureServices();
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    private static void ConfigureServices()
    {
        // One shared audio-session manager: it registers interruption/route observers, so it must
        // not be recreated for every link.
        var session = new IosAudioSessionManager();

        // iOS apps do not quit themselves (the user swipes them away), so there is no "Exit NodeX" item.
        AppServices.CanExit = false;

        AppServices.Initialize(
            new IosKeychainSecretStore(),
            () => new IosAudioDeviceService(session),
            // Zero-amplitude PCM during receive silence keeps the app alive in the background.
            IosAudioDeviceService.RecommendedEngineOptions,
            appVersion: BundleVersion());
    }

    /// <summary>"1.15 (17)" from Info.plist, for Settings and the diagnostic log header; null if it cannot be read.</summary>
    private static string? BundleVersion()
    {
        try
        {
            var info = NSBundle.MainBundle.InfoDictionary;
            string? shortVersion = info?.ObjectForKey(new NSString("CFBundleShortVersionString"))?.ToString();
            string? build = info?.ObjectForKey(new NSString("CFBundleVersion"))?.ToString();
            if (string.IsNullOrEmpty(shortVersion)) return null;
            return string.IsNullOrEmpty(build) ? shortVersion : $"{shortVersion} ({build})";
        }
        catch (System.Exception)
        {
            return null;
        }
    }
}
