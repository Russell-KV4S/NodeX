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

namespace NodeX.UI.Android;

/// <summary>
/// Android Auto (and Google Assistant) entry point: a listen-only media app. The favourites are the browse list;
/// selecting one dials it, Stop hangs it up. Nothing reachable from here can key the transmitter: this class never
/// touches <c>NodePttController</c>, <c>HardwarePtt</c> or the transmit choice, and it does not override the media
/// button handling (so a steering-wheel button can only play, stop, skip).
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

        // Cold start: Android Auto can bind this service with no Activity, so the runtime is started here too.
        _ = StartRuntimeAsync();
    }

    public override void OnDestroy()
    {
        var rt = AppServices.Runtime;
        if (_changed != null) rt.ConnectionsChanged -= _changed;
        _changed = null;

        EndCarSession();
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

        var builder = new PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(code, PlaybackState.PlaybackPositionUnknown, 1f)!;
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
        public override void OnSkipToNext() => owner.Step(+1);
        public override void OnSkipToPrevious() => owner.Step(-1);
    }
}
