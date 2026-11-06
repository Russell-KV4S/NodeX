using System;
using AVFoundation;
using Foundation;

namespace NodeX.UI.iOS.Audio;

/// <summary>
/// Configures the shared <see cref="AVAudioSession"/> so the node keeps running in the background:
/// PlayAndRecord with Bluetooth HFP headsets allowed and mixing with other audio, as required by
/// the spec (background audio mode + continuous output keep the process and UDP socket alive).
/// </summary>
public sealed class IosAudioSessionManager : IDisposable
{
    private NSObject? _interruptionObserver;
    private NSObject? _routeObserver;

    /// <summary>Raised when an interruption (phone call, Siri, alarm) ends and the session was reactivated.</summary>
    public event Action? InterruptionEnded;

    /// <summary>Raised when an interruption begins.</summary>
    public event Action? InterruptionBegan;

    /// <summary>Raised when the audio route changes (headset connected/removed, SCO switch).</summary>
    public event Action<string>? RouteChanged;

    public event Action<string>? ErrorOccurred;

    /// <summary>Sets the category, activates the session and starts observing interruptions and route changes.</summary>
    public bool Activate()
    {
        var session = AVAudioSession.SharedInstance();

        // Spec 4: PlayAndRecord with AllowBluetoothHFP | MixWithOthers. DefaultToSpeaker keeps audio
        // on the loudspeaker (not the earpiece) when no headset is connected.
        if (!session.SetCategory(
                AVAudioSessionCategory.PlayAndRecord,
                AVAudioSessionCategoryOptions.AllowBluetooth // the HFP option; this binding has no AllowBluetoothHfp name
                | AVAudioSessionCategoryOptions.MixWithOthers
                | AVAudioSessionCategoryOptions.DefaultToSpeaker,
                out NSError? categoryError))
        {
            ErrorOccurred?.Invoke($"AVAudioSession category failed: {categoryError?.LocalizedDescription}");
            return false;
        }

        // VoiceChat mode enables echo cancellation and the voice-processing I/O path.
        session.SetMode(AVAudioSessionMode.VoiceChat, out _);

        // Match the 8 kHz pipeline; iOS may pick the nearest hardware rate and the device service resamples.
        session.SetPreferredSampleRate(8000, out _);
        session.SetPreferredIOBufferDuration(0.02, out _); // 20 ms frames

        if (!session.SetActive(true, out NSError? activeError))
        {
            ErrorOccurred?.Invoke($"AVAudioSession activation failed: {activeError?.LocalizedDescription}");
            return false;
        }

        _interruptionObserver ??= AVAudioSession.Notifications.ObserveInterruption(OnInterruption);
        _routeObserver ??= AVAudioSession.Notifications.ObserveRouteChange(
            (_, e) => RouteChanged?.Invoke(e.Reason.ToString()));
        return true;
    }

    /// <summary>Deactivates the session and notifies other apps so their audio can resume.</summary>
    public void Deactivate()
    {
        AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
    }

    /// <summary>Current hardware input sample rate (Hz), falling back to 48000.</summary>
    public double HardwareSampleRate
    {
        get
        {
            double rate = AVAudioSession.SharedInstance().SampleRate;
            return rate > 0 ? rate : 48000;
        }
    }

    private void OnInterruption(object? sender, AVAudioSessionInterruptionEventArgs e)
    {
        if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
        {
            InterruptionBegan?.Invoke();
            return;
        }

        // Ended: reactivate the session; consumers restart their engines.
        if (AVAudioSession.SharedInstance().SetActive(true, out NSError? error))
        {
            InterruptionEnded?.Invoke();
        }
        else
        {
            ErrorOccurred?.Invoke($"Could not reactivate audio after interruption: {error?.LocalizedDescription}");
        }
    }

    public void Dispose()
    {
        _interruptionObserver?.Dispose();
        _routeObserver?.Dispose();
        _interruptionObserver = _routeObserver = null;
        Deactivate();
    }
}
