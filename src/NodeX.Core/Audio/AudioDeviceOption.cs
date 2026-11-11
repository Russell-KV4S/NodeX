namespace NodeX.Core.Audio;

/// <summary>
/// One microphone or speaker the user can pick in Settings. <see cref="Id"/> is the value saved in
/// <c>NodeSettings.InputDeviceId</c>/<c>OutputDeviceId</c> (null = system default). Ids are device names, not
/// driver indexes, because indexes change when devices are plugged in or out.
/// </summary>
public sealed record AudioDeviceOption(string? Id, string Name)
{
    /// <summary>The "use whatever the system default is" entry shown first in each list.</summary>
    public static AudioDeviceOption SystemDefault { get; } = new(null, "System default");

    public override string ToString() => Name;
}

/// <summary>The devices a platform head can offer, not including the "System default" entry.</summary>
public sealed record AudioDeviceCatalog(
    IReadOnlyList<AudioDeviceOption> Inputs,
    IReadOnlyList<AudioDeviceOption> Outputs);
