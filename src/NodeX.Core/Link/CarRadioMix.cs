namespace NodeX.Core;

/// <summary>Which Android audio stream node audio plays on.</summary>
public enum PlaybackUsage { VoiceCommunication, Media, NavigationGuidance }

/// <summary>
/// "Hear nodes over the car radio" (owner request 2026-11-20, setting <c>CarRadioMix</c>). A car's head unit plays one
/// media source at a time, so while the radio (or another app) is the car's source NodeX's media audio is not heard.
/// Navigation prompts are mixed over the radio, which the car lowers while they play. So, while the car is playing
/// something else, node audio goes on the navigation stream and NodeX holds navigation-style ("may duck") audio focus
/// only while an unmuted node is heard, letting go <see cref="ReleaseDelay"/> after it stops so short pauses between
/// words do not pump the radio. When NodeX is the car's chosen source it plays as ordinary media, as before.
/// Pure rules here; the Android head applies them. Not tried in a car.
/// </summary>
public static class CarRadioMix
{
    /// <summary>How long after the last heard audio the radio is let back up.</summary>
    public static readonly TimeSpan ReleaseDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>True when node audio should be mixed over whatever the car is playing.</summary>
    public static bool Applies(bool enabled, bool carActive, bool nodeXHasCarAudio) =>
        enabled && carActive && !nodeXHasCarAudio;

    /// <summary>
    /// The stream for node audio. Car or loudspeaker: media (never call mode in a car). Earpiece or headset: the call
    /// stream. Car with radio mixing in effect: navigation guidance.
    /// </summary>
    public static PlaybackUsage Choose(bool carActive, bool speakerActive, bool mixApplies) =>
        mixApplies ? PlaybackUsage.NavigationGuidance
        : carActive || speakerActive ? PlaybackUsage.Media
        : PlaybackUsage.VoiceCommunication;
}

/// <summary>
/// Decides when to hold the "duck the radio" audio focus: from the first heard audio until
/// <see cref="CarRadioMix.ReleaseDelay"/> after the last. Call <see cref="Update"/> on a short timer.
/// </summary>
public sealed class RadioDuckGate
{
    private long _lastHeard = long.MinValue;

    /// <summary>Whether the focus is wanted now.</summary>
    public bool Holding { get; private set; }

    /// <summary>Feeds the current state; returns true when <see cref="Holding"/> changed.</summary>
    public bool Update(bool applies, bool anyHeard, long nowMs)
    {
        bool want;
        if (!applies) { want = false; _lastHeard = long.MinValue; }
        else if (anyHeard) { want = true; _lastHeard = nowMs; }
        else want = _lastHeard != long.MinValue && nowMs - _lastHeard < CarRadioMix.ReleaseDelay.TotalMilliseconds;

        if (want == Holding) return false;
        Holding = want;
        return true;
    }
}
