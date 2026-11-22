using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;

namespace NodeX.UI.Android;

/// <summary>
/// Keeps an idle node registered while the phone sleeps. Idle (no link), the foreground service holds no wake lock,
/// so the CPU suspends and the registration timer simply stops: a field report (2026-11-19) showed AllStarLink
/// dropping the node after about 20 minutes with no refresh, and far nodes then answering "node cannot be found".
/// This alarm wakes the app every <see cref="Interval"/> (in Doze, Android stretches that to its own minimum of
/// about 9 minutes) and re-registers under a short wake lock. While linked, the service's wake lock keeps the normal
/// timer running and the alarm is cancelled.
/// </summary>
[BroadcastReceiver(Name = "com.kv4s.nodex.RegistrationAlarm", Exported = false)]
public sealed class RegistrationAlarm : BroadcastReceiver
{
    /// <summary>Wake-up interval while idle: inside the registry's refresh window with room for one miss.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private const int RequestCode = 4710;
    private const string LockTag = "NodeX:Register";

    /// <summary>When the pending wake-up is due (elapsed-realtime ms), or 0 when none is pending.</summary>
    private static long _dueAt;

    /// <summary>
    /// Schedules the next wake-up unless one is already pending. The service re-applies its state often (every
    /// notification refresh); re-arming each time would keep pushing the wake-up back and it might never fire.
    /// </summary>
    public static void Schedule(Context context)
    {
        try
        {
            long now = SystemClock.ElapsedRealtime();
            long due = System.Threading.Interlocked.Read(ref _dueAt);
            if (due != 0 && due > now - 60_000) return;     // pending (or only just overdue: Android batches alarms)

            var app = context.ApplicationContext ?? context;
            if (app.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
            long at = now + (long)Interval.TotalMilliseconds;
            System.Threading.Interlocked.Exchange(ref _dueAt, at);
            var pending = Pending(app);
            // Inexact and allowed while idle: needs no exact-alarm permission and lets Android batch it.
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
                alarms.SetAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup, at, pending);
            else
                alarms.Set(AlarmType.ElapsedRealtimeWakeup, at, pending);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("NodeX", $"Registration alarm not scheduled: {ex.Message}");
        }
    }

    /// <summary>Cancels a pending wake-up.</summary>
    public static void Cancel(Context context)
    {
        System.Threading.Interlocked.Exchange(ref _dueAt, 0);
        try
        {
            var app = context.ApplicationContext ?? context;
            if (app.GetSystemService(Context.AlarmService) is AlarmManager alarms) alarms.Cancel(Pending(app));
        }
        catch (Exception) { /* nothing scheduled */ }
    }

    private static PendingIntent Pending(Context app)
    {
        var intent = new Intent(app, typeof(RegistrationAlarm));
        var flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;
        return PendingIntent.GetBroadcast(app, RequestCode, intent, flags)!;
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null) return;
        System.Threading.Interlocked.Exchange(ref _dueAt, 0);     // this one has fired; the next Schedule arms a new one
        var app = context.ApplicationContext ?? context;
        var result = GoAsync();      // keeps the process awake for the receiver beyond OnReceive

        PowerManager.WakeLock? wake = null;
        try
        {
            if (app.GetSystemService(Context.PowerService) is PowerManager pm)
            {
                wake = pm.NewWakeLock(WakeLockFlags.Partial, LockTag);
                wake?.Acquire(20_000);   // released below; the timeout is a safety net
            }
        }
        catch (Exception) { wake = null; }

        _ = Task.Run(async () =>
        {
            try
            {
                var runtime = AppServices.Runtime;
                // After Android killed the process, this alarm can be the first thing to run: nothing has loaded the
                // settings, so the runtime looked idle and the alarm chain ended (2026-11-21 review). Idempotent.
                await runtime.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var state = runtime.Background;
                if (state.Needed && !state.IsLinked && !runtime.HasExited)
                {
                    await runtime.RenewRegistrationAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Schedule(app);
                }
            }
            catch (Exception ex)
            {
                NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("reg", $"idle wake-up re-registration failed: {ex.Message}");
                try { Schedule(app); } catch { }
            }
            finally
            {
                try { if (wake is { IsHeld: true }) wake.Release(); } catch { }
                result?.Finish();
            }
        });
    }
}
