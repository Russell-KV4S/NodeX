namespace NodeX.Core;

[Flags]
public enum BackgroundServiceKinds { None = 0, ConnectedDevice = 1, MediaPlayback = 2, Microphone = 4, SpecialUse = 8 }

/// <summary>Foreground-service and routing decisions used by the Android service, independent of Android bindings.</summary>
public sealed record AndroidServicePlan(BackgroundServiceKinds Kinds, bool HoldLocks, bool CarAudio, bool BluetoothSco, bool HeadsetKeys)
{
    public static AndroidServicePlan Create(BackgroundState state, int apiLevel, bool microphoneAllowed,
        bool carActive, bool speakerActive, bool headsetKeysEnabled)
    {
        if (!state.Needed) return new(BackgroundServiceKinds.None, false, false, false, false);
        bool linked = state.IsLinked;
        var kinds = linked
            ? BackgroundServiceKinds.MediaPlayback | BackgroundServiceKinds.ConnectedDevice |
              (microphoneAllowed ? BackgroundServiceKinds.Microphone : BackgroundServiceKinds.None)
            : apiLevel >= 34 ? BackgroundServiceKinds.SpecialUse : BackgroundServiceKinds.ConnectedDevice;
        return new(kinds, linked, linked && carActive, linked && !carActive && !speakerActive,
            linked && !carActive && headsetKeysEnabled);
    }

    // Re-starting an unchanged microphone FGS from the background can lose microphone access on Android 14+.
    public static bool NeedsForegroundStart(bool running, int? previousKinds, int requestedKinds) =>
        !running || previousKinds != requestedKinds;
}

public enum CarMediaButton { Play, Pause, Stop, Next, Previous }
public enum CarMediaAction { Ignore, Unkey, Mute, Unmute, Talk, Next, Previous }

/// <summary>Used directly by MediaSession callbacks. Pause/Stop mute and Play unmutes (owner decision 2026-11-12: a media command must never hang a node up); no media command can auto-dial a favorite.</summary>
public static class CarMediaButtons
{
    public static CarMediaAction Decide(CarMediaButton button, bool anythingKeyed, bool nextTalks, bool previousTalks)
    {
        if (anythingKeyed) return CarMediaAction.Unkey;
        return button switch
        {
            CarMediaButton.Play => CarMediaAction.Unmute,
            CarMediaButton.Pause or CarMediaButton.Stop => CarMediaAction.Mute,
            CarMediaButton.Next => nextTalks ? CarMediaAction.Talk : CarMediaAction.Next,
            CarMediaButton.Previous => previousTalks ? CarMediaAction.Talk : CarMediaAction.Previous,
            _ => CarMediaAction.Ignore
        };
    }
}
