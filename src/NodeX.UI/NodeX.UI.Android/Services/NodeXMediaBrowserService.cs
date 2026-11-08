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
/// Android Auto (and Google Assistant) entry point: a media app. The favourites are the browse list; selecting one
/// dials it, Stop hangs it up. It is listen-only unless the user turned on "Talk from the car" in Settings; then a
/// Talk button (and optionally the wheel play/pause button) toggles the transmit node through <see cref="CarTransmit"/>,
/// which enforces the safety rules. The car never changes the transmit node, and every car key is released when the
/// car session ends, audio focus is lost or this service is destroyed.
/// </summary>
[Service(Name = "com.kv4s.nodex.NodeXMediaBrowserService", Exported = true, Label = "NodeX")]
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
    private bool _carStarted;             // we (not the phone) dialled _nowPlaying
    private string? _error;
    private readonly HashSet<string> _mutedForFocus = [];
    private Action? _changed;
    private readonly object _gate = new();

    private const string TalkAction = "nodex.talk";
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

    private static async Task StartRuntimeAsync()
    {
        try { await AppServices.Runtime.StartAsync(); }
        catch (Exception ex) { global::Android.Util.Log.Warn("NodeX", $"Car start failed: {ex.Message}"); }
    }

    // ----- browsing -----

    public override BrowserRoot? OnGetRoot(string clientPackageName, int clientUid, Bundle? rootHints)
    {
        if (!IsAllowedCaller(clientPackageName, clientUid)) return null;
        return new BrowserRoot(RootId, null);
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
                    foreach (var item in CarBrowse.BuildItems(rt.Config.Settings))
                        items.Add(ToMediaItem(item));
                }
                result.SendResult(items);
            }
            catch (Exception)
            {
                result.SendResult(new JavaList<MediaBrowser.MediaItem>());
            }
        });
    }

    private static MediaBrowser.MediaItem ToMediaItem(CarBrowseItem item)
    {
        var description = new MediaDescription.Builder()
            .SetMediaId(item.MediaId)!
            .SetTitle(item.Title)!
            .SetSubtitle(item.Playable ? "Node " + item.MediaId : null)!
            .Build();
        return new MediaBrowser.MediaItem(description, item.Playable ? MediaItemFlags.Playable : MediaItemFlags.Browsable);
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

            string? previous = _carStarted ? _nowPlaying : null;
            bool alreadyUp = rt.Connections.Any(c => c.Node == node && c.Link != null);

            lock (_gate)
            {
                _error = null;
                _nowPlaying = node;
                _carStarted = true;
            }
            BeginCarSession();
            Remember(node);
            PublishState();

            if (!alreadyUp)
            {
                var info = await rt.ConnectAsync(node);
                if (info == null)
                {
                    Fail($"Could not find node {node}.");
                    return;
                }
            }

            // The new node is up; now drop the car's previous one. Links started on the phone are left alone.
            string? replace = CarBrowse.NodeToReplace(previous, node);
            if (replace != null) await rt.DisconnectAsync(replace);
        }
        catch (Exception ex)
        {
            Fail(ex is InvalidOperationException or ArgumentException ? ex.Message : $"Could not connect: {ex.Message}");
        }
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
    }

    // ----- talk (only with "Talk from the car" on; see CarTransmit for the rules) -----

    private async Task TalkAsync()
    {
        try
        {
            var result = await CarTransmit.ToggleAsync(AppServices.Runtime, _nowPlaying, CarSession.MicrophoneUsable);
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", result.Action == CarTalkAction.Refuse
                ? $"talk refused: {result.Message}"
                : $"talk {(result.Action == CarTalkAction.Key ? "keyed" : "unkeyed")} for node {_nowPlaying}");
            if (result.Action == CarTalkAction.Refuse) ShowNotice(result.Message);
        }
        catch (Exception ex)
        {
            ShowNotice("Could not talk: " + ex.Message);
        }
        PublishState();
    }

    private bool WheelTalks(CarWheelTalk button) =>
        AppServices.Runtime.Config.Settings.CarPtt
        && AppServices.Runtime.Config.Settings.CarPttWheel == button
        && _nowPlaying != null;

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
            if (CarSession.Active && AppServices.Runtime.Config.Settings.CarPtt) Beep(key);
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

    private void Beep(bool keyed)
    {
        try
        {
            _tone ??= new ToneGenerator(global::Android.Media.Stream.Music, 70);
            _tone.StartTone(keyed ? Tone.PropBeep : Tone.PropAck, keyed ? 150 : 250);
        }
        catch (Exception) { /* the on-screen state still shows it */ }
    }

    private void Step(int direction)
    {
        var settings = AppServices.Runtime.Config.Settings;
        string? next = CarBrowse.Neighbor(settings.FavoriteNodes, _nowPlaying, direction);
        if (next != null) _ = PlayAsync(next);
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

        lock (_gate)
        {
            if (_nowPlaying == null)
            {
                // A link was already up when the car connected: show it (the transmit/first link), as the phone's.
                string? adopt = rt.TransmitNode ?? conns.FirstOrDefault()?.Node;
                if (adopt != null) { _nowPlaying = adopt; _carStarted = false; }
            }
            else if (!conns.Any(c => c.Node == _nowPlaying) && _error == null)
            {
                // It was hung up (here, on the phone, or recovery gave up).
                _nowPlaying = null;
                _carStarted = false;
            }
        }

        if (_nowPlaying == null) EndCarSession();
        PublishState();
    }

    private void PublishState()
    {
        var session = _session;
        if (session == null) return;

        var rt = AppServices.Runtime;
        string? node = _nowPlaying;
        var conn = node == null ? null : rt.Connections.FirstOrDefault(c => c.Node == node);

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
            subtitle = "Connected";
        }
        else
        {
            code = PlaybackStateCode.Buffering;
            subtitle = conn?.Reconnect.Active == true ? "Reconnecting…" : "Connecting…";
        }

        // Android Auto's required default set includes Pause; for a live radio link Pause simply hangs up.
        var actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionStop | PlaybackState.ActionPlayFromMediaId
                      | PlaybackState.ActionPlayFromSearch | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious;

        bool keyed = rt.Link?.Ptt.IsPttActive == true;
        // Who is talking: the last set RefreshTalkers decided to show (not recomputed here, so it cannot flicker).
        if (code == PlaybackStateCode.Playing && _talkers.Length > 0) subtitle = "Hearing " + _talkers;
        if (keyed && code == PlaybackStateCode.Playing) subtitle = "TRANSMITTING";
        if (_notice != null && code != PlaybackStateCode.Error) subtitle = _notice;

        var builder = new PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(code, PlaybackState.PlaybackPositionUnknown, 1f)!;

        // The Talk button, only when the user turned "Talk from the car" on and a node is showing.
        if (node != null && _error == null && rt.Config.Settings.CarPtt)
        {
            builder.AddCustomAction(new PlaybackState.CustomAction.Builder(
                TalkAction,
                keyed ? "Stop talking" : "Talk",
                keyed ? Resource.Drawable.ic_car_talk_stop : Resource.Drawable.ic_car_talk).Build());
        }
