namespace NodeX.UI.Android;

/// <summary>
/// True while a listen-only Android Auto session owns the audio. Read when an audio track is created and when the
/// foreground service picks its type, so car listening uses media audio (no call mode, no Bluetooth SCO, no microphone
/// service type). It never affects phone or Bluetooth-headset use outside a car session.
/// </summary>
public static class CarSession
{
    private static volatile bool _active;

    public static bool Active
    {
        get => _active;
        set => _active = value;
    }
}
