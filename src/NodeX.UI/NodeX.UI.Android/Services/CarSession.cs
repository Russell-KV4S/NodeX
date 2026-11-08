namespace NodeX.UI.Android;

/// <summary>
/// True while an Android Auto session owns the audio. Read when an audio track is created and when the foreground
/// service picks its type, so car listening uses media audio (no call mode, no Bluetooth SCO). The microphone service
/// type is only requested when "Talk from the car" is on. It never affects phone or Bluetooth-headset use outside a
/// car session.
/// </summary>
public static class CarSession
{
    private static volatile bool _active;
    private static volatile bool _microphoneUsable;

    public static bool Active
    {
        get => _active;
        set => _active = value;
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
