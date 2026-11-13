namespace NodeX.Core;

/// <summary>What a headset Play/Pause (media key) press should do.</summary>
public enum MediaKeyAction
{
    /// <summary>Do nothing (feature off, or nothing linked). Silent.</summary>
    Ignore,
    /// <summary>Do nothing and tell the user why (<see cref="MediaKeyDecision.Message"/>).</summary>
    Refuse,
    Key,
    Unkey
}

public sealed record MediaKeyDecision(MediaKeyAction Action, string Message = "");

/// <summary>
/// The rules for using a headset's media button (Play/Pause) as push-to-talk. A media key has no "released" event, so it
/// is toggle only: press to go on air, press again to stop. Safety rules: off unless the user turned it on; keys only the
/// transmit link (<see cref="NodeRuntime.Link"/>) and only while it is connected; the <see cref="TransmitPolicy"/>
/// (listen-only, no microphone) and the time-out timer still apply in <see cref="NodePttController"/>; unkeying is never
/// refused. Pure so it can be tested without a headset.
/// </summary>
public static class MediaKeyTransmit
{
    public static MediaKeyDecision Decide(bool enabled, bool linkConnected, bool currentlyKeyed, TransmitBlock block)
    {
        // Stopping a transmission is always allowed.
        if (currentlyKeyed) return new(MediaKeyAction.Unkey);

        if (!enabled || !linkConnected) return new(MediaKeyAction.Ignore);

        if (block != TransmitBlock.None)
            return new(MediaKeyAction.Refuse, "Microphone access is off in NodeX.");

        return new(MediaKeyAction.Key);
    }

    /// <summary>Carries out one headset-button press on the transmit link and returns what happened.</summary>
    public static async Task<MediaKeyDecision> ToggleAsync(NodeRuntime runtime)
    {
        var link = runtime.Link;
        var decision = Decide(
            runtime.Config.Settings.MediaKeyPtt,
            link?.Session.State == Iax2SessionState.Connected,
            link?.Ptt.IsPttActive == true,
            runtime.Transmit.Block);

        if (link == null) return decision;

        switch (decision.Action)
        {
            case MediaKeyAction.Unkey:
                await link.Ptt.SetPttStateAsync(false, PttSource.MediaKey);
                break;

            case MediaKeyAction.Key:
                await link.Ptt.SetPttStateAsync(true, PttSource.MediaKey);
                if (!link.Ptt.IsPttActive)
                    return new(MediaKeyAction.Refuse, "This node is listen-only. Choose it as the transmit node to talk.");
                break;
        }
        return decision;
    }

    /// <summary>Releases any key that came from a media button (headset gone, link ending). Safe to call any time.</summary>
    public static async Task UnkeyAsync(NodeRuntime runtime)
    {
        foreach (var link in runtime.Links)
        {
            if (link.Ptt.IsPttActive && link.Ptt.CurrentSource == PttSource.MediaKey)
            {
                try { await link.Ptt.SetPttStateAsync(false, PttSource.MediaKey); }
                catch (Exception) { /* the link is going away anyway */ }
            }
        }
    }
}
