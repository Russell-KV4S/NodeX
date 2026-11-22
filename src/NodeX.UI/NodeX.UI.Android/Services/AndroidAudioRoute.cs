using System;
using Android.Content;
using Android.Media;
using NodeX.Core.Diagnostics;

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
        var app = context.ApplicationContext ?? context;
        if (app.GetSystemService(Context.AudioService) is not AudioManager am) return false;

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
            DiagnosticLog.Default.Info("audio", "route -> speaker (Media, Mode.Normal)");
            RouteChanged?.Invoke();
            return true;
        }

        SpeakerActive = false;
        am.Mode = Mode.InCommunication;

        bool result;
        string chosenDesc;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            AudioDeviceInfo? chosen = Find(am, AudioDeviceType.BluetoothSco)
                                      ?? Find(am, AudioDeviceType.WiredHeadset)
                                      ?? Find(am, AudioDeviceType.BuiltinEarpiece);
            result = chosen != null && am.SetCommunicationDevice(chosen);
            chosenDesc = chosen != null ? $"{chosen.Type} ({result})" : "none";
        }
        else
        {
#pragma warning disable CA1422 // SpeakerphoneOn is the route control below API 31
            am.SpeakerphoneOn = false;
#pragma warning restore CA1422
            result = true;
            chosenDesc = "legacy earpiece/BT";
        }

        DiagnosticLog.Default.Info("audio", $"route -> phone/headset (VoiceCommunication, Mode.InCommunication, device={chosenDesc})");
        RouteChanged?.Invoke();
        return result;
    }

    /// <summary>
    /// A car took over while linked: leave communication mode and any forced device so the link plays as media
    /// through the car, and rebuild playback. The speaker/phone choice is kept for when the car goes away.
    /// </summary>
    public static void EnterCar(Context context)
    {
        var app = context.ApplicationContext ?? context;
        if (app.GetSystemService(Context.AudioService) is AudioManager am)
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(31)) am.ClearCommunicationDevice();
                am.Mode = Mode.Normal;
            }
            catch (Exception) { /* routing stays as it was */ }
        }
        DiagnosticLog.Default.Info("audio", "route -> car (Media, Mode.Normal)");
        RouteChanged?.Invoke();
    }

    /// <summary>The car went away while linked: put the phone/headset route back (the loudspeaker needs nothing).</summary>
    public static void LeaveCar(Context context)
    {
        if (SpeakerActive)
        {
            DiagnosticLog.Default.Info("audio", "route <- car (restoring loudspeaker)");
            RouteChanged?.Invoke();
        }
        else
        {
            SetSpeaker(context, false);
        }
    }

    /// <summary>Back to the system's normal routing (nothing is linked any more).</summary>
    public static void Reset(Context context)
    {
        bool wasSpeaker = SpeakerActive;
        SpeakerActive = false;
        var app = context.ApplicationContext ?? context;
        if (app.GetSystemService(Context.AudioService) is not AudioManager am) return;

        bool wasNonNormal = am.Mode != Mode.Normal;
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
        if (wasSpeaker || wasNonNormal)
            DiagnosticLog.Default.Info("audio", $"route -> reset (Mode.Normal, wasSpeaker={wasSpeaker}, wasNonNormalMode={wasNonNormal})");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private static AudioDeviceInfo? Find(AudioManager am, AudioDeviceType type)
    {
        foreach (var device in am.AvailableCommunicationDevices)
            if (device.Type == type) return device;
        return null;
    }
}
