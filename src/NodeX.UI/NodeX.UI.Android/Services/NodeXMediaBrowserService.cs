using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Media;
using Android.Media.Browse;
using Android.Media.Session;
using Android.OS;
using Android.Runtime;
using Android.Service.Media;
using NodeX.Core;
using NodeX.Core.Settings;

namespace NodeX.UI.Android;

/// <summary>
/// Android Auto (and Google Assistant) entry point: a media app. The browse screen has two tabs: Active Links (one
/// row per connected node, like the phone's cards) and Favourites (selecting one dials it). The now-playing screen
/// shows one node with its buttons: Mute, Disconnect and, for a listen-only node, "Transmit here" (the only way the
/// car changes the transmit node, and always a deliberate tap). It is listen-only unless the user turned on "Talk
/// from the car" in Settings; then a Talk button (and optionally a wheel button) toggles the transmit node through
/// <see cref="CarTransmit"/>, which enforces the safety rules. While transmitting, every media button only stops
/// the transmission. The wheel's Next/Back never dial: they step through the connected nodes. Every car key is
/// released when the car session ends, audio focus is lost, the car time limit passes or this service is destroyed.
/// </summary>
[Service(Name = "com.kv4s.nodex.NodeXMediaBrowserService", Exported = true, Label = "NodeX", Icon = "@mipmap/ic_launcher")]
[IntentFilter(new[] { "android.media.browse.MediaBrowserService" })]
public sealed class NodeXMediaBrowserService : MediaBrowserService, AudioManager.IOnAudioFocusChangeListener
{
    private const string RootId = "nodex_root";
    private const string LastNodePref = "car_last_node";

    // Callers allowed to browse and control: Android Auto, the Assistant, the system UI and the system itself.
    private static readonly string[] AllowedPackages =
    [
        "com.google.android.projection.gearhead",
        "com.google.android.googlequicksearchbox",
        "com.android.systemui",
        "android",
    ];

    private MediaSession? _session;
    private AudioFocusRequestClass? _focus;
    private string? _nowPlaying;          // the node this car session is playing; null = nothing
    private string? _error;
    private readonly HashSet<string> _mutedForFocus = [];
    private Action? _changed;
    private readonly object _gate = new();

    private const string TalkAction = "nodex.talk";
    private const string MuteAction = "nodex.mute";
    private const string DisconnectAction = "nodex.disconnect";
    private const string TransmitHereAction = "nodex.transmit_here";

    // Android Auto browse layout hints (the documented media "content style" keys): rows with pictures, as a list.
    private const string ContentStyleSupported = "android.media.browse.CONTENT_STYLE_SUPPORTED";
    private const string ContentStylePlayable = "android.media.browse.CONTENT_STYLE_PLAYABLE_HINT";
    private const string ContentStyleBrowsable = "android.media.browse.CONTENT_STYLE_BROWSABLE_HINT";
    private const int ContentStyleList = 1;

    private Action? _carChanged;
    private System.Threading.CancellationTokenSource? _carKeyLimit;  // ends a car transmission that runs too long
    private readonly RxActivity _rx = new();
    private System.Threading.Timer? _rxTimer;
    private string _talkers = "";                  // who the car screen last showed as heard
    private long _talkersShownAt;
    private ToneGenerator? _tone;
    private string? _notice;                       // short message shown on the car screen (why Talk was refused)
    private readonly Dictionary<NodeLink, Action<bool, PttSource>> _hooks = [];
    private Action<NodeLink>? _linkAdded;
    private Action<NodeLink>? _linkRemoved;

