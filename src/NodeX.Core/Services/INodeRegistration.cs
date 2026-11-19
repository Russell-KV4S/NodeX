using System.Net;

namespace NodeX.Core;

/// <summary>
/// Keeps this node registered with AllStarLink. Implemented over HTTPS (<see cref="AslRegistrationService"/>),
/// over IAX2 from the call socket (<see cref="Iax2Registrar"/>), and by <see cref="FallbackRegistration"/>, which
/// prefers IAX2 and falls back to HTTPS.
/// </summary>
public interface INodeRegistration : IAsyncDisposable
{
    /// <summary>True after the last attempt succeeded (see <see cref="LastSuccessfulHeartbeat"/> for how long ago).</summary>
    bool IsRegistered { get; }

    /// <summary>When the registry last accepted this node (UTC), or null if never.</summary>
    DateTime? LastSuccessfulHeartbeat { get; }

    /// <summary>Human-readable result of the last attempt.</summary>
    string LastStatusMessage { get; }

    /// <summary>Public address the registry recorded for this node.</summary>
    IPAddress? PerceivedAddress { get; }

    /// <summary>Public port the registry recorded for this node.</summary>
    int PerceivedPort { get; }

    /// <summary>Time between registrations right now.</summary>
    TimeSpan CurrentInterval { get; }

    /// <summary>Short name of the way it registers ("IAX2" or "HTTPS"), for the log and the status text.</summary>
    string Transport { get; }

    event Action<bool, string>? RegistrationStatusChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    /// <summary>Registers right now, outside the normal schedule (after a network change, before dialling).</summary>
    Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default);
}
