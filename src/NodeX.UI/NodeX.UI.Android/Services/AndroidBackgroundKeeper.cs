using System;
using Android.Content;
using Android.Media;
using Android.Net.Wifi;
using Android.OS;
using Android.Provider;

namespace NodeX.UI.Android;

/// <summary>
/// Holds the OS-level "do not sleep" resources the node needs while connected:
/// a partial CPU wake lock, a low-latency Wi-Fi lock, and Bluetooth SCO headset routing.
/// </summary>
public sealed class AndroidBackgroundKeeper : IDisposable
{
    private const string LockTag = "NodeX:Link";

    private readonly Context _context;
    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.WifiLock? _wifiLock;
    private bool _scoActive;

    public AndroidBackgroundKeeper(Context context)
    {
        _context = context.ApplicationContext ?? context;
    }

    /// <summary>Acquires the partial wake lock and low-latency Wi-Fi lock. Idempotent.</summary>
    public void Acquire()
    {
        if (_wakeLock == null && _context.GetSystemService(Context.PowerService) is PowerManager pm)
        {
            _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, LockTag);
            _wakeLock?.SetReferenceCounted(false);
        }
        if (_wakeLock is { IsHeld: false }) _wakeLock.Acquire();

        if (_wifiLock == null && _context.GetSystemService(Context.WifiService) is WifiManager wm)
        {
            // FullLowLatency exists from API 29; older devices fall back to FullHighPerf.
            var mode = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? global::Android.Net.WifiMode.FullLowLatency
#pragma warning disable CA1422 // validate platform compatibility (FullHighPerf obsolete on 29+, used only below 29)
                : global::Android.Net.WifiMode.FullHighPerf;
#pragma warning restore CA1422
            _wifiLock = wm.CreateWifiLock(mode, LockTag);
            _wifiLock?.SetReferenceCounted(false);
        }
        if (_wifiLock is { IsHeld: false }) _wifiLock.Acquire();
    }

    /// <summary>Releases the wake and Wi-Fi locks.</summary>
    public void Release()
    {
        if (_wakeLock is { IsHeld: true }) _wakeLock.Release();
        if (_wifiLock is { IsHeld: true }) _wifiLock.Release();
    }

    /// <summary>Routes call audio to a connected Bluetooth SCO headset, if one is present.</summary>
    /// <returns>True when a SCO device was selected or SCO was started.</returns>
    public bool StartBluetoothSco()
    {
        if (_context.GetSystemService(Context.AudioService) is not AudioManager am) return false;

        // Only switch into communication mode if a Bluetooth headset is actually available; otherwise
        // leave normal audio routing (loudspeaker) alone.
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            // API 31+: pick the communication device explicitly (StartBluetoothSco is deprecated).
            foreach (var device in am.AvailableCommunicationDevices)
            {
                if (device.Type == AudioDeviceType.BluetoothSco)
                {
                    am.Mode = Mode.InCommunication;
                    _scoActive = am.SetCommunicationDevice(device);
                    return _scoActive;
                }
            }
            return false;
        }

#pragma warning disable CA1422 // legacy SCO API for API < 31
        if (!am.IsBluetoothScoAvailableOffCall) return false;
        am.Mode = Mode.InCommunication;
        am.StartBluetoothSco();
        am.BluetoothScoOn = true;
#pragma warning restore CA1422
        _scoActive = true;
        return true;
    }

    /// <summary>Stops SCO routing and restores normal audio mode.</summary>
    public void StopBluetoothSco()
    {
        if (_context.GetSystemService(Context.AudioService) is not AudioManager am) return;

        if (_scoActive)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                am.ClearCommunicationDevice();
            }
            else
            {
#pragma warning disable CA1422
                am.BluetoothScoOn = false;
                am.StopBluetoothSco();
#pragma warning restore CA1422
            }
            am.Mode = Mode.Normal;
            _scoActive = false;
        }
    }

    /// <summary>True when the app is already exempt from battery optimization.</summary>
    public bool IsIgnoringBatteryOptimizations =>
        _context.GetSystemService(Context.PowerService) is PowerManager pm
        && pm.IsIgnoringBatteryOptimizations(_context.PackageName);

    /// <summary>
    /// Builds the system prompt intent asking the user to exempt the app from battery optimization
    /// (requires the REQUEST_IGNORE_BATTERY_OPTIMIZATIONS permission).
    /// </summary>
    public Intent CreateBatteryOptimizationExemptionIntent()
    {
        var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
        intent.SetData(global::Android.Net.Uri.Parse($"package:{_context.PackageName}"));
        return intent;
    }

    public void Dispose()
    {
        StopBluetoothSco();
        Release();
        _wakeLock?.Dispose();
        _wifiLock?.Dispose();
        _wakeLock = null;
        _wifiLock = null;
    }
}