    public override void OnCreate()
    {
        base.OnCreate();

        _session = new MediaSession(this, "NodeX");
        _session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
        _session.SetCallback(new Callback(this));
        SessionToken = _session.SessionToken;
        _session.Active = true;
        PublishState();

        _changed = () => OnRuntimeChanged();
        var rt = AppServices.Runtime;
        rt.ConnectionsChanged += _changed;
        rt.ReconnectChanged += _ => _changed?.Invoke();

        // Follow keying on every link so the car shows it and beeps, whoever keyed (a time-out unkey beeps too).
        _linkAdded = Hook;
        _linkRemoved = Unhook;
        rt.LinkAdded += _linkAdded;
        rt.LinkRemoved += _linkRemoved;
        foreach (var existing in rt.Links) Hook(existing);

        // Who is talking: refresh the car's subtitle when the set of heard nodes changes. Polled because the "voice
        // within 500 ms" rule ends by itself, with no event. Changes are shown at most every 2 s so the car screen
        // does not flicker (Android Auto may announce metadata changes).
        _rxTimer = new System.Threading.Timer(_ => RefreshTalkers(), null, 700, 700);

        // Cold start: Android Auto can bind this service with no Activity, so the runtime is started here too.
        _ = StartRuntimeAsync();

        // Android Auto may bind this service before the car connection is known: take the audio once it is.
        _carChanged = () =>
        {
            if (!CarSession.Connected || _nowPlaying == null) return;
            BeginCarSession();
            PublishState();
        };
        CarSession.Changed += _carChanged;

        // A link that was already up on the phone is shown straight away, so Talk and the wheel buttons have a node
        // to act on without waiting for the next change.
        OnRuntimeChanged();
    }

    public override void OnDestroy()
    {
        var rt = AppServices.Runtime;
        if (_changed != null) rt.ConnectionsChanged -= _changed;
        _changed = null;
        if (_linkAdded != null) rt.LinkAdded -= _linkAdded;
        if (_linkRemoved != null) rt.LinkRemoved -= _linkRemoved;
        foreach (var link in _hooks.Keys.ToArray()) Unhook(link);
        _rxTimer?.Dispose();
        _rxTimer = null;
        _rx.Dispose();
        if (_carChanged != null) CarSession.Changed -= _carChanged;
        _carChanged = null;
        _carKeyLimit?.Cancel();
        _carKeyLimit = null;

        // The car went away: never leave the transmitter keyed. Wait briefly so the UNKEY actually leaves.
        try { Task.Run(() => CarTransmit.UnkeyCarAsync(rt)).Wait(TimeSpan.FromSeconds(1)); }
        catch (Exception) { /* nothing more to do */ }

        EndCarSession();
        _tone?.Release();
        _tone = null;
        _session?.Release();
        _session = null;
        base.OnDestroy();
    }

    private async Task StartRuntimeAsync()
    {
        try
        {
            await AppServices.Runtime.StartAsync();
            OnRuntimeChanged();
        }
        catch (Exception ex) { global::Android.Util.Log.Warn("NodeX", $"Car start failed: {ex.Message}"); }
    }

    // ----- browsing -----

    public override BrowserRoot? OnGetRoot(string clientPackageName, int clientUid, Bundle? rootHints)
    {
        if (!IsAllowedCaller(clientPackageName, clientUid)) return null;
        var extras = new Bundle();
        extras.PutBoolean(ContentStyleSupported, true);
        extras.PutInt(ContentStylePlayable, ContentStyleList);
        extras.PutInt(ContentStyleBrowsable, ContentStyleList);
        return new BrowserRoot(RootId, extras);
    }

    private bool IsAllowedCaller(string package, int uid)
    {
        if (uid == Process.MyUid() || uid == Process.SystemUid) return true;
        if (!AllowedPackages.Contains(package)) return false;

        // The claimed name must really belong to the calling uid.
        var owned = PackageManager?.GetPackagesForUid(uid);
        return owned != null && owned.Contains(package);
    }

