namespace NodeX.Core;

/// <summary>
/// Whether the platform must keep the app running, and what to tell the user about it.
/// A configured node should stay registered with AllStarLink even when nothing is linked, so connecting later is
/// instant; only <see cref="NodeRuntime.ExitAsync"/> (or deliberately not being configured) turns this off.
/// </summary>
/// <param name="Needed">True while the app should be kept alive in the background.</param>
/// <param name="LinkedNode">The node currently linked or being linked, or null when idle.</param>
/// <param name="Registered">Whether the registry has accepted our latest registration.</param>
/// <param name="RegistrationStatus">Human-readable registration status.</param>
public sealed record BackgroundState(bool Needed, string? LinkedNode, bool Registered, string RegistrationStatus)
{
    public bool IsLinked => LinkedNode != null;

    /// <summary>One line for an ongoing notification.</summary>
    public string Summary =>
        IsLinked ? $"Linked to node {LinkedNode}"
        : Registered ? "Registered with AllStarLink. Ready to connect."
        : $"Waiting to register: {RegistrationStatus}";
}
