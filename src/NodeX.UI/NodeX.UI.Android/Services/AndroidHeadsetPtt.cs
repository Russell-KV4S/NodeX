using System;
using System.Threading.Tasks;
using Android.Content;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Views;
using NodeX.Core;

namespace NodeX.UI.Android;

/// <summary>
/// Lets a Bluetooth headset's Play/Pause button act as push-to-talk while linked (press to talk, press again to stop),
/// including with the screen off. Android sends headset buttons to the most recently active media session, so while the
/// option is on and a link is up this holds an active (playing) session whose callback handles them. The rules and
/// safety checks are in <see cref="MediaKeyTransmit"/>; this class only turns key events into calls and gives audible
/// feedback (a headset user can't see the screen). The key is released if the headset is unplugged or the link ends.
/// </summary>
public sealed class AndroidHeadsetPtt : IDisposable
{
    private readonly Context _context;
    private readonly object _gate = new();
    private MediaSession? _session;
    private NoisyReceiver? _noisy;
    private ToneGenerator? _tone;

    public AndroidHeadsetPtt(Context context)
    {
        _context = context.ApplicationContext ?? context;
    }

    public bool IsActive => _session != null;

    /// <summary>Starts or stops listening for the headset button. Idempotent.</summary>
    public void Update(bool wanted)
    {
        lock (_gate)
        {
            if (wanted && _session == null) Start();
            else if (!wanted && _session != null) Stop();
        }
    }

    private void Start()
    {
        var session = new MediaSession(_context, "NodeX headset PTT");
        session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
        session.SetCallback(new Callback(this));
        session.SetPlaybackState(new PlaybackState.Builder()
            .SetActions(PlaybackState.ActionPlayPause | PlaybackState.ActionPlay | PlaybackState.ActionPause)!
            .SetState(PlaybackStateCode.Playing, 0, 1f)!
            .Build()!);
        session.Active = true;
        _session = session;

        _noisy = new NoisyReceiver();
        var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            _context.RegisterReceiver(_noisy, filter, ReceiverFlags.NotExported);
        else
            _context.RegisterReceiver(_noisy, filter);
    }

    private void Stop()
    {
        // The headset button must never leave the transmitter on once it stops listening.
        ReleaseKey();

        if (_noisy != null)
        {
            try { _context.UnregisterReceiver(_noisy); } catch (Exception) { /* not registered */ }
            _noisy = null;
        }
        if (_session != null)
        {
            _session.Active = false;
            _session.Release();
            _session = null;
        }
        _tone?.Release();
        _tone = null;
    }

    private static void ReleaseKey()
    {
        try { Task.Run(() => MediaKeyTransmit.UnkeyAsync(AppServices.Runtime)).Wait(TimeSpan.FromSeconds(1)); }
        catch (Exception) { /* nothing more to do */ }
    }

    private async Task PressAsync()
    {
        MediaKeyDecision result;
        try { result = await MediaKeyTransmit.ToggleAsync(AppServices.Runtime); }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("NodeX", $"Headset PTT failed: {ex.Message}");
            Beep(Tone.PropNack, 300);
            return;
        }

        switch (result.Action)
        {
            case MediaKeyAction.Key: Beep(Tone.PropBeep, 150); break;
            case MediaKeyAction.Unkey: Beep(Tone.PropAck, 250); break;
            case MediaKeyAction.Refuse:
                NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("headset", $"talk refused: {result.Message}");
                Beep(Tone.PropNack, 300);
                break;
        }
    }

    private void Beep(Tone tone, int ms)
    {
        try
        {
            _tone ??= new ToneGenerator(global::Android.Media.Stream.Music, 70);
            _tone.StartTone(tone, ms);
        }
        catch (Exception) { /* the on-screen state still shows it */ }
    }

    private static bool IsPttKey(Keycode code) =>
        code is Keycode.Headsethook or Keycode.MediaPlayPause or Keycode.MediaPlay or Keycode.MediaPause;

    private sealed class Callback(AndroidHeadsetPtt owner) : MediaSession.Callback
    {
        public override bool OnMediaButtonEvent(Intent mediaButtonIntent)
        {
            KeyEvent? e;
#pragma warning disable CA1422 // the typed overload exists only from API 33
            e = mediaButtonIntent.GetParcelableExtra(Intent.ExtraKeyEvent) as KeyEvent;
#pragma warning restore CA1422
            if (e == null || !IsPttKey(e.KeyCode)) return base.OnMediaButtonEvent(mediaButtonIntent);

            // A car session owns the media buttons while it is up (its own Talk rules apply there).
            if (CarSession.Active) return base.OnMediaButtonEvent(mediaButtonIntent);

            if (e.Action == KeyEventActions.Down && e.RepeatCount == 0) _ = owner.PressAsync();
            return true;   // the matching key-up is swallowed too
        }
    }

    private sealed class NoisyReceiver : BroadcastReceiver
    {
        // Headset unplugged or disconnected: the phone microphone must not stay on air with nobody to hear the beeps.
        public override void OnReceive(Context? context, Intent? intent) =>
            _ = Task.Run(() => MediaKeyTransmit.UnkeyAsync(AppServices.Runtime));
    }

    public void Dispose()
    {
        lock (_gate) { if (_session != null) Stop(); }
    }
}
