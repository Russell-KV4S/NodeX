using NodeX.Core;

namespace NodeX.Core.Tests;

public class BlePttTests
{
    private const string Char1 = "0000ffe1-0000-1000-8000-00805f9b34fb";
    private const string Char2 = "0000ffe2-0000-1000-8000-00805f9b34fb";

    private sealed class FakeTransport : IBlePttTransport
    {
        public bool IsSupported { get; set; } = true;
        public bool IsConnected { get; private set; }
        public string? ConnectedAddress { get; private set; }
        public int ConnectCalls;
        public bool FailConnect;
        public event Action<BleDeviceInfo>? DeviceFound { add { } remove { } }
        public event Action<string, byte[]>? Notification;
        public event Action<bool, string>? ConnectionChanged;

        public Task StartScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopScanAsync() => Task.CompletedTask;

        public Task ConnectAsync(string address, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            if (FailConnect) throw new InvalidOperationException("nope");
            IsConnected = true;
            ConnectedAddress = address;
            ConnectionChanged?.Invoke(true, "Button");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            if (!IsConnected) return Task.CompletedTask;
            IsConnected = false;
            ConnectionChanged?.Invoke(false, "Button");
            return Task.CompletedTask;
        }

        public void Send(string characteristic, params byte[] data) => Notification?.Invoke(characteristic, data);
        public void Drop() => DisconnectAsync().GetAwaiter().GetResult();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly BlePttProfile Profile = new("AA:BB", "Button", Char1, "01", "00");

    // ----- learner -----

    [Fact]
    public void Learner_RecordsPressThenRelease()
    {
        var l = new BlePttLearner();
        l.Begin("AA:BB", "Button");
        Assert.Equal(BleLearnStep.WaitPress, l.Step);

        l.OnNotification(Char1, [0x01]);
        Assert.Equal(BleLearnStep.WaitRelease, l.Step);

        l.OnNotification(Char1, [0x00]);
        Assert.Equal(BleLearnStep.Done, l.Step);
        Assert.Equal(new BlePttProfile("AA:BB", "Button", Char1, "01", "00"), l.Result);
    }

    [Fact]
    public void Learner_IgnoresRepeatedPressAndOtherCharacteristics()
    {
        var l = new BlePttLearner();
        l.Begin("AA:BB", "Button");
        l.OnNotification(Char1, [0x01]);

        l.OnNotification(Char1, [0x01]);   // button repeats while held
        l.OnNotification(Char2, [0x05]);   // noise from another characteristic
        Assert.Equal(BleLearnStep.WaitRelease, l.Step);

        l.OnNotification(Char1, [0x02]);
        Assert.Equal(BleLearnStep.Done, l.Step);
        Assert.Equal("02", l.Result!.ReleaseHex);
    }

    [Fact]
    public void Learner_NoRelease_BecomesPressOnlyTap()
    {
        var l = new BlePttLearner();
        l.Begin("AA:BB", "Button");
        l.OnNotification(Char1, [0x7F]);
        l.TimeOutStep();

        Assert.Equal(BleLearnStep.Done, l.Step);
        Assert.False(l.Result!.HasRelease);
        Assert.Equal("7F", l.Result.PressHex);
    }

    [Fact]
    public void Learner_NoPress_Fails()
    {
        var l = new BlePttLearner();
        l.Begin("AA:BB", "Button");
        l.TimeOutStep();
        Assert.Equal(BleLearnStep.Failed, l.Step);
        Assert.Null(l.Result);
    }

    // ----- profile matching -----

    [Fact]
    public void Profile_MatchesOnlyExactPacketOnLearnedCharacteristic()
    {
        Assert.True(Profile.IsPress(Char1.ToUpperInvariant(), [0x01]));
        Assert.True(Profile.IsRelease(Char1, [0x00]));
        Assert.False(Profile.IsPress(Char2, [0x01]));        // wrong characteristic
        Assert.False(Profile.IsPress(Char1, [0x01, 0x00]));  // wrong length
        Assert.False(Profile.IsPress(Char1, [0x02]));
    }

    // ----- manager -----

