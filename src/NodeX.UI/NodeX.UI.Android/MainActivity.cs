using System;
﻿using System.Collections.Generic;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Window;
using Avalonia;
using Avalonia.Android;

namespace NodeX.UI.Android;

[Activity(
    Label = "NodeX",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int PermissionRequestCode = 1001;

    private bool _micPermanentlyDenied;

    /// <summary>The visible activity, so "Exit NodeX" can close it.</summary>
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Declare what this phone supports BEFORE the base class builds the UI: Settings reads these flags when its
        // controls are created, and a flag set afterwards left the phone-button mapper (and the speaker and log
        // options) hidden.
        Current = this;
        AppServices.HardwareKeysSupported = true;
        AppServices.HardwareKeyNamer = code =>
        {
            string name = global::Android.Views.KeyEvent.KeyCodeToString((global::Android.Views.Keycode)code) ?? "";
            name = name.Replace("KEYCODE_", "").Replace('_', ' ').ToLowerInvariant();
            return name.Length == 0 ? $"Key {code}" : char.ToUpperInvariant(name[0]) + name[1..];
        };
        AppServices.MicrophonePermissionRequester = RequestMicrophoneOrOpenSettings;
        AppServices.IdReminderNotifier = due => NodeForegroundService.NotifyIdReminder(ApplicationContext!, due);
        AppServices.HapticFeedback = active =>
        {
            try
            {
                Window?.DecorView?.PerformHapticFeedback(active
                    ? global::Android.Views.FeedbackConstants.KeyboardTap
                    : OperatingSystem.IsAndroidVersionAtLeast(27)
                        ? global::Android.Views.FeedbackConstants.VirtualKeyRelease
                        : global::Android.Views.FeedbackConstants.VirtualKey);
            }
            catch (Exception) { }
        };
        AppServices.ExitHandler = () => AndroidAppExit.Finish(this);
        AppServices.SpeakerphoneSetter = speaker => AndroidAudioRoute.SetSpeaker(this, speaker);
        AppServices.LogSharer = ShareLogText;
        AppServices.BleTransport ??= new AndroidBlePttTransport(this);

        base.OnCreate(savedInstanceState);
        RegisterBackCallback();
        RequestRuntimePermissions();
        UpdateMicrophoneState();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateMicrophoneState();   // the user may have granted it in system settings meanwhile
        NodeForegroundService.RetryMicrophone();   // a link dialled from the car could not get the microphone until now
        MaybeAskBatteryExemption();
    }

    /// <summary>
    /// Back closes an open panel, otherwise it only hides the app: NodeX keeps running (and stays registered) in the
    /// background. Quitting is the explicit "Exit NodeX" menu item.
    /// </summary>
    /// <summary>
    /// Android 13+ with predictive back never calls OnBackPressed, so Back skipped our tab/overlay handling and just
    /// hid the app. Registering a dispatcher callback routes every Back press through the same logic on every version.
    /// </summary>
    private void RegisterBackCallback()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new BackCallback(this));
    }

    private sealed class BackCallback(MainActivity activity) : Java.Lang.Object, IOnBackInvokedCallback
    {
        public void OnBackInvoked() => activity.HandleBack();
    }

    private void HandleBack()
    {
        if (AppServices.BackHandler?.Invoke() == true) return;
        MoveTaskToBack(true);
    }

#pragma warning disable CA1422 // OnBackPressed is the portable route; predictive back is not enabled
    public override void OnBackPressed()
    {
        HandleBack();
    }
