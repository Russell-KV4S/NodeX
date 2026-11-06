using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace NodeX.UI.Android;

/// <summary>
/// Foreground service that keeps the process, UDP socket and audio pipeline alive while the
/// display is off. Declared with microphone | mediaPlayback | connectedDevice service types.
/// </summary>
[Service(
    Name = "com.CompanyName.NodeX.UI.NodeForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone
                            | ForegroundService.TypeMediaPlayback
                            | ForegroundService.TypeConnectedDevice)]
public sealed class NodeForegroundService : Service
{
    public const string ActionStart = "com.CompanyName.NodeX.UI.action.START";
    public const string ActionStop = "com.CompanyName.NodeX.UI.action.STOP";
    public const string ExtraNode = "node";

    private const string ChannelId = "nodex_link";
    private const int NotificationId = 4569;

    private AndroidBackgroundKeeper? _keeper;

    public static bool IsRunning { get; private set; }

    /// <summary>Starts the service (call when a link is being connected).</summary>
    public static void Start(Context context, string? targetNode)
    {
        var intent = new Intent(context, typeof(NodeForegroundService)).SetAction(ActionStart);
        if (targetNode != null) intent.PutExtra(ExtraNode, targetNode);
        context.StartForegroundService(intent);
    }

    /// <summary>Stops the service (call after disconnecting).</summary>
    public static void Stop(Context context)
    {
        context.StartService(new Intent(context, typeof(NodeForegroundService)).SetAction(ActionStop));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            Teardown();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        string? node = intent?.GetStringExtra(ExtraNode);
        EnsureChannel();
        var notification = BuildNotification(node);

        // The service-type overload is required on API 29+ (and enforced on 34+).
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            StartForeground(NotificationId, notification,
                ForegroundService.TypeMicrophone
                | ForegroundService.TypeMediaPlayback
                | ForegroundService.TypeConnectedDevice);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        _keeper ??= new AndroidBackgroundKeeper(this);
        _keeper.Acquire();
        _keeper.StartBluetoothSco();   // no-op when no Bluetooth headset is connected
        IsRunning = true;

        return StartCommandResult.Sticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        // Swiping the app away must not leave a keyed transmitter or held locks behind.
        Teardown();
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        Teardown();
        base.OnDestroy();
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

        var channel = new NotificationChannel(ChannelId, "NodeX link", NotificationImportance.Low)
        {
            Description = "Shown while NodeX is connected to an AllStarLink node."
        };
        channel.SetShowBadge(false);
        nm.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string? node)
    {
        var open = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        var pending = PendingIntent.GetActivity(this, 0, open,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var stop = PendingIntent.GetService(this, 1,
            new Intent(this, typeof(NodeForegroundService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("NodeX connected")!
            .SetContentText(string.IsNullOrEmpty(node) ? "Radio link active" : $"Linked to node {node}")!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetOngoing(true)!
            .SetCategory(Notification.CategoryService)!
            .SetContentIntent(pending)!
            .AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Disconnect", stop)!
            .Build()!;
    }
}