    public override void OnLoadChildren(string parentId, Result result)
    {
        result.Detach();
        _ = Task.Run(async () =>
        {
            try
            {
                var rt = AppServices.Runtime;
                await rt.StartAsync();
                var items = new JavaList<MediaBrowser.MediaItem>();
                if (parentId == RootId)
                {
                    int activeCount = rt.Connections.Count;
                    int favCount = (rt.Config.Settings.FavoriteNodes ?? []).Count(f => !string.IsNullOrWhiteSpace(f));
                    foreach (var item in CarBrowse.BuildRootItems(activeCount, favCount))
                        items.Add(ToMediaItem(item));
                }
                else if (parentId == CarBrowse.ActiveFolderId)
                {
                    var conns = rt.Connections;
                    var talking = new HashSet<string>(_rx.Talking());
                    foreach (var item in CarBrowse.BuildActiveItems(rt.Config.Settings, conns, rt.TransmitNode, talking))
                        items.Add(ToMediaItem(item));
                }
                else if (parentId == CarBrowse.FavoritesFolderId)
                {
                    var connectedNodes = rt.Connections.Select(c => c.Node).ToList();
                    foreach (var item in CarBrowse.BuildFavoritesItems(rt.Config.Settings, connectedNodes, rt.TransmitNode))
                        items.Add(ToMediaItem(item));
                }
                result.SendResult(items);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("NodeX", $"OnLoadChildren failed for {parentId}: {ex.Message}");
                result.SendResult(new JavaList<MediaBrowser.MediaItem>());
            }
        });
    }

    private MediaBrowser.MediaItem ToMediaItem(CarBrowseItem item)
    {
        var builder = new MediaDescription.Builder()
            .SetMediaId(item.MediaId)!
            .SetTitle(item.Title)!
            .SetSubtitle(item.Subtitle ?? (item.Playable ? "Node " + item.MediaId : null))!;

        // The coloured tile beside the row (red = transmit, slate = listen-only), as on the phone's cards.
        string? drawable = item.Icon switch
        {
            CarIcon.Transmit => "ic_car_node_tx",
            CarIcon.Listen => "ic_car_node_rx",
            CarIcon.Favorite => "ic_car_node_fav",
            CarIcon.Disconnect => "ic_car_node_disconnect",
            _ => null
        };
        if (drawable != null)
            builder.SetIconUri(global::Android.Net.Uri.Parse($"android.resource://{PackageName}/drawable/{drawable}"));

        return new MediaBrowser.MediaItem(builder.Build()!, item.Playable ? MediaItemFlags.Playable : MediaItemFlags.Browsable);
    }

    private void NotifyChanges()
    {
        try
        {
            NotifyChildrenChanged(RootId);
            NotifyChildrenChanged(CarBrowse.ActiveFolderId);
            NotifyChildrenChanged(CarBrowse.FavoritesFolderId);
        }
        catch (Exception) { /* head unit disconnected or not ready */ }
    }

    // ----- control (from the MediaSession callback) -----

