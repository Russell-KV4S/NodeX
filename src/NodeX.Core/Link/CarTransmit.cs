using NodeX.Core.Settings;

namespace NodeX.Core;

/// <summary>Which steering-wheel button talks (the other keeps skipping favourites). The numbers are saved in settings: do not reorder.</summary>
public enum CarWheelTalk
{
    Off = 0,
    Next = 1,
    Back = 2
}

/// <summary>What a car "talk" command should do.</summary>
public enum CarTalkAction
{
    /// <summary>Do nothing and tell the driver why (<see cref="CarTalkDecision.Message"/>).</summary>
    Refuse,
    Key,
    Unkey
}

public sealed record CarTalkDecision(CarTalkAction Action, string Message = "");

/// <summary>
/// The rules for keying the transmitter from Android Auto. Pure so they can be tested without a car. The safety rules:
/// off unless the user turned it on; toggle only; it keys only the node the car shows as now playing AND that is the
/// chosen transmit node (the car never changes the transmit node); never without a connected link; the existing
/// <see cref="TransmitPolicy"/> and time-out timer still apply in <see cref="NodePttController"/>; unkeying is never refused.
/// </summary>
public static class CarTransmit
{
    public static CarTalkDecision Decide(
        bool carPttEnabled,
        string? nowPlayingNode,
        string? transmitNode,
        bool linkConnected,
        bool micUsable,
        bool currentlyKeyed,
        TransmitBlock block)
    {
        // Stopping a transmission is always allowed, whatever else is wrong.
        if (currentlyKeyed) return new(CarTalkAction.Unkey);

        if (!carPttEnabled)
            return new(CarTalkAction.Refuse, "Talk from the car is off. Turn it on in NodeX Settings on your phone.");
        if (nowPlayingNode == null || !linkConnected)
            return new(CarTalkAction.Refuse, "Not connected to a node.");
        if (block != TransmitBlock.None)
            return new(CarTalkAction.Refuse, block == TransmitBlock.ListenOnly
                ? "Listen only: add your callsign in NodeX Settings to talk."
                : "Microphone access is off in NodeX.");
        if (nowPlayingNode != transmitNode)
            return new(CarTalkAction.Refuse, $"Node {nowPlayingNode} is listen-only. Choose it as the transmit node on your phone to talk.");
        if (!micUsable)
            return new(CarTalkAction.Refuse, "The phone is not letting NodeX use the microphone from the car right now. Open NodeX on the phone once.");

        return new(CarTalkAction.Key);
    }

    /// <summary>
    /// Carries out a toggle for the node the car shows. Returns what happened and, when refused, why. Keys only
    /// <see cref="NodeRuntime.Link"/> (the transmit link).
    /// </summary>
    public static async Task<CarTalkDecision> ToggleAsync(NodeRuntime runtime, string? nowPlayingNode, bool micUsable)
    {
        var link = runtime.Link;
        var decision = Decide(
            runtime.Config.Settings.CarPtt,
            nowPlayingNode,
            runtime.TransmitNode,
            link?.Session.State == Iax2SessionState.Connected,
            micUsable,
            link?.Ptt.IsPttActive == true,
            runtime.Transmit.Block);

        if (link == null) return decision;

        switch (decision.Action)
        {
            case CarTalkAction.Unkey:
                await link.Ptt.SetPttStateAsync(false, PttSource.Car);
                break;

            case CarTalkAction.Key:
                await link.Ptt.SetPttStateAsync(true, PttSource.Car);
                if (!link.Ptt.IsPttActive)
                    return new(CarTalkAction.Refuse, "Could not start transmitting.");
                break;
        }
        return decision;
    }

    /// <summary>
    /// The longest one car transmission may last before it is released automatically. Shorter than the normal
    /// time-out timer because a driver cannot watch the screen to notice a transmitter left keyed.
    /// </summary>
    public static readonly TimeSpan MaxCarTransmit = TimeSpan.FromSeconds(120);

    /// <summary>True when any link is transmitting, whoever keyed it.</summary>
    public static bool AnyKeyed(NodeRuntime runtime) => runtime.Links.Any(l => l.Ptt.IsPttActive);

    /// <summary>
    /// Releases every keyed link, whoever keyed it. This is what any wheel or car-screen media button does while
    /// transmitting, so the driver can always stop a transmission without reaching for the phone. Returns true when
    /// something was keyed.
    /// </summary>
    public static async Task<bool> UnkeyAllAsync(NodeRuntime runtime)
    {
        bool any = false;
        foreach (var link in runtime.Links)
        {
            if (!link.Ptt.IsPttActive) continue;
            any = true;
            try { await link.Ptt.SetPttStateAsync(false, PttSource.Car); }
            catch (Exception) { /* the link is going away anyway */ }
        }
        return any;
    }

    /// <summary>Releases any key that came from the car (car session ended, focus lost, service stopped). Safe to call any time.</summary>
    public static async Task UnkeyCarAsync(NodeRuntime runtime)
    {
        foreach (var link in runtime.Links)
        {
            if (link.Ptt.IsPttActive && link.Ptt.CurrentSource == PttSource.Car)
            {
                try { await link.Ptt.SetPttStateAsync(false, PttSource.Car); }
                catch (Exception) { /* the link is going away anyway */ }
            }
        }
    }
}
