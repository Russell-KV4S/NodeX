namespace NodeX.Core.Settings;

/// <summary>
/// Non-secret user configuration, persisted as JSON. The node password is deliberately NOT part of
/// this type: it lives in an <see cref="ISecretStore"/> (OS-protected storage).
/// </summary>
public sealed record NodeSettings
{
    /// <summary>Your AllStarLink node number (from the allstarlink.org portal).</summary>
    public string NodeNumber { get; init; } = "";

    /// <summary>Optional callsign shown in the UI.</summary>
    public string Callsign { get; init; } = "";

    /// <summary>Node pre-filled in the connect box.</summary>
    public string DefaultTargetNode { get; init; } = "";

    /// <summary>Quick-connect node numbers.</summary>
    public List<string> FavoriteNodes { get; init; } = [];

    /// <summary>Registry endpoint; change only for a test registry.</summary>
    public string RegistrationUrl { get; init; } = AslRegistrationService.DefaultRegistrationUrl;

    /// <summary>IAX2 port advertised to the registry.</summary>
    public int IaxPort { get; init; } = Iax2Protocol.DefaultPort;

    /// <summary>Register with AllStarLink automatically when the app starts.</summary>
    public bool RegisterOnStartup { get; init; } = true;

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

    /// <summary>Latching (toggle) PTT instead of press-and-hold.</summary>
    public bool TogglePtt { get; init; }
}
