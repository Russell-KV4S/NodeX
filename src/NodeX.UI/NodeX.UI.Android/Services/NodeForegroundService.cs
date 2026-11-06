using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using NodeX.Core;

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

    public static bool IsRunning { get; private set; }

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

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                StartForeground(NotificationId, notification, TypesFor(state, micAllowed));
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("NodeX", $"Foreground start refused: {ex.Message}");
            Shutdown();
            return false;
        }

        _keeper ??= new AndroidBackgroundKeeper(this);
        if (state.IsLinked)
        {
            _keeper.Acquire();
            _keeper.StartBluetoothSco();   // no-op when no Bluetooth headset is connected
        }
        else
        {
            // Idle: registration is a small HTTPS request each minute; no reason to hold the CPU or Wi-Fi awake.
            _keeper.StopBluetoothSco();
            _keeper.Release();
            AndroidAudioRoute.Reset(this);   // loudspeaker choice only lasts while linked
        }

        IsRunning = true;
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static ForegroundService TypesFor(BackgroundState state, bool micAllowed)
    {
        if (!state.IsLinked)
        {
            return OperatingSystem.IsAndroidVersionAtLeast(34)
                ? ForegroundService.TypeSpecialUse
                : ForegroundService.TypeConnectedDevice;
        }

        var types = ForegroundService.TypeMediaPlayback | ForegroundService.TypeConnectedDevice;
        if (micAllowed) types |= ForegroundService.TypeMicrophone;
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
        _keeper?.Dispose();
        _keeper = null;
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