#pragma warning disable CA1422 // the string overload works on every API level we support
        if (_error != null) builder.SetErrorMessage(_error);
#pragma warning restore CA1422
        session.SetPlaybackState(builder.Build());

        if (node != null)
        {
            session.SetMetadata(new MediaMetadata.Builder()
                .PutString(MediaMetadata.MetadataKeyMediaId, node)!
                .PutString(MediaMetadata.MetadataKeyTitle, CarBrowse.TitleFor(rt.Config.Settings, node))!
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
        lock (_gate) { _nowPlaying = null; _carStarted = false; _error = null; }
    }

    // ----- audio session -----

    private void BeginCarSession()
    {
        CarSession.Active = true;
        if (GetSystemService(AudioService) is not AudioManager am || _focus != null) return;

        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!
            .Build()!;
        _focus = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
            .SetAudioAttributes(attributes)!
            .SetOnAudioFocusChangeListener(this)!
            .SetWillPauseWhenDucked(false)!
            .Build();
        am.RequestAudioFocus(_focus);
    }

    private void EndCarSession()
    {
        _ = CarTransmit.UnkeyCarAsync(AppServices.Runtime);
        RestoreMutes();
        if (_focus != null && GetSystemService(AudioService) is AudioManager am)
            am.AbandonAudioFocusRequest(_focus);
        _focus = null;
        CarSession.Active = false;
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
            if (!string.IsNullOrEmpty(mediaId) && mediaId != CarBrowse.NoFavoritesId)
                _ = owner.PlayAsync(mediaId);
        }

        public override void OnPlay() => owner.PlaySearch(null);
        public override void OnPlayFromSearch(string? query, Bundle? extras) => owner.PlaySearch(query);
        public override void OnStop() => _ = owner.StopAsync();
        public override void OnPause() => _ = owner.StopAsync();   // a radio link has nothing to pause

        public override void OnCustomAction(string? action, Bundle? extras)
        {
            if (action == TalkAction) _ = owner.TalkAsync();
        }

        // The wheel button chosen in Settings talks; the other still steps through favourites.
        public override void OnSkipToNext()
        {
            if (owner.WheelTalks(CarWheelTalk.Next)) _ = owner.TalkAsync();
            else owner.Step(+1);
        }

        public override void OnSkipToPrevious()
        {
            if (owner.WheelTalks(CarWheelTalk.Back)) _ = owner.TalkAsync();
            else owner.Step(-1);
        }
    }
}
