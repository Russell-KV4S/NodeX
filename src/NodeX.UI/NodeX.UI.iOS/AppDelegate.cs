using System;
using AVFoundation;
using Foundation;
using UIKit;
using UserNotifications;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using NodeX.UI.iOS.Audio;
using NodeX.UI.iOS.Security;
using NodeX.UI.iOS.Services;

namespace NodeX.UI.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the
// User Interface of the application, as well as listening (and optionally responding) to
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    private const string IdReminderNotificationId = "nodex_id_reminder";

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
        AppServices.BleTransport ??= new IosBlePttTransport();
        AppServices.MicrophonePermissionRequester = RequestMicrophoneOrOpenSettings;
        AppServices.PermissionsRequester = OpenPermissionSettings;
        AppServices.IdReminderNotifier = NotifyIdReminder;
        AppServices.HapticFeedback = TriggerHapticFeedback;

        AppServices.Initialize(
            new IosKeychainSecretStore(),
            () => new IosAudioDeviceService(session),
            // Zero-amplitude PCM during receive silence keeps the app alive in the background.
            IosAudioDeviceService.RecommendedEngineOptions,
            appVersion: BundleVersion());

        UpdateMicrophoneState();
    }

#pragma warning disable CA1422 // iOS 13.0+ compatibility for AVAudioSession.RecordPermission and UIImpactFeedbackGenerator
    private static void UpdateMicrophoneState()
    {
        try
        {
            var perm = AVAudioSession.SharedInstance().RecordPermission;
            // Undetermined allows the first PTT press to trigger iOS's system permission dialog;
            // Denied blocks transmit and offers to open Settings.
            AppServices.Runtime.Transmit.MicrophoneAllowed = perm != AVAudioSessionRecordPermission.Denied;
        }
        catch (Exception) { }
    }

    private static void RequestMicrophoneOrOpenSettings()
    {
        try
        {
            var audioSession = AVAudioSession.SharedInstance();
            if (audioSession.RecordPermission == AVAudioSessionRecordPermission.Undetermined)
            {
                audioSession.RequestRecordPermission(granted =>
                {
                    AppServices.Runtime.Transmit.MicrophoneAllowed = granted;
                });
                return;
            }

            OpenPermissionSettings();
        }
        catch (Exception) { }
    }

    private static void OpenPermissionSettings()
    {
        var url = new NSUrl(UIApplication.OpenSettingsUrlString);
        if (UIApplication.SharedApplication.CanOpenUrl(url))
            UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
    }

    private static void NotifyIdReminder(bool due)
    {
        try
        {
            var center = UNUserNotificationCenter.Current;
            if (!due)
            {
                center.RemovePendingNotificationRequests([IdReminderNotificationId]);
                center.RemoveDeliveredNotifications([IdReminderNotificationId]);
                return;
            }

            center.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound, (granted, _) =>
            {
                if (!granted) return;
                var content = new UNMutableNotificationContent
                {
                    Title = "Time to give your callsign",
                    Body = "Ten minutes of transmitting have passed — identify your station."
                };
                var request = UNNotificationRequest.FromIdentifier(IdReminderNotificationId, content, trigger: null);
                center.AddNotificationRequest(request, null);
            });
        }
        catch (Exception) { }
    }

    private static void TriggerHapticFeedback(bool active)
    {
        try
        {
            UIApplication.SharedApplication.InvokeOnMainThread(() =>
            {
                using var generator = new UIImpactFeedbackGenerator(
                    active ? UIImpactFeedbackStyle.Medium : UIImpactFeedbackStyle.Light);
                generator.Prepare();
                generator.ImpactOccurred();
            });
        }
        catch (Exception) { }
    }
#pragma warning restore CA1422

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
