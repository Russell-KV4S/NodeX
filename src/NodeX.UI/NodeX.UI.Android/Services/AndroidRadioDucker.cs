using System;
using System.Linq;
using Android.Content;
using Android.Media;
using NodeX.Core;

namespace NodeX.UI.Android;

/// <summary>
/// "Hear nodes over the car radio" (<see cref="CarRadioMix"/>): while it applies, holds navigation-style
/// "may duck" audio focus whenever an unmuted node is heard, so the car lowers its radio and plays the node's voice
/// over it (node audio is on the navigation stream then, see <c>AndroidAudioDeviceService</c>). Lives in the
/// foreground service, which runs whenever anything is linked, because the car media service is only bound while
/// Android Auto shows NodeX. Not tried in a car.
/// </summary>
public sealed class AndroidRadioDucker : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
{
    private readonly Context _context;
    private readonly RxActivity _rx = new();
    private readonly RadioDuckGate _gate = new();
    private readonly System.Threading.Timer _timer;
    private readonly Action<NodeLink> _added;
    private readonly Action<NodeLink> _removed;
    private AudioFocusRequestClass? _request;
    private bool _disposed;

    public AndroidRadioDucker(Context context)
    {
        _context = context;
        var rt = AppServices.Runtime;
        _added = link => _rx.Attach(link);
        _removed = link => _rx.Detach(link.Target.NodeNumber);
        rt.LinkAdded += _added;
        rt.LinkRemoved += _removed;
        foreach (var link in rt.Links) _rx.Attach(link);
        // 100 ms so the radio dips within a word of a node starting to talk.
        _timer = new System.Threading.Timer(_ => Tick(), null, 100, 100);
    }

    private void Tick()
    {
        try
        {
            if (_disposed) return;
            bool applies = CarSession.RadioMixApplies;
            bool heard = false;
            if (applies)
            {
                var muted = AppServices.Runtime.Connections.Where(c => c.Muted).Select(c => c.Node).ToHashSet();
                heard = _rx.Talking().Any(n => !muted.Contains(n));
            }
            if (!_gate.Update(applies, heard, Environment.TickCount64)) return;
            if (_gate.Holding) Duck(); else Release();
        }
        catch (Exception ex)
        {
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("car", $"radio mix tick failed: {ex.Message}");
        }
    }

    private void Duck()
    {
        if (_request != null || _context.GetSystemService(Context.AudioService) is not AudioManager am) return;
        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.AssistanceNavigationGuidance)!
            .SetContentType(AudioContentType.Speech)!
            .Build()!;
        _request = new AudioFocusRequestClass.Builder(AudioFocus.GainTransientMayDuck)
            .SetAudioAttributes(attributes)!
            .SetOnAudioFocusChangeListener(this)!
            .Build()!;
        var result = am.RequestAudioFocus(_request);
        NodeX.Core.Diagnostics.DiagnosticLog.Default.Detail("car", $"radio mix: node talking, ducking the car audio ({result})");
    }

    private void Release()
    {
        if (_request == null) return;
        if (_context.GetSystemService(Context.AudioService) is AudioManager am) am.AbandonAudioFocusRequest(_request);
        _request = null;
        NodeX.Core.Diagnostics.DiagnosticLog.Default.Detail("car", "radio mix: quiet, car audio back up");
    }

    public void OnAudioFocusChange(AudioFocus focusChange) =>
        NodeX.Core.Diagnostics.DiagnosticLog.Default.Detail("car", $"radio mix focus changed: {focusChange}");

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Dispose();
            var rt = AppServices.Runtime;
            rt.LinkAdded -= _added;
            rt.LinkRemoved -= _removed;
            _rx.Dispose();
            Release();
        }
        base.Dispose(disposing);
    }
}
