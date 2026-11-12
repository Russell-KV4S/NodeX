using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using NodeX.Core;
using NodeX.Core.Settings;

namespace NodeX.UI.Android;

/// <summary>
/// Foreground service that keeps the process alive while NodeX has work to do: a configured node stays
/// registered with AllStarLink (and follows IP changes) even when nothing is linked, and while linked it also
/// holds the CPU/Wi-Fi locks and Bluetooth routing and keeps the audio pipeline alive with the screen off.
/// It runs until the runtime says it is no longer needed, which is only after "Exit NodeX" (or the task is
/// swiped away), so pressing Back leaves it running.
/// <list type="bullet">
/// <item>Idle (registered, nothing linked): <c>specialUse</c> (API 34+) / <c>connectedDevice</c>, no wake lock.</item>
/// <item>Linked: <c>mediaPlayback | connectedDevice</c>, plus <c>microphone</c> when the mic is allowed.</item>
/// </list>
/// </summary>
[Service(
    Name = "com.kv4s.nodex.NodeForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone
                            | ForegroundService.TypeMediaPlayback
                            | ForegroundService.TypeConnectedDevice
                            | ForegroundService.TypeSpecialUse)]
[Property("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE",
    Value = "Keeps the user's AllStarLink radio node registered and reachable while the app is in the background")]
public sealed class NodeForegroundService : Service
{
    public const string ActionStart = "com.kv4s.nodex.action.START";
    public const string ActionStop = "com.kv4s.nodex.action.STOP";
    public const string ActionDisconnect = "com.kv4s.nodex.action.DISCONNECT";
    public const string ActionExit = "com.kv4s.nodex.action.EXIT";

    private const string ChannelId = "nodex_link";
    private const int NotificationId = 4569;

    private AndroidBackgroundKeeper? _keeper;
    private Action<BackgroundState>? _handler;
    private Action<NodeConfig>? _configHandler;
    private AndroidHeadsetPtt? _headset;
    private Action? _carHandler;
    private int? _types;          // the service types of the last successful start; null = not started
    private bool _micGranted;
    private bool _carMode;        // the audio route was last set up for a car
    private static NodeForegroundService? _current;

    public static bool IsRunning { get; private set; }

    /// <summary>
    /// Call when NodeX comes to the front on the phone. If Android refused the microphone while the app was in the
    /// background (a link dialled from the car screen), the service asks again now that it is allowed, so the car's
    /// Talk button and the wheel button start working.
    /// </summary>
    public static void RetryMicrophone()
    {
        var service = _current;
        if (service == null || !IsRunning || service._micGranted) return;
        if (!AppServices.Runtime.Background.IsLinked) return;
        service._types = null;
        service.Apply(AppServices.Runtime.Background);
    }

    /// <summary>Starts the service if it is not already running (call when <see cref="BackgroundState.Needed"/> turns true).</summary>
    public static void EnsureStarted(Context context)
    {
        if (IsRunning) return;
        try
        {
            context.StartForegroundService(new Intent(context, typeof(NodeForegroundService)).SetAction(ActionStart));
        }
        catch (Exception ex)
        {
            // Android refuses to start a foreground service from the background in some states; the next
            // time the app is opened it is started again.
            global::Android.Util.Log.Warn("NodeX", $"Could not start the background service: {ex.Message}");
        }
    }

