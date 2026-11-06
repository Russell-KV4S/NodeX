using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using NodeX.UI;

namespace NodeX.UI.Android;

/// <summary>
/// Really quits NodeX on Android: hangs up and stops registering (<see cref="NodeX.Core.NodeRuntime.ExitAsync"/>),
/// stops the background service, removes the task from Recents and ends the process. Android never does this on
/// its own (Back only hides the app), which is why the app has an explicit "Exit NodeX".
/// </summary>
internal static class AndroidAppExit
{
    private static int _running;

    /// <summary>Stops everything, then ends the process. Safe to call more than once.</summary>
    public static void RunFull(Context context)
    {
        if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1) return;
        var app = context.ApplicationContext ?? context;

        _ = Task.Run(async () =>
        {
            try
            {
                await AppServices.Runtime.ExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Leaving anyway.
            }
            Finish(app);
        });
    }

    /// <summary>Ends the task and the process; call after the runtime has been stopped.</summary>
    public static void Finish(Context context)
    {
        try { NodeForegroundService.Stop(context); } catch (Exception) { }

        if (MainActivity.Current is { } activity)
        {
            activity.RunOnUiThread(() => activity.FinishAndRemoveTask());
        }

        // Give the activity a moment to close, then end the process so nothing lingers in the background.
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        });
    }
}
