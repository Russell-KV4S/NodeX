using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeX.Core;

namespace NodeX.UI.ViewModels;

/// <summary>
/// One row of the "Linked nodes" card: a node the user is linked to (or is linking to / re-linking to), with its
/// state, whether audio is arriving from it, whether it is the transmit node, and per-node actions.
/// All state is copied from the <see cref="NodeConnection"/> by <see cref="Refresh"/> on the UI thread.
/// </summary>
public sealed partial class LinkItemViewModel : ObservableObject
{
    private readonly Func<string, string> _labelFor;
    private readonly Action<string> _selectTransmit;
    private readonly Action<string> _toggleMute;
    private readonly Action<string> _disconnect;
    private readonly Func<string, bool> _isFavorite;
    private readonly Action<string> _toggleFavorite;
    private readonly Action<string, string> _saveLabel;
    private string _shownLabel = "";

    public LinkItemViewModel(
        string node,
        Func<string, string> labelFor,
        Action<string> selectTransmit,
        Action<string> toggleMute,
        Action<string> disconnect,
        Func<string, bool>? isFavorite = null,
        Action<string>? toggleFavorite = null,
        Action<string, string>? saveLabel = null)
    {
        Node = node;
        _labelFor = labelFor;
        _selectTransmit = selectTransmit;
        _toggleMute = toggleMute;
        _disconnect = disconnect;
        _isFavorite = isFavorite ?? (_ => false);
        _toggleFavorite = toggleFavorite ?? (_ => { });
        _saveLabel = saveLabel ?? ((_, _) => { });
        Title = $"Node {node}";
    }

    public string Node { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    public partial string Details { get; set; } = "";

    public bool HasDetails => Details.Length > 0;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "CONNECTING...";

    [ObservableProperty]
    public partial string RttText { get; set; } = "RTT —";

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    /// <summary>Audio is arriving from this node right now (set from the session's key events).</summary>
    [ObservableProperty]
    public partial bool IsReceiving { get; set; }

    /// <summary>When audio from this node last started or stopped arriving (null = not heard this session).</summary>
    private DateTime? _lastHeard;

    [ObservableProperty]
    public partial string LastHeardText { get; set; } = "Not heard yet";

    /// <summary>Records that this node was just heard (called on key and unkey).</summary>
    public void NoteHeard()
    {
        _lastHeard = DateTime.Now;
        UpdateLastHeardText();
    }

    /// <summary>"Hearing now" / "Last heard 7:41 PM (2 min ago)". The UI timer calls this too, so the age ticks.</summary>
    public void UpdateLastHeardText()
    {
        if (IsReceiving) { LastHeardText = "Hearing now"; return; }
        if (_lastHeard is not { } t) { LastHeardText = "Not heard yet"; return; }
        var age = DateTime.Now - t;
        string ago = age.TotalSeconds < 60 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : $"{(int)age.TotalHours} h {age.Minutes} min ago";
        LastHeardText = $"Last heard {t:h:mm tt} ({ago})";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectTransmit), nameof(MuteButtonText), nameof(MuteAutomationName))]
    public partial bool IsTransmitTarget { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteButtonText), nameof(MuteAutomationName), nameof(MuteGlyph), nameof(MuteShortText))]
    public partial bool IsMuted { get; set; }

    /// <summary>Speaker glyph + word for the compact phone row, so mute state is never colour-only.</summary>
    public string MuteGlyph => IsMuted ? "🔇" : "🔊";

    public string MuteShortText => IsMuted ? "Muted" : "Mute";

    public bool CanSelectTransmit => !IsTransmitTarget;

    public string MuteButtonText => IsMuted ? "🔇 Muted · tap to hear" : "🔊 Mute";

    public string MuteAutomationName => IsMuted ? $"Unmute node {Node}" : $"Mute node {Node}";

    public string SelectAutomationName => $"Transmit to node {Node}";

    public string DisconnectAutomationName => $"Disconnect node {Node}";

    // --- Favourite this node (star on the card; the pencil opens a small rename box) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph), nameof(FavoriteAutomationName))]
    public partial bool IsFavorite { get; set; }

    /// <summary>The name being edited in the rename flyout.</summary>
    [ObservableProperty]
    public partial string LabelInput { get; set; } = "";

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public string FavoriteAutomationName =>
        IsFavorite ? $"Remove node {Node} from favorites" : $"Add node {Node} to favorites";

    public string RenameAutomationName => $"Rename favorite {Node}";

    // --- Compact chip shown in the always-visible top bar ---

    /// <summary>"27339 · East Coast HUB1" or just the number.</summary>
    [ObservableProperty]
    public partial string ChipTitle { get; set; } = "";

    /// <summary>Short state: TX, listen, muted, connecting, reconnecting (n).</summary>
    [ObservableProperty]
    public partial string ChipStatus { get; set; } = "";

    /// <summary>Copies the connection's current state. Call on the UI thread.</summary>
    public void Refresh(NodeConnection c)
    {
        string label = _labelFor(Node);
        Title = label.Length > 0 ? $"{Node} · {label}" : $"Node {Node}";
        ChipTitle = label.Length > 0 ? $"{Node} · {label}" : Node;

        IsFavorite = _isFavorite(Node);
        if (label != _shownLabel) { _shownLabel = label; LabelInput = label; }

        var info = c.Info;
        Details = info == null ? "" :
            string.IsNullOrWhiteSpace(info.Callsign) ? info.Description : $"{info.Callsign} • {info.Description}";

        var link = c.Link;
        var state = link?.Session.State ?? Iax2SessionState.Disconnected;
        IsConnected = state == Iax2SessionState.Connected;

        StatusText = c.Reconnect.Active && !IsConnected
            ? $"RECONNECTING ({c.Reconnect.Attempt})"
            : link == null ? "CONNECTING..." : StateText(state);

        IsTransmitTarget = c.IsTransmitTarget;
        IsMuted = c.Muted;
        if (!IsConnected) IsReceiving = false;
        RefreshRtt(c);

        ChipStatus = !IsConnected
            ? (c.Reconnect.Active ? $"reconnecting ({c.Reconnect.Attempt})" : "connecting…")
            : IsTransmitTarget ? "TX"
            : IsMuted ? "muted"
            : "listen";
    }

    /// <summary>Badge text for a call state.</summary>
    public static string StateText(Iax2SessionState state) => state switch
    {
        Iax2SessionState.Connected => "CONNECTED",
        Iax2SessionState.Connecting => "CONNECTING...",
        Iax2SessionState.CallTokenHandshake => "HANDSHAKE...",
        Iax2SessionState.Authenticating => "AUTHENTICATING...",
        Iax2SessionState.Disconnecting => "DISCONNECTING...",
        _ => "DISCONNECTED"
    };

    /// <summary>Updates only the round-trip time (called from the UI timer).</summary>
    public void RefreshRtt(NodeConnection c)
    {
        var rtt = c.Link?.Session.RoundTripTime ?? TimeSpan.Zero;
        RttText = IsConnected && rtt > TimeSpan.Zero ? $"RTT {rtt.TotalMilliseconds:F0} ms" : "RTT —";
        UpdateLastHeardText();
    }

    [RelayCommand]
    public void SelectTransmit() => _selectTransmit(Node);

    [RelayCommand]
    public void ToggleMute() => _toggleMute(Node);

    [RelayCommand]
    public void Disconnect() => _disconnect(Node);

    [RelayCommand]
    public void ToggleFavorite() => _toggleFavorite(Node);

    [RelayCommand]
    public void SaveLabel() => _saveLabel(Node, LabelInput);
}
