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

    /// <summary>Raised (on any thread) when Android Auto goes from connected to disconnected.</summary>
    public static event Action? Disconnected;

    /// <summary>
    /// Android Auto is connected to a car screen. Going from connected to disconnected also ends the car selection:
    /// otherwise <see cref="Active"/> stayed true (Selected kept it up), <see cref="Changed"/> never fired and car mode,
    /// car push-to-talk and car-radio mixing outlived the car (2026-11-21 review).
    /// </summary>
    public static bool Connected
    {
        get { lock (Gate) return _connected; }
        set
        {
            bool unplugged;
            bool changed;
            lock (Gate)
            {
                bool before = _connected || _selected;
                unplugged = _connected && !value;
                _connected = value;
                if (unplugged) _selected = false;
                changed = before != (_connected || _selected);
            }
            if (unplugged) Raise(Disconnected, "disconnect");
            if (changed) Raise(Changed, "change");
        }
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
        if (changed) Raise(Changed, "change");
    }

    private static void Raise(Action? handler, string what)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { global::Android.Util.Log.Warn("NodeX", $"Car session {what} failed: {ex.Message}"); }
    }

    private static volatile bool _hasCarAudio;

    /// <summary>Raised (on any thread) when <see cref="HasCarAudio"/> changes.</summary>
    public static event Action? CarAudioChanged;

    /// <summary>
    /// NodeX holds the car's media audio (it is the car's chosen source). False once the car's radio or another app
    /// took the audio for good; "hear nodes over the radio" (<c>CarRadioMix</c>) only applies then.
    /// </summary>
    public static bool HasCarAudio
    {
        get => _hasCarAudio;
        set
        {
            if (_hasCarAudio == value) return;
            _hasCarAudio = value;
            try { CarAudioChanged?.Invoke(); }
            catch (Exception ex) { global::Android.Util.Log.Warn("NodeX", $"Car audio change failed: {ex.Message}"); }
        }
    }

    private static volatile bool _radioMixEnabled;

    /// <summary>The "hear nodes over the car radio" option, copied from settings by the foreground service.</summary>
    public static bool RadioMixEnabled
    {
        get => _radioMixEnabled;
        set
        {
            if (_radioMixEnabled == value) return;
            _radioMixEnabled = value;
            try { CarAudioChanged?.Invoke(); }
            catch (Exception ex) { global::Android.Util.Log.Warn("NodeX", $"Radio mix change failed: {ex.Message}"); }
        }
    }

    /// <summary>True when node audio should be mixed over what the car is playing (<see cref="NodeX.Core.CarRadioMix"/>).</summary>
    public static bool RadioMixApplies => NodeX.Core.CarRadioMix.Applies(RadioMixEnabled, Active, HasCarAudio);

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
