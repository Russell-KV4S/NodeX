namespace NodeX.Core;

/// <summary>Why transmitting is not currently possible.</summary>
public enum TransmitBlock
{
    None,
    /// <summary>The operating system has not granted microphone access.</summary>
    MicrophoneDenied
}

/// <summary>
/// Single source of truth for "may this app key the transmitter right now?". Receiving never depends on it:
/// a user who denied the microphone permission can still connect and listen. A callsign and AllStarLink login
/// are always required to use the app at all (see NodeSettingsValidator), so there is no callsign-free mode.
/// Input: the platform's microphone permission.
/// </summary>
public sealed class TransmitPolicy
{
    private readonly Lock _gate = new();
    private bool _microphoneAllowed = true;

    /// <summary>False when the OS denied microphone access. Platform heads set this; default true.</summary>
    public bool MicrophoneAllowed
    {
        get { lock (_gate) return _microphoneAllowed; }
        set => Update(ref _microphoneAllowed, value);
    }

    /// <summary>The first reason transmitting is blocked, or <see cref="TransmitBlock.None"/>.</summary>
    public TransmitBlock Block
    {
        get
        {
            lock (_gate)
            {
                if (!_microphoneAllowed) return TransmitBlock.MicrophoneDenied;
                return TransmitBlock.None;
            }
        }
    }

    public bool CanTransmit => Block == TransmitBlock.None;

    /// <summary>User-facing explanation, empty when transmitting is allowed.</summary>
    public string Reason => Block switch
    {
        TransmitBlock.MicrophoneDenied => "Microphone access is off, so you can listen but not transmit.",
        _ => ""
    };

    /// <summary>Raised (on any thread) when <see cref="Block"/> may have changed.</summary>
    public event Action? Changed;

    private void Update(ref bool field, bool value)
    {
        lock (_gate)
        {
            if (field == value) return;
            field = value;
        }
        Changed?.Invoke();
    }
}
