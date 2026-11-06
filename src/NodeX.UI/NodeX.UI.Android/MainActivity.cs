using System;
﻿using System.Collections.Generic;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace NodeX.UI.Android;

[Activity(
    Label = "NodeX",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int PermissionRequestCode = 1001;

    private bool _micPermanentlyDenied;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppServices.MicrophonePermissionRequester = RequestMicrophoneOrOpenSettings;
        RequestRuntimePermissions();
        UpdateMicrophoneState();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateMicrophoneState();   // the user may have granted it in system settings meanwhile
        MaybeAskBatteryExemption();
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
            wanted.Add(global::Android.Manifest.Permission.BluetoothConnect);

        var missing = wanted.FindAll(p => CheckSelfPermission(p) != Permission.Granted);
        if (missing.Count > 0)
        {
            RequestPermissions(missing.ToArray(), PermissionRequestCode);
        }
    }
}