    /// <summary>Stops the service and removes its notification.</summary>
    public static void Stop(Context context)
    {
        try
        {
            context.StartService(new Intent(context, typeof(NodeForegroundService)).SetAction(ActionStop));
        }
        catch (Exception)
        {
            context.StopService(new Intent(context, typeof(NodeForegroundService)));
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        // The service follows the runtime itself, so notification text, service type and locks can change while
        // the app is in the background without any new service start (which Android restricts there).
        _handler = state => Apply(state);
        AppServices.Runtime.BackgroundChanged += _handler;

        // Settings saved while linked (the headset-button option) take effect without a relink.
        _configHandler = _ => { if (IsRunning) Apply(AppServices.Runtime.Background); };
        AppServices.Runtime.ConfigService.Changed += _configHandler;

        // Android Auto connecting or disconnecting changes the audio route and who owns the media buttons.
        _carHandler = () => { if (IsRunning) Apply(AppServices.Runtime.Background); };
        CarSession.Changed += _carHandler;
        _current = this;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActionStop:
                Shutdown();
                return StartCommandResult.NotSticky;

            case ActionDisconnect:
                _ = AppServices.Runtime.DisconnectAsync();
                break;

            case ActionExit:
                AndroidAppExit.RunFull(this);
                break;
        }

        if (!Apply(AppServices.Runtime.Background)) return StartCommandResult.NotSticky;
        return StartCommandResult.Sticky;
    }

    /// <summary>Shows the right notification and holds the right resources for <paramref name="state"/>. False if the service stopped.</summary>
    private bool Apply(BackgroundState state)
    {
        if (!state.Needed)
        {
            Shutdown();
            return false;
        }

        EnsureChannel();
        var notification = BuildNotification(state);
        bool micAllowed = CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == Permission.Granted;

        // The microphone type is wanted in the car too (the car's Talk button, the wheel button and a Bluetooth PTT
        // button all need it); if Android refuses it the service falls back to listen-only below.
        bool wantMic = micAllowed;
        bool micGranted = false;
        bool car = CarSession.Active;

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var types = TypesFor(state, wantMic);
                if (IsRunning && _types == (int)types)
                {
                    // Nothing about the service changed, only the text. Do not start it again: from the background
                    // (in a car, screen off) Android 14+ refuses the microphone type on a new start, which would
                    // take the microphone away from a link that already had it.
                    (GetSystemService(NotificationService) as NotificationManager)?.Notify(NotificationId, notification);
                    micGranted = _micGranted;
                }
                else
                {
                    StartForeground(NotificationId, notification, types);
                    micGranted = wantMic && state.IsLinked;
                    _types = (int)types;
                }
            }
            else
            {
                StartForeground(NotificationId, notification);
                micGranted = micAllowed;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("NodeX", $"Foreground start refused: {ex.Message}");

            // From the car with the app in the background, Android 14+ may refuse the microphone type. Keep listening
            // without it and tell the car it cannot talk, rather than losing the link.
            bool retried = false;
            if (wantMic && state.IsLinked && OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                try
                {
                    var fallback = TypesFor(state, false);
                    StartForeground(NotificationId, notification, fallback);
                    // Remembered as "no microphone", so opening NodeX on the phone asks for it again (RetryMicrophone).
                    _types = (int)fallback;
                    retried = true;
                    NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car",
                        "Android refused the microphone for the background service; listening only until NodeX is opened on the phone");
                }
                catch (Exception ex2)
                {
                    global::Android.Util.Log.Warn("NodeX", $"Foreground start refused again: {ex2.Message}");
                }
            }

