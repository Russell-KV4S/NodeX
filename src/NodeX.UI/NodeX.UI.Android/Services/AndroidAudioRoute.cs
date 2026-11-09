using System;
using Android.Content;
using Android.Media;

namespace NodeX.UI.Android;

/// <summary>
/// Switches call audio between the phone earpiece (or a connected Bluetooth headset) and the loudspeaker.
/// NodeX plays through a voice-communication stream, which Android sends to the earpiece by default.
/// </summary>
public static class AndroidAudioRoute
{
    /// <summary>
    /// True while the loudspeaker is selected. In that state NodeX plays as ordinary media and never
    /// puts the phone into communication mode, so other apps' notifications keep their own sounds
    /// (in communication mode Android swaps them for a generic in-call beep).
    /// </summary>
    public static volatile bool SpeakerActive;

    /// <summary>Raised after the speaker/phone choice changes so playback can be rebuilt with the right stream.</summary>
    public static event Action? RouteChanged;

    /// <summary>True = loudspeaker; false = Bluetooth headset if one is connected, else the earpiece.</summary>
    public static bool SetSpeaker(Context context, bool speaker)
    {
        if (context.GetSystemService(Context.AudioService) is not AudioManager am) return false;

        if (speaker)
        {
            // Plain media playback to the loudspeaker: no communication mode, no forced device.
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) am.ClearCommunicationDevice();
            else
            {
#pragma warning disable CA1422
                am.SpeakerphoneOn = false;
#pragma warning restore CA1422
            }
            am.Mode = Mode.Normal;
            SpeakerActive = true;
            RouteChanged?.Invoke();
            return true;
        }

        SpeakerActive = false;
        am.Mode = Mode.InCommunication;
        RouteChanged?.Invoke();

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            AudioDeviceInfo? chosen = null;
            if (speaker)
            {
                chosen = Find(am, AudioDeviceType.BuiltinSpeaker);
            }
            else
            {
                chosen = Find(am, AudioDeviceType.BluetoothSco)
                         ?? Find(am, AudioDeviceType.WiredHeadset)
                         ?? Find(am, AudioDeviceType.BuiltinEarpiece);
            }
            return chosen != null && am.SetCommunicationDevice(chosen);
        }

#pragma warning disable CA1422 // SpeakerphoneOn is the route control below API 31
        am.SpeakerphoneOn = speaker;
#pragma warning restore CA1422
        return true;
    }

    /// <summary>Back to the system's normal routing (nothing is linked any more).</summary>
    public static void Reset(Context context)
    {
        SpeakerActive = false;
        if (context.GetSystemService(Context.AudioService) is not AudioManager am) return;

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            am.ClearCommunicationDevice();
        }
        else
        {
#pragma warning disable CA1422
            am.SpeakerphoneOn = false;
#pragma warning restore CA1422
        }
        am.Mode = Mode.Normal;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private static AudioDeviceInfo? Find(AudioManager am, AudioDeviceType type)
    {
        foreach (var device in am.AvailableCommunicationDevices)
            if (device.Type == type) return device;
        return null;
    }
}
