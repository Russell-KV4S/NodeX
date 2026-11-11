using System;

namespace NodeX.UI.Android;

/// <summary>
/// True while the phone is in a car: Android Auto is connected (<see cref="Connected"/>, from
/// <see cref="CarConnectionWatcher"/>) or a node was picked on the car screen (<see cref="Selected"/>). Read when an
/// audio track is created and when the foreground service applies its state, so car listening uses media audio (no
/// call mode, no Bluetooth SCO) and the wheel's media buttons reach the car's media session, also for a link that
/// was started on the phone before the car connected. It never affects phone or Bluetooth-headset use outside a car.
/// </summary>
public static class CarSession
{
    private static readonly object Gate = new();
    private static bool _connected;
    private static bool _selected;
    private static volatile bool _microphoneUsable;

    /// <summary>Raised (on any thread) when <see cref="Active"/> changes.</summary>
    public static event Action? Changed;

    public static bool Active
    {
        get { lock (Gate) return _connected || _selected; }
    }

    /// <summary>Android Auto is connected to a car screen.</summary>
    public static bool Connected
    {
        get { lock (Gate) return _connected; }
        set => Set(ref _connected, value);
    }

    /// <summary>The car's media session is playing a node.</summary>
    public static bool Selected
    {
        get { lock (Gate) return _selected; }
        set => Set(ref _selected, value);
    }

    private static void Set(ref bool field, bool value)
    {
        bool changed;
        lock (Gate)
        {
            bool before = _connected || _selected;
            field = value;
            changed = before != (_connected || _selected);
        }
        if (!changed) return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { global::Android.Util.Log.Warn("NodeX", $"Car session change failed: {ex.Message}"); }
    }

    /// <summary>
    /// True when the foreground service really holds the microphone type, so keying can capture audio. Set by
    /// <see cref="NodeForegroundService"/>; the car's Talk button refuses while it is false.
    /// </summary>
    public static bool MicrophoneUsable
    {
        get => _microphoneUsable;
        set => _microphoneUsable = value;
    }
}