    [Fact]
    public void Manager_PressAndRelease_RaiseEventsOnce()
    {
        var t = new FakeTransport();
        using var m = new BlePttManager(t);
        var events = new List<bool>();
        m.PttStateChanged += events.Add;
        m.SetProfile(Profile);

        t.Send(Char1, 0x01);
        t.Send(Char1, 0x01);   // repeat while held
        t.Send(Char1, 0x00);
        t.Send(Char1, 0x00);

        Assert.Equal([true, false], events);
    }

    [Fact]
    public void Manager_IgnoresUnlearnedTraffic()
    {
        var t = new FakeTransport();
        using var m = new BlePttManager(t);
        var events = new List<bool>();
        m.PttStateChanged += events.Add;

        t.Send(Char1, 0x01);            // no profile yet
        m.SetProfile(Profile);
        t.Send(Char2, 0x01);            // other characteristic
        t.Send(Char1, 0x09);            // unknown packet

        Assert.Empty(events);
    }

    [Fact]
    public async Task Manager_ConnectionLostWhilePressed_ReleasesTheKey()
    {
        var t = new FakeTransport();
        using var m = new BlePttManager(t);
        var events = new List<bool>();
        m.PttStateChanged += events.Add;
        m.SetProfile(Profile);

        await t.ConnectAsync("AA:BB");
        t.Send(Char1, 0x01);
        t.Drop();

        Assert.Equal([true, false], events);
        Assert.False(m.IsPressed);
    }

    [Fact]
    public void Manager_PressOnlyProfile_PulsesPressThenRelease()
    {
        var t = new FakeTransport();
        using var m = new BlePttManager(t);
        var events = new List<bool>();
        m.PttStateChanged += events.Add;
        m.SetProfile(new BlePttProfile("AA:BB", "Button", Char1, "7F", ""));

        t.Send(Char1, 0x7F);

        Assert.Equal([true, false], events);   // never left keyed
    }

    [Fact]
    public void Manager_BatteryIsNotPtt()
    {
        var t = new FakeTransport();
        using var m = new BlePttManager(t);
        var events = new List<bool>();
        int battery = -1;
        m.PttStateChanged += events.Add;
        m.BatteryLevelReceived += b => battery = b;
        m.SetProfile(new BlePttProfile("AA:BB", "Button", BlePttManager.BatteryCharacteristic, "01", "00"));

        t.Send(BlePttManager.BatteryCharacteristic, 0x01);

        Assert.Empty(events);
        Assert.Equal(1, battery);
    }

    [Fact]
    public async Task Manager_Learn_ConnectsAndProducesProfile()
    {
        var t = new FakeTransport();
        using var m = new BlePttManager(t);
        var events = new List<bool>();
        m.PttStateChanged += events.Add;

        await m.BeginLearnAsync(new BleDeviceInfo("AA:BB", "Button", -50));
        Assert.True(m.IsLearning);
        Assert.Equal(BleLearnStep.WaitPress, m.Learner.Step);

        t.Send(Char1, 0x01);
        t.Send(Char1, 0x00);

        Assert.Equal(BleLearnStep.Done, m.Learner.Step);
        Assert.Equal(Profile, m.Learner.Result);
        Assert.False(m.IsLearning);
        Assert.Empty(events);   // learning never keys anything
    }

    [Fact]
    public async Task Manager_Learn_ConnectFailure_ReportsFailed()
    {
        var t = new FakeTransport { FailConnect = true };
        using var m = new BlePttManager(t);

        await m.BeginLearnAsync(new BleDeviceInfo("AA:BB", "Button", -50));

        Assert.Equal(BleLearnStep.Failed, m.Learner.Step);
        Assert.False(m.IsLearning);
    }

    [Fact]
    public void Manager_Unsupported_DoesNothing()
    {
        using var m = new BlePttManager(null);
        m.SetProfile(Profile);
        Assert.False(m.IsSupported);
        Assert.Equal("BLE PTT: not available", m.StatusText);
    }

    [Fact]
    public async Task Manager_ConnectLoop_ConnectsSavedButton()
    {
        var t = new FakeTransport();
        using var m = new BlePttManager(t);
        m.SetProfile(Profile);

        for (int i = 0; i < 100 && !t.IsConnected; i++) await Task.Delay(20);

        Assert.True(t.IsConnected);
        Assert.Equal("AA:BB", t.ConnectedAddress);
    }
}
