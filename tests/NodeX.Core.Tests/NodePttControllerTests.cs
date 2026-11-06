using NodeX.Core;

namespace NodeX.Core.Tests;

public class NodePttControllerTests
{
    /// <summary>Minimal in-memory Bluetooth PTT source for driving hardware events.</summary>
    private sealed class FakeBluetoothPtt : IBluetoothPttService
    {
        public bool IsConnected => true;
        public string? DeviceName => "Fake";
        public event Action<bool>? PttStateChanged;
        public event Action<bool, string>? ConnectionStateChanged { add { } remove { } }
        public event Action<int>? BatteryLevelReceived { add { } remove { } }
        public void Raise(bool pressed) => PttStateChanged?.Invoke(pressed);
        public bool HasSubscribers => PttStateChanged != null;
        public Task StartScanningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    // A disconnected session: SetPttAsync is a no-op, so no network traffic is produced.
    private static Iax2Session NewSession() => new();

    private static async Task<T> WithTimeout<T>(Task<T> task, int ms = 3000)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms));
        Assert.Same(task, done);
        return await task;
    }

    [Fact]
    public void Defaults()
    {
        using var s = NewSession();
        using var c = new NodePttController(s);
        Assert.False(c.IsPttActive);
        Assert.Equal(PttSource.None, c.CurrentSource);
        Assert.Equal(PttMode.Momentary, c.Mode);
        Assert.Equal(TimeSpan.FromSeconds(180), c.TimeOutTimerLimit);
        Assert.Equal(TimeSpan.Zero, c.CurrentTxDuration);
    }

    [Fact]
    public void Constructor_RejectsNullSession()
    {
        Assert.Throws<ArgumentNullException>(() => new NodePttController(null!));
    }

    [Fact]
    public async Task Momentary_PressKeys_ReleaseUnkeys()
    {
        using var s = NewSession();
        using var c = new NodePttController(s);
        var events = new List<(bool, PttSource)>();
        c.PttStateChanged += (k, src) => events.Add((k, src));

        await c.HandleUiPttAsync(true);
        Assert.True(c.IsPttActive);
        Assert.Equal(PttSource.UiButton, c.CurrentSource);

        await c.HandleUiPttAsync(false);
        Assert.False(c.IsPttActive);
        Assert.Equal(PttSource.None, c.CurrentSource);

        Assert.Equal([(true, PttSource.UiButton), (false, PttSource.UiButton)], events);
    }

    [Fact]
    public async Task Momentary_RepeatedPress_IsIdempotent()
    {
        using var s = NewSession();
        using var c = new NodePttController(s);
        int count = 0;
        c.PttStateChanged += (_, _) => count++;

        await c.HandleUiPttAsync(true);
        await c.HandleUiPttAsync(true);
        await c.HandleUiPttAsync(false);
        await c.HandleUiPttAsync(false);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Toggle_PressLatches_ReleaseIgnored_SecondPressUnkeys()
    {
        using var s = NewSession();
        using var c = new NodePttController(s) { Mode = PttMode.Toggle };

        await c.HandleUiPttAsync(true);
        Assert.True(c.IsPttActive);

        await c.HandleUiPttAsync(false); // release is ignored in toggle mode
        Assert.True(c.IsPttActive);

        await c.HandleUiPttAsync(true);
        Assert.False(c.IsPttActive);
    }

    [Fact]
    public async Task MediaKey_AlwaysToggles_AndAttributesSource()
    {
        using var s = NewSession();
        using var c = new NodePttController(s); // Momentary mode still toggles for media keys

        await c.HandleMediaKeyPttAsync();
        Assert.True(c.IsPttActive);
        Assert.Equal(PttSource.MediaKey, c.CurrentSource);

        await c.HandleMediaKeyPttAsync();
        Assert.False(c.IsPttActive);
    }

    [Fact]
    public async Task Vox_OnlyActsInMomentaryMode()
    {
        using var s = NewSession();
        using var c = new NodePttController(s);

        await c.HandleVoxStateAsync(true);
        Assert.True(c.IsPttActive);
        Assert.Equal(PttSource.Vox, c.CurrentSource);
        await c.HandleVoxStateAsync(false);
        Assert.False(c.IsPttActive);

        c.Mode = PttMode.Toggle;
        await c.HandleVoxStateAsync(true);
        Assert.False(c.IsPttActive);
    }

    [Fact]
    public async Task SourceAttribution_KeepsOriginalKeyingSource()
    {
        using var s = NewSession();
        using var c = new NodePttController(s);

        await c.SetPttStateAsync(true, PttSource.BluetoothBle);
        // A second "key" from another source does not steal attribution.
        await c.SetPttStateAsync(true, PttSource.UiButton);
        Assert.Equal(PttSource.BluetoothBle, c.CurrentSource);
    }

    [Fact]
    public async Task CurrentTxDuration_AdvancesWhileKeyed_AndResetsOnUnkey()
    {
        using var s = NewSession();
        using var c = new NodePttController(s);

        await c.HandleUiPttAsync(true);
        await Task.Delay(60);
        Assert.True(c.CurrentTxDuration >= TimeSpan.FromMilliseconds(40));

        await c.HandleUiPttAsync(false);
        Assert.Equal(TimeSpan.Zero, c.CurrentTxDuration);
    }

    [Fact]
    public async Task TimeOutTimer_ForcesUnkey_AndRaisesEvents()
    {
        using var s = NewSession();
        using var c = new NodePttController(s) { TimeOutTimerLimit = TimeSpan.FromMilliseconds(100) };

        var totFired = new TaskCompletionSource();
        var unkeyed = new TaskCompletionSource<PttSource>();
        string? warning = null;
        c.TimeOutTimerExceeded += () => totFired.TrySetResult();
        c.WarningLogged += w => warning = w;
        c.PttStateChanged += (k, src) => { if (!k) unkeyed.TrySetResult(src); };

        await c.HandleUiPttAsync(true);

        await WithTimeout(totFired.Task.ContinueWith(_ => true));
        Assert.Equal(PttSource.External, await WithTimeout(unkeyed.Task));
        Assert.False(c.IsPttActive);
        Assert.Contains("Time-Out Timer", warning);
    }

    [Fact]
    public async Task TimeOutTimer_IsCancelledByNormalUnkey()
    {
        using var s = NewSession();
        using var c = new NodePttController(s) { TimeOutTimerLimit = TimeSpan.FromMilliseconds(150) };
        bool totFired = false;
        c.TimeOutTimerExceeded += () => totFired = true;

        await c.HandleUiPttAsync(true);
        await c.HandleUiPttAsync(false);
        await Task.Delay(300);

        Assert.False(totFired);
    }

    [Fact]
    public async Task TimeOutTimer_RestartsOnEachNewOver()
    {
        using var s = NewSession();
        using var c = new NodePttController(s) { TimeOutTimerLimit = TimeSpan.FromMilliseconds(250) };
        bool totFired = false;
        c.TimeOutTimerExceeded += () => totFired = true;

        await c.HandleUiPttAsync(true);
        await Task.Delay(150);
        await c.HandleUiPttAsync(false);
        await c.HandleUiPttAsync(true);   // new over: TOT restarts from zero
        await Task.Delay(150);            // 300ms since first key, 150ms since re-key

        Assert.False(totFired);
        Assert.True(c.IsPttActive);
        await c.HandleUiPttAsync(false);
    }

    [Fact]
    public async Task Bluetooth_Momentary_PressAndRelease()
    {
        using var s = NewSession();
        var bt = new FakeBluetoothPtt();
        using var c = new NodePttController(s, bt);

        var keyed = new TaskCompletionSource<PttSource>();
        var unkeyed = new TaskCompletionSource();
        c.PttStateChanged += (k, src) =>
        {
            if (k) keyed.TrySetResult(src); else unkeyed.TrySetResult();
        };

        bt.Raise(true);
        Assert.Equal(PttSource.BluetoothBle, await WithTimeout(keyed.Task));
        bt.Raise(false);
        await WithTimeout(unkeyed.Task.ContinueWith(_ => true));
        Assert.False(c.IsPttActive);
    }

    [Fact]
    public async Task Bluetooth_Toggle_IgnoresRelease()
    {
        using var s = NewSession();
        var bt = new FakeBluetoothPtt();
        using var c = new NodePttController(s, bt) { Mode = PttMode.Toggle };

        var keyed = new TaskCompletionSource();
        c.PttStateChanged += (k, _) => { if (k) keyed.TrySetResult(); };

        bt.Raise(true);
        await WithTimeout(keyed.Task.ContinueWith(_ => true));
        bt.Raise(false);
        await Task.Delay(100);
        Assert.True(c.IsPttActive);
    }

    [Fact]
    public async Task Dispose_UnsubscribesBluetooth_AndUnkeys()
    {
        using var s = NewSession();
        var bt = new FakeBluetoothPtt();
        var c = new NodePttController(s, bt);
        Assert.True(bt.HasSubscribers);

        await c.HandleUiPttAsync(true);
        await c.DisposeAsync();

        Assert.False(bt.HasSubscribers);
        Assert.False(c.IsPttActive);
        await c.DisposeAsync(); // idempotent
    }
}