    private async Task PlayAsync(string node)
    {
        var rt = AppServices.Runtime;
        try
        {
            await rt.StartAsync();
            if (!rt.Config.IsComplete)
            {
                Fail("Finish setting up NodeX on your phone first.");
                return;
            }

            string? previous = _nowPlaying;
            bool alreadyUp = rt.Connections.Any(c => c.Node == node && c.Link != null);

            lock (_gate)
            {
                _error = null;
                _nowPlaying = node;
            }
            BeginCarSession();
            Remember(node);

            // Selecting an already-connected node only shows it. It does not move the transmit node: that takes a
            // deliberate tap on "Transmit here" on the now-playing screen.
            PublishState();
            NotifyChanges();

            // A favourite is added alongside the links that are already up: NodeX is built around several nodes at
            // once, so the car never hangs one up to make room. Hanging up is always the Disconnect button.
            if (!alreadyUp)
            {
                try
                {
                    var info = await rt.ConnectAsync(node);
                    if (info == null) FailConnect($"Could not find node {node}.", previous);
                }
                catch (System.OperationCanceledException)
                {
                    // Hung up (here or on the phone) before the call started: not a failure, no error beep.
                }
                catch (Exception ex)
                {
                    FailConnect(ex is InvalidOperationException or ArgumentException
                        ? ex.Message
                        : $"Could not connect: {ex.Message}", previous);
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex is InvalidOperationException or ArgumentException ? ex.Message : $"Could not connect: {ex.Message}");
        }
    }

    /// <summary>
    /// A favourite could not be added (not found, or the link limit is reached). With other links up, the car goes
    /// back to the node it was showing and says why; with none, it shows the error state.
    /// </summary>
    private void FailConnect(string message, string? previous)
    {
        NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", $"connect from car failed: {message}");
        bool previousStillUp = previous != null && AppServices.Runtime.Connections.Any(c => c.Node == previous);
        if (!previousStillUp)
        {
            Fail(message);
            return;
        }

        lock (_gate) _nowPlaying = previous;
        Beep(Tone.PropNack, 300);
        ShowNotice(message);
        NotifyChanges();
    }

    private async Task StopAsync()
    {
        string? node;
        lock (_gate) node = _nowPlaying;
        try
        {
            if (node != null) await AppServices.Runtime.DisconnectAsync(node);
        }
        catch (Exception) { /* already gone */ }
        EndCarSession();
        PublishState();
        NotifyChanges();
    }

    private async Task DisconnectNodeAsync(string node)
    {
        try
        {
            await AppServices.Runtime.DisconnectAsync(node);
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", $"disconnected node {node} from car screen");
        }
        catch (Exception ex)
        {
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", $"failed to disconnect node {node}: {ex.Message}");
        }

        lock (_gate)
        {
            if (_nowPlaying == node)
            {
                _nowPlaying = null;
            }
        }

        OnRuntimeChanged();
    }

    private async Task DisconnectAllAsync()
    {
        try
        {
            await AppServices.Runtime.DisconnectAsync();
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", "disconnected all nodes from car screen");
        }
        catch (Exception ex)
        {
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", $"failed to disconnect all nodes: {ex.Message}");
        }

        lock (_gate)
        {
            _nowPlaying = null;
        }

        EndCarSession();
        PublishState();
        NotifyChanges();
    }

    // ----- talk (only with "Talk from the car" on; see CarTransmit for the rules) -----

    private async Task TalkAsync(bool fromWheel)
    {
        var log = NodeX.Core.Diagnostics.DiagnosticLog.Default;
        try
        {
            var rt = AppServices.Runtime;

            // The wheel button always talks to the transmit node. A driver pressing it is not looking at the screen,
            // so if the car was showing another (listen-only) node, show the transmit node first; the rule that the
            // car only keys the node it shows still holds.
            string? tx = rt.TransmitNode;
            if (fromWheel && tx != null && tx != _nowPlaying && rt.Connections.Any(c => c.Node == tx && c.Link != null))
            {
                lock (_gate) _nowPlaying = tx;
                BeginCarSession();
            }

            var result = await CarTransmit.ToggleAsync(rt, _nowPlaying, CarSession.MicrophoneUsable);
            string by = fromWheel ? "wheel" : "screen";
            log.Info("car", result.Action == CarTalkAction.Refuse
                ? $"talk refused ({by}): {result.Message}"
                : $"talk {(result.Action == CarTalkAction.Key ? "keyed" : "unkeyed")} ({by}) for node {_nowPlaying}");
            if (result.Action == CarTalkAction.Refuse)
            {
                // A refusal must be heard, not only shown: the driver would otherwise talk to nobody.
                Beep(Tone.PropNack, 300);
                ShowNotice(result.Message);
            }
        }
        catch (Exception ex)
        {
            log.Info("car", $"talk failed: {ex.Message}");
            Beep(Tone.PropNack, 300);
            ShowNotice("Could not talk: " + ex.Message);
        }
        PublishState();
    }

    /// <summary>
    /// While transmitting, any media button (wheel or screen: next, back, play, pause, stop) only stops the
    /// transmission. Returns true when it did, so the button's normal job is skipped.
    /// </summary>
    private bool StopTalkingIfKeyed(string button)
    {
        var rt = AppServices.Runtime;
        if (!CarTransmit.AnyKeyed(rt)) return false;

        NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", $"unkeyed by the {button} button");
        _ = Task.Run(async () =>
        {
            await CarTransmit.UnkeyAllAsync(rt);
            PublishState();
        });
        return true;
    }

    private bool WheelTalks(CarWheelTalk button) =>
        AppServices.Runtime.Config.Settings.CarPtt
        && AppServices.Runtime.Config.Settings.CarPttWheel == button;

    private void HandleMediaButton(CarMediaButton button)
    {
        var action = CarMediaButtons.Decide(button, CarTransmit.AnyKeyed(AppServices.Runtime),
            WheelTalks(CarWheelTalk.Next), WheelTalks(CarWheelTalk.Back));
        switch (action)
        {
            case CarMediaAction.Unkey: StopTalkingIfKeyed(button.ToString()); break;
            case CarMediaAction.Disconnect: _ = StopAsync(); break;
            case CarMediaAction.Talk: _ = TalkAsync(fromWheel: true); break;
            case CarMediaAction.Next: Step(+1); break;
            case CarMediaAction.Previous: Step(-1); break;
            case CarMediaAction.Ignore:
                NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", "play ignored (nothing is dialled automatically)");
                break;
        }
    }

    /// <summary>A car transmission that runs past <see cref="CarTransmit.MaxCarTransmit"/> is released.</summary>
    private void WatchCarKey(bool keyed, PttSource source)
    {
        _carKeyLimit?.Cancel();
        _carKeyLimit = null;
        if (!keyed || source != PttSource.Car) return;

        var cts = new System.Threading.CancellationTokenSource();
        _carKeyLimit = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(CarTransmit.MaxCarTransmit, cts.Token); }
            catch (System.OperationCanceledException) { return; }

            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", "car transmission reached the time limit; unkeyed");
            await CarTransmit.UnkeyCarAsync(AppServices.Runtime);
            ShowNotice("Transmission stopped: time limit.");
        });
    }

    private void ToggleMute()
    {
        string? node = _nowPlaying;
        var rt = AppServices.Runtime;
        var conn = node == null ? null : rt.Connections.FirstOrDefault(c => c.Node == node);
        if (conn == null) return;

        _mutedForFocus.Remove(conn.Node);   // the driver's choice outlasts a navigation prompt
        rt.SetMuted(conn.Node, !conn.Muted);
        PublishState();
    }

    private void TransmitHere()
    {
        string? node = _nowPlaying;
        if (node == null) return;

        bool ok = AppServices.Runtime.SetTransmitNode(node);
        NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", ok
            ? $"transmit node set to {node} from the car screen"
            : $"could not set transmit node {node}: not linked");
        if (!ok) ShowNotice($"Node {node} is not connected.");
        PublishState();
        NotifyChanges();
    }

    private void ShowNotice(string message)
    {
        _notice = message;
        PublishState();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (_notice == message) { _notice = null; PublishState(); }
        });
    }

    private void Hook(NodeLink link)
    {
        if (_hooks.ContainsKey(link)) return;
        Action<bool, PttSource> handler = (key, source) =>
        {
            // The audible key/unkey indication, only for the car's talk feature.
            if (CarSession.Active && AppServices.Runtime.Config.Settings.CarPtt)
                Beep(key ? Tone.PropBeep : Tone.PropAck, key ? 150 : 250);
            WatchCarKey(key, source);
            PublishState();
        };
        _hooks[link] = handler;
        link.Ptt.PttStateChanged += handler;
        _rx.Attach(link);
    }

    private void Unhook(NodeLink link)
    {
        _rx.Detach(link.Target.NodeNumber);
        if (_hooks.Remove(link, out var handler)) link.Ptt.PttStateChanged -= handler;
    }

    /// <summary>The names of the nodes heard right now ("East Coast Reflector, 2000"), or empty.</summary>
    private string TalkersText()
    {
        var settings = AppServices.Runtime.Config.Settings;
        return string.Join(", ", _rx.Talking().Select(n => CarBrowse.TitleFor(settings, n)));
    }

    private void RefreshTalkers()
    {
        try
        {
            if (_nowPlaying == null) return;
            string now = TalkersText();
            long tick = System.Environment.TickCount64;
            if (now == _talkers || tick - _talkersShownAt < 2000) return;

            _talkers = now;
            _talkersShownAt = tick;
            PublishState();
        }
        catch (Exception) { /* a display nicety only */ }
    }

    private void Beep(Tone tone, int ms)
    {
        try
        {
            _tone ??= new ToneGenerator(global::Android.Media.Stream.Music, 70);
            _tone.StartTone(tone, ms);
        }
        catch (Exception) { /* the on-screen state still shows it */ }
    }

    /// <summary>
    /// Next/Back step the car's now-playing screen through the nodes that are already connected. They never dial a
    /// favourite, hang anything up or move the transmit node, so a stray press on the wheel is harmless.
    /// </summary>
    private void Step(int direction)
    {
        var connected = AppServices.Runtime.Connections.Select(c => c.Node).ToList();
        string? next = CarBrowse.Neighbor(connected, _nowPlaying, direction);
        if (next == null || next == _nowPlaying) return;

        lock (_gate) _nowPlaying = next;
        BeginCarSession();
        PublishState();
        NotifyChanges();
    }

    private void PlaySearch(string? query)
    {
        var settings = AppServices.Runtime.Config.Settings;
        string? node = string.IsNullOrWhiteSpace(query)
            ? LastRemembered(settings)
            : CarBrowse.FindFavorite(settings, query);

        if (node == null)
        {
            Fail(string.IsNullOrWhiteSpace(query) ? "No favourite to play yet." : $"No favourite matches \"{query}\".");
            return;
        }
        _ = PlayAsync(node);
    }

    // ----- state -----

    private void OnRuntimeChanged()
    {
        var rt = AppServices.Runtime;
        var conns = rt.Connections;
        bool adopted = false;

        lock (_gate)
        {
            if (_nowPlaying == null)
            {
                // A link was already up when the car connected: show it (the transmit/first link), as the phone's.
                string? adopt = rt.TransmitNode ?? conns.FirstOrDefault()?.Node;
                if (adopt != null) { _nowPlaying = adopt; adopted = true; }
            }
            else if (!conns.Any(c => c.Node == _nowPlaying) && _error == null)
            {
                // It was hung up (here, on the phone, or recovery gave up). Adopt remaining if any.
                _nowPlaying = rt.TransmitNode ?? conns.FirstOrDefault()?.Node;
            }
        }

        if (_nowPlaying == null) EndCarSession();
        // In a car, a link adopted from the phone becomes the car's audio like one picked on the car screen, so the
        // wheel buttons are sent to NodeX.
        else if (adopted && CarSession.Connected) BeginCarSession();
        PublishState();
        NotifyChanges();
    }

    private void PublishState()
    {
        var session = _session;
        if (session == null) return;

        var rt = AppServices.Runtime;
        string? node = _nowPlaying;
        var conn = node == null ? null : rt.Connections.FirstOrDefault(c => c.Node == node);
        int activeCount = rt.Connections.Count(c => c.Link != null);
        bool isTx = node != null && node == rt.TransmitNode;

        PlaybackStateCode code;
        string subtitle;
        if (_error != null)
        {
            code = PlaybackStateCode.Error;
            subtitle = _error;
        }
        else if (node == null)
        {
            code = PlaybackStateCode.Stopped;
            subtitle = !rt.Config.IsComplete ? "Finish setup on your phone" : "Pick a favourite";
        }
        else if (conn?.Link?.Session.State == Iax2SessionState.Connected)
        {
            code = PlaybackStateCode.Playing;
            subtitle = CarBrowse.StatusLine(isTx, connected: true, reconnecting: false, hearing: false, conn.Muted);
            if (activeCount > 1) subtitle += $" ({activeCount} links)";
        }
        else
        {
            code = PlaybackStateCode.Buffering;
            subtitle = conn?.Reconnect.Active == true ? "Reconnecting…" : "Connecting…";
        }

        // Android Auto's required default set includes Pause; for a live radio link Pause simply hangs up.
        var actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionStop | PlaybackState.ActionPlayFromMediaId
                      | PlaybackState.ActionPlayFromSearch | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious;

        bool keyed = CarTransmit.AnyKeyed(rt);
        // Who is talking: the last set RefreshTalkers decided to show (not recomputed here, so it cannot flicker).
        if (code == PlaybackStateCode.Playing && _talkers.Length > 0)
            subtitle = (isTx ? "TRANSMIT HERE · " : "Listen only · ") + "Hearing " + _talkers
                       + (activeCount > 1 ? $" ({activeCount} links)" : "");
        if (keyed && code == PlaybackStateCode.Playing) subtitle = "TRANSMITTING";
        if (_notice != null && code != PlaybackStateCode.Error) subtitle = _notice;

        var builder = new PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(code, PlaybackState.PlaybackPositionUnknown, 1f)!;

        // The node's buttons, in the order of the phone's card. Talk comes first so it gets the most prominent slot.
        if (node != null && _error == null && conn != null)
        {
            if (isTx || keyed)
            {
                // Only with "Talk from the car" on; "Stop talking" is offered whenever something is keyed.
                if (rt.Config.Settings.CarPtt || keyed)
                {
                    builder.AddCustomAction(new PlaybackState.CustomAction.Builder(
                        TalkAction,
                        keyed ? "Stop talking" : "Talk",
                        keyed ? Resource.Drawable.ic_car_talk_stop : Resource.Drawable.ic_car_talk).Build());
                }
            }
            else if (conn.Link != null)
            {
                builder.AddCustomAction(new PlaybackState.CustomAction.Builder(
                    TransmitHereAction, "Transmit here", Resource.Drawable.ic_car_tx_here).Build());
            }

            builder.AddCustomAction(new PlaybackState.CustomAction.Builder(
                MuteAction,
                conn.Muted ? "Unmute" : "Mute",
                conn.Muted ? Resource.Drawable.ic_car_unmute : Resource.Drawable.ic_car_mute).Build());

            builder.AddCustomAction(new PlaybackState.CustomAction.Builder(
                DisconnectAction, "Disconnect", Resource.Drawable.ic_car_disconnect).Build());
        }
#pragma warning disable CA1422 // the string overload works on every API level we support
        if (_error != null) builder.SetErrorMessage(_error);
#pragma warning restore CA1422
        session.SetPlaybackState(builder.Build());

        if (node != null)
        {
            session.SetMetadata(new MediaMetadata.Builder()
                .PutString(MediaMetadata.MetadataKeyMediaId, node)!
                .PutString(MediaMetadata.MetadataKeyTitle, CarBrowse.CardTitle(rt.Config.Settings, node))!
                .PutString(MediaMetadata.MetadataKeyDisplaySubtitle, subtitle)!
                .Build());
        }
        else if (LastRemembered(rt.Config.Settings) is { } lastFav && !string.IsNullOrWhiteSpace(lastFav))
        {
            // EP-2 state restoration: show the last remembered station on the car's media card in Stopped state
            // so the driver sees the station context immediately without auto-dialling.
            session.SetMetadata(new MediaMetadata.Builder()
                .PutString(MediaMetadata.MetadataKeyMediaId, lastFav)!
                .PutString(MediaMetadata.MetadataKeyTitle, CarBrowse.CardTitle(rt.Config.Settings, lastFav))!
                .PutString(MediaMetadata.MetadataKeyDisplaySubtitle, subtitle)!
                .Build());
        }
        else
        {
            session.SetMetadata(null);
        }
    }

    private void Fail(string message)
    {
        lock (_gate) _error = message;
        EndCarSession();
        PublishState();
        lock (_gate) { _nowPlaying = null; _error = null; }
    }

    // ----- audio session -----

    private void BeginCarSession()
    {
        CarSession.Selected = true;
        if (GetSystemService(AudioService) is not AudioManager am || _focus != null) return;

        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!
            .Build()!;
        _focus = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
            .SetAudioAttributes(attributes)!
            .SetOnAudioFocusChangeListener(this)!
            .SetWillPauseWhenDucked(false)!
            .Build()!;
        am.RequestAudioFocus(_focus);
    }

    private void EndCarSession()
    {
        _ = CarTransmit.UnkeyCarAsync(AppServices.Runtime);
        RestoreMutes();
        if (_focus != null && GetSystemService(AudioService) is AudioManager am)
            am.AbandonAudioFocusRequest(_focus);
        _focus = null;
        CarSession.Selected = false;
    }

    /// <summary>
    /// Navigation prompt or phone call: silence the links (they stay up, so playback resumes without redialling).
    /// Permanent loss (another media app took over): hang up the car's node.
    /// </summary>
    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        var rt = AppServices.Runtime;

        // A navigation prompt, a call or another app taking the audio always ends a car transmission first.
        if (focusChange != AudioFocus.Gain) _ = CarTransmit.UnkeyCarAsync(rt);

        switch (focusChange)
        {
            case AudioFocus.LossTransient:
            case AudioFocus.LossTransientCanDuck:
                foreach (var c in rt.Connections.Where(c => !c.Muted))
                {
                    _mutedForFocus.Add(c.Node);
                    rt.SetMuted(c.Node, true);
                }
                break;

            case AudioFocus.Gain:
                RestoreMutes();
                break;

            case AudioFocus.Loss:
                _ = StopAsync();
                break;
        }
    }

    private void RestoreMutes()
    {
        var rt = AppServices.Runtime;
        foreach (string node in _mutedForFocus.ToArray())
            rt.SetMuted(node, false);
        _mutedForFocus.Clear();
    }

    // ----- last played favourite (for "play NodeX" with no name) -----

    private void Remember(string node)
    {
        try { GetSharedPreferences("nodex_prefs", FileCreationMode.Private)?.Edit()?.PutString(LastNodePref, node)?.Apply(); }
        catch (Exception) { /* a convenience only */ }
    }

    private string? LastRemembered(NodeX.Core.Settings.NodeSettings settings)
    {
        string? last = GetSharedPreferences("nodex_prefs", FileCreationMode.Private)?.GetString(LastNodePref, null);
        if (last != null && settings.FavoriteNodes.Contains(last)) return last;
        return settings.FavoriteNodes.FirstOrDefault();
    }

    private sealed class Callback(NodeXMediaBrowserService owner) : MediaSession.Callback
    {
        public override void OnPlayFromMediaId(string? mediaId, Bundle? extras)
        {
            if (string.IsNullOrEmpty(mediaId)) return;

            if (mediaId == CarBrowse.DisconnectAllId)
            {
                _ = owner.DisconnectAllAsync();
            }
            else if (mediaId.StartsWith(CarBrowse.DisconnectPrefix))
            {
                string node = mediaId[CarBrowse.DisconnectPrefix.Length..];
                _ = owner.DisconnectNodeAsync(node);
            }
            else if (mediaId != CarBrowse.NoFavoritesId && mediaId != CarBrowse.NoActiveId
                     && mediaId != CarBrowse.ActiveFolderId && mediaId != CarBrowse.FavoritesFolderId)
            {
                _ = owner.PlayAsync(mediaId);
            }
        }

        // While transmitting, every one of these only stops the transmission (see StopTalkingIfKeyed).
        public override void OnPlay() => owner.HandleMediaButton(CarMediaButton.Play);

        public override void OnPlayFromSearch(string? query, Bundle? extras) => owner.PlaySearch(query);

        public override void OnStop() => owner.HandleMediaButton(CarMediaButton.Stop);

        public override void OnPause() => owner.HandleMediaButton(CarMediaButton.Pause);

        public override void OnCustomAction(string? action, Bundle? extras)
        {
            switch (action)
            {
                case TalkAction:
                    _ = owner.TalkAsync(fromWheel: false);
                    break;
                case MuteAction:
                    owner.ToggleMute();
                    break;
                case TransmitHereAction:
                    owner.TransmitHere();
                    break;
                case DisconnectAction:
                    if (owner._nowPlaying is { } node) _ = owner.DisconnectNodeAsync(node);
                    break;
            }
        }

        // The wheel button chosen in Settings talks; the other steps through the connected nodes.
        public override void OnSkipToNext() => owner.HandleMediaButton(CarMediaButton.Next);

        public override void OnSkipToPrevious() => owner.HandleMediaButton(CarMediaButton.Previous);
    }
}