            if (!retried)
            {
                CarSession.MicrophoneUsable = false;
                Shutdown();
                return false;
            }
        }
        _micGranted = micGranted;
        CarSession.MicrophoneUsable = micGranted;

        _keeper ??= new AndroidBackgroundKeeper(this);
        if (state.IsLinked)
        {
            _keeper.Acquire();
            if (car)
            {
                // In a car the link plays as ordinary media: a car's hands-free link must not switch the phone into
                // call-style audio (the car then treats it as a phone call and its media buttons stop working).
                // This also covers a link that was started on the phone before the car connected.
                _keeper.StopBluetoothSco();
                if (!_carMode) AndroidAudioRoute.EnterCar(this);
            }
            else
            {
                if (_carMode) AndroidAudioRoute.LeaveCar(this);
                // No-op when no Bluetooth headset is connected. Apply runs again on every change (another node
                // linked, a settings save, a notification refresh), so it must not undo a loudspeaker choice:
                // starting SCO puts the phone back in communication mode and moves the audio to the headset.
                // Switching back to Phone picks the headset itself (AndroidAudioRoute.SetSpeaker).
                if (!AndroidAudioRoute.SpeakerActive) _keeper.StartBluetoothSco();
            }
            _carMode = car;
        }
        else
        {
            // Idle: registration is a small HTTPS request each minute; no reason to hold the CPU or Wi-Fi awake.
            _keeper.StopBluetoothSco();
            _keeper.Release();
            AndroidAudioRoute.Reset(this);   // loudspeaker choice only lasts while linked
            _carMode = false;
        }

        // A headset's Play/Pause button as push-to-talk, only while linked, only if the user turned it on, never in the car.
        _headset ??= new AndroidHeadsetPtt(this);
        _headset.Update(state.IsLinked && !CarSession.Active && AppServices.Runtime.Config.Settings.MediaKeyPtt);

        IsRunning = true;
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static ForegroundService TypesFor(BackgroundState state, bool includeMic)
    {
        if (!state.IsLinked)
        {
            return OperatingSystem.IsAndroidVersionAtLeast(34)
                ? ForegroundService.TypeSpecialUse
                : ForegroundService.TypeConnectedDevice;
        }

        var types = ForegroundService.TypeMediaPlayback | ForegroundService.TypeConnectedDevice;
        // A car session leaves the microphone type off unless "Talk from the car" is on, because Android 14+ refuses
        // it for a service started from the background (Apply then falls back to listen-only).
        if (includeMic) types |= ForegroundService.TypeMicrophone;
        return types;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        // Swiping the app out of Recents is a deliberate "close it": unkey, hang up, stop registering, end the
        // process. (Back only hides the app and does not come here.)
        AndroidAppExit.RunFull(this);
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        if (_handler != null) AppServices.Runtime.BackgroundChanged -= _handler;
        _handler = null;
        if (_configHandler != null) AppServices.Runtime.ConfigService.Changed -= _configHandler;
        _configHandler = null;
        if (_carHandler != null) CarSession.Changed -= _carHandler;
        _carHandler = null;
        if (_current == this) _current = null;
        Teardown();
        base.OnDestroy();
    }

    private void Shutdown()
    {
        Teardown();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private void Teardown()
    {
        _headset?.Dispose();
        _headset = null;
        _keeper?.Dispose();
        _keeper = null;
        _types = null;
        _micGranted = false;
        _carMode = false;
        IsRunning = false;
    }

    private void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (GetSystemService(NotificationService) is not NotificationManager nm) return;
        if (nm.GetNotificationChannel(ChannelId) != null) return;

        var channel = new NotificationChannel(ChannelId, "NodeX status", NotificationImportance.Low)
        {
            Description = "Shown while NodeX is running: registered and ready, or linked to a node."
        };
        channel.SetShowBadge(false);
        nm.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(BackgroundState state)
    {
        var open = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        var pending = PendingIntent.GetActivity(this, 0, open,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(state.IsLinked ? "NodeX connected" : "NodeX is running")!
            .SetContentText(state.Summary)!
            .SetSmallIcon(Resource.Drawable.ic_stat_nodex)!
            .SetOngoing(true)!
            .SetCategory(Notification.CategoryService)!
            .SetContentIntent(pending)!;

        if (state.IsLinked)
        {
            builder.AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Disconnect",
                ServiceAction(1, ActionDisconnect));
        }

        builder.AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Exit NodeX",
            ServiceAction(2, ActionExit));

        return builder.Build()!;
    }

    private PendingIntent? ServiceAction(int requestCode, string action) =>
        PendingIntent.GetService(this, requestCode,
            new Intent(this, typeof(NodeForegroundService)).SetAction(action),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
}
