namespace NodeX.Core.Settings;

/// <summary>
/// Non-secret user configuration, persisted as JSON. The node password is deliberately NOT part of
/// this type: it lives in an <see cref="ISecretStore"/> (OS-protected storage).
/// </summary>
public sealed record NodeSettings
{
    /// <summary>Your AllStarLink node number (from the allstarlink.org portal).</summary>
    public string NodeNumber { get; init; } = "";

    /// <summary>
    /// Your amateur radio callsign. Required unless <see cref="ListenOnly"/> is on. It is shown in the UI;
    /// the app does not verify it against a licence database.
    /// </summary>
    public string Callsign { get; init; } = "";

    /// <summary>
    /// For users without a callsign: connect and listen, but never transmit (PTT is disabled).
    /// When false, a callsign is required.
    /// </summary>
    public bool ListenOnly { get; init; }

    /// <summary>Node pre-filled in the connect box.</summary>
    public string DefaultTargetNode { get; init; } = "";

    /// <summary>Quick-connect node numbers.</summary>
    public List<string> FavoriteNodes { get; init; } = [];

    /// <summary>Friendly names for favourites, keyed by node number (e.g. "27339" → "East Coast Reflector").</summary>
    public Dictionary<string, string> FavoriteLabels { get; init; } = [];

    /// <summary>Registry endpoint; change only for a test registry.</summary>
    public string RegistrationUrl { get; init; } = AslRegistrationService.DefaultRegistrationUrl;

    /// <summary>IAX2 port advertised to the registry.</summary>
    public int IaxPort { get; init; } = Iax2Protocol.DefaultPort;

    /// <summary>Register with AllStarLink automatically when the app starts.</summary>
    public bool RegisterOnStartup { get; init; } = true;

    /// <summary>
    /// Also register with classic IAX2 REGREQ over UDP every 60 s (in addition to HTTPS). Off by default: the
    /// registry then records this app's UDP source port for the node instead of <see cref="IaxPort"/>.
    /// </summary>
    public bool UdpRegistration { get; init; }

    /// <summary>Re-dial automatically when an established link is lost or the network changes.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>Selected microphone (platform-specific id); null = system default.</summary>
    public string? InputDeviceId { get; init; }

    /// <summary>Selected speaker (platform-specific id); null = system default.</summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>
    /// Android: start each link on the loudspeaker instead of the phone earpiece. The Speaker/Phone button on the
    /// main screen can still switch it while linked.
    /// </summary>
    public bool DefaultToSpeaker { get; init; }

    /// <summary>Microphone gain, 1.0 = unity.</summary>
    public float MicGain { get; init; } = 1.0f;

    /// <summary>
    /// Clean up the transmitted voice: high-pass filter, automatic gain towards a radio-friendly level and a
    /// soft limiter. Turn off to send the microphone exactly as it is (then <see cref="MicGain"/> is the only control).
    /// </summary>
    public bool MicProcessing { get; init; } = true;

    /// <summary>Transmit time-out timer in seconds.</summary>
    public int TimeOutTimerSeconds { get; init; } = 180;

    /// <summary>Toggle PTT (tap to start, tap again to stop) instead of press-and-hold.</summary>
    public bool TogglePtt { get; init; }

    /// <summary>
    /// Key code of a phone button (volume key, headset button...) used as push-to-talk; 0 = none. It follows
    /// <see cref="TogglePtt"/>: hold-to-talk, or tap once to start and again to stop.
    /// </summary>
    public int HardwarePttKey { get; init; }

    /// <summary>
    /// Android: the Play/Pause button on a Bluetooth headset or earbuds acts as push-to-talk while linked (press to
    /// talk, press again to stop), even with the screen off. Off by default because it takes over that button and a stray
    /// press would go on air.
    /// </summary>
    public bool MediaKeyPtt { get; init; }

    // ----- BLE PTT button (learned in Settings; empty address = none) -----

    /// <summary>Bluetooth address of the BLE PTT button.</summary>
    public string BlePttAddress { get; init; } = "";

    /// <summary>Display name of the BLE PTT button.</summary>
    public string BlePttName { get; init; } = "";

    /// <summary>UUID of the characteristic the button notifies on.</summary>
    public string BlePttCharacteristic { get; init; } = "";

    /// <summary>Learned "pressed" packet as hex.</summary>
    public string BlePttPress { get; init; } = "";

    /// <summary>Learned "released" packet as hex; empty for a press-only button.</summary>
    public string BlePttRelease { get; init; } = "";

    /// <summary>The learned BLE PTT button, or null when none is set up.</summary>
    public BlePttProfile? GetBlePttProfile() =>
        BlePttAddress.Length > 0 && BlePttCharacteristic.Length > 0 && BlePttPress.Length > 0
            ? new BlePttProfile(BlePttAddress, BlePttName, BlePttCharacteristic, BlePttPress, BlePttRelease)
            : null;

    /// <summary>
    /// Android Auto: show a Talk button on the car's now-playing screen that keys the transmit node (tap to talk, tap
    /// again to stop). Off by default; nothing in the car can transmit unless this is on.
    /// </summary>
    public bool CarPtt { get; init; }

    /// <summary>
    /// Android Auto, only with <see cref="CarPtt"/>: which steering-wheel button (Next or Back/Previous) toggles talk
    /// instead of skipping favourites; the other one still skips. Off by default because a stray press would go on air.
    /// </summary>
    public CarWheelTalk CarPttWheel { get; init; }

    /// <summary>
    /// Show a reminder to identify (by voice) while linked. Off by default. Nothing is ever transmitted
    /// automatically; the reminder only prompts the user.
    /// </summary>
    public bool IdReminderEnabled { get; init; }

    /// <summary>Minutes after the user starts transmitting before the ID reminder appears.</summary>
    public int IdReminderMinutes { get; init; } = 10;

    /// <summary>
    /// Write per-step timings and protocol state to the troubleshooting log file (in addition to the normal event
    /// lines, which are always logged). Off by default because it is chatty; turn on to chase a problem, then send the log.
    /// </summary>
    public bool DetailedLogging { get; init; }
}
