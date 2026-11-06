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
    /// Show a reminder to identify (by voice) while linked. Off by default. Nothing is ever transmitted
    /// automatically; the reminder only prompts the user.
    /// </summary>
    public bool IdReminderEnabled { get; init; }

    /// <summary>Minutes after the user starts transmitting before the ID reminder appears.</summary>
    public int IdReminderMinutes { get; init; } = 10;
}
