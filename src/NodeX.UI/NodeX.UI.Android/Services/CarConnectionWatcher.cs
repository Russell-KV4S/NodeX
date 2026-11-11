using System;
using System.Threading.Tasks;
using Android.Content;

namespace NodeX.UI.Android;

/// <summary>
/// Follows whether Android Auto is connected to a car screen and keeps <see cref="CarSession.Connected"/> current.
/// It asks Android Auto the same way the AndroidX <c>CarConnection</c> class does (a content-provider query plus a
/// broadcast when it changes), without needing the Car App Library. If Android Auto is not installed or does not
/// answer, the phone counts as not in a car.
/// </summary>
public sealed class CarConnectionWatcher : BroadcastReceiver
{
    private const string UpdatedAction = "androidx.car.app.connection.action.CAR_CONNECTION_UPDATED";
    private const string StateColumn = "CarConnectionState";
    private const int Projection = 2;   // 0 = not connected, 1 = built-in (Automotive OS), 2 = Android Auto
    private static readonly global::Android.Net.Uri Provider =
        global::Android.Net.Uri.Parse("content://androidx.car.app.connection")!;

    private static CarConnectionWatcher? _instance;
    private readonly Context _context;

    private CarConnectionWatcher(Context context) => _context = context;

    /// <summary>Starts watching for the life of the process. Idempotent.</summary>
    public static void Start(Context context)
    {
        if (_instance != null) return;
        var app = context.ApplicationContext ?? context;
        var watcher = new CarConnectionWatcher(app);
        _instance = watcher;
        try
        {
            var filter = new IntentFilter(UpdatedAction);
            // The broadcast comes from Android Auto, another app, so the receiver has to be exported.
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                app.RegisterReceiver(watcher, filter, ReceiverFlags.Exported);
            else
                app.RegisterReceiver(watcher, filter);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("NodeX", $"Car connection watch failed: {ex.Message}");
        }
        watcher.Refresh();
    }

    public override void OnReceive(Context? context, Intent? intent) => Refresh();

    private void Refresh() => _ = Task.Run(() =>
    {
        bool connected = Query();
        if (connected != CarSession.Connected)
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", connected ? "Android Auto connected" : "Android Auto disconnected");
        CarSession.Connected = connected;
    });

    private bool Query()
    {
        try
        {
            using var cursor = _context.ContentResolver?.Query(Provider, [StateColumn], null, null, null);
            if (cursor == null) return false;
            int column = cursor.GetColumnIndex(StateColumn);
            if (column < 0 || !cursor.MoveToNext()) return false;
            return cursor.GetInt(column) == Projection;
        }
        catch (Exception)
        {
            return false;   // Android Auto missing or not answering
        }
    }
}