#pragma warning restore CA1422

    /// <summary>
    /// Offers every key to the PTT mapper first: the mapped phone button (or the key being learned) is consumed here,
    /// everything else goes on as normal. Only works while this screen is in front.
    /// </summary>
    public override bool DispatchKeyEvent(global::Android.Views.KeyEvent? e)
    {
        if (e != null)
        {
            bool down = e.Action == global::Android.Views.KeyEventActions.Down;
            bool up = e.Action == global::Android.Views.KeyEventActions.Up;
            if ((down || up) && AppServices.HardwarePtt.OnKey((int)e.KeyCode, down, e.RepeatCount > 0)) return true;
        }
        return base.DispatchKeyEvent(e);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        // Denied with no further prompt allowed ("Don't ask again"): only system settings can fix it.
        _micPermanentlyDenied = CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted
                                && !ShouldShowRequestPermissionRationale(global::Android.Manifest.Permission.RecordAudio);
        UpdateMicrophoneState();
    }

    /// <summary>Without the microphone the node still connects and listens; only transmit is turned off.</summary>
    /// <summary>
    /// Opens the Android share sheet with the log as plain text (e-mail body, note, chat...). Text rather than a
    /// file attachment keeps this free of a FileProvider; the size is capped by <see cref="AppServices.ShareTextLimit"/>.
    /// The full copy stays in the app's private storage.
    /// </summary>
    private System.Threading.Tasks.Task ShareLogText(string path, string text)
    {
        var send = new global::Android.Content.Intent(global::Android.Content.Intent.ActionSend);
        send.SetType("text/plain");
        send.PutExtra(global::Android.Content.Intent.ExtraSubject, $"NodeX log {DateTime.Now:yyyy-MM-dd HH:mm}");
        send.PutExtra(global::Android.Content.Intent.ExtraText, text);
        StartActivity(global::Android.Content.Intent.CreateChooser(send, "Send NodeX log"));
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void UpdateMicrophoneState() =>
        AppServices.Runtime.Transmit.MicrophoneAllowed =
            CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == Permission.Granted;

    private void RequestMicrophoneOrOpenSettings()
    {
        if (_micPermanentlyDenied)
        {
            var intent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.FromParts("package", PackageName, null));
            StartActivity(intent);
        }
        else
        {
            RequestPermissions([global::Android.Manifest.Permission.RecordAudio], PermissionRequestCode);
        }
    }

    /// <summary>
    /// A link that must survive for hours is killed by aggressive OEM battery managers unless the app is
    /// exempt from battery optimisation. Ask once, and only once a link is actually up (so the request has
    /// obvious context); Android shows its own system dialog.
    /// </summary>
    private void MaybeAskBatteryExemption()
    {
        if (!NodeForegroundService.IsRunning) return;

        var prefs = GetSharedPreferences("nodex_prefs", global::Android.Content.FileCreationMode.Private);
        if (prefs == null || prefs.GetBoolean("battery_exemption_asked", false)) return;

        using var keeper = new AndroidBackgroundKeeper(this);
        if (keeper.IsIgnoringBatteryOptimizations) return;

        prefs.Edit()?.PutBoolean("battery_exemption_asked", true)?.Apply();
        try { StartActivity(keeper.CreateBatteryOptimizationExemptionIntent()); }
        catch (global::Android.Content.ActivityNotFoundException) { /* no such settings screen on this device */ }
    }

    /// <summary>
    /// Microphone (to talk), notifications (the "NodeX connected" foreground-service notice) and
    /// Bluetooth (headsets and PTT buttons). Without the microphone the node can still listen.
    /// </summary>
    private void RequestRuntimePermissions()
    {
        var wanted = new List<string> { global::Android.Manifest.Permission.RecordAudio };
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            wanted.Add(global::Android.Manifest.Permission.PostNotifications);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            wanted.Add(global::Android.Manifest.Permission.BluetoothConnect);
            wanted.Add(global::Android.Manifest.Permission.BluetoothScan);   // finding a BLE PTT button
        }
        else
        {
            wanted.Add(global::Android.Manifest.Permission.AccessFineLocation);   // Android 11 and older need it to scan
        }

        var missing = wanted.FindAll(p => CheckSelfPermission(p) != Permission.Granted);
        if (missing.Count > 0)
        {
            RequestPermissions(missing.ToArray(), PermissionRequestCode);
        }
    }
}
