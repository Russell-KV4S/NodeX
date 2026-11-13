using NodeX.Core;

namespace NodeX.Core.Tests;

public class TransmitPolicyTests
{
    [Fact]
    public void Default_CanTransmit()
    {
        var p = new TransmitPolicy();
        Assert.True(p.CanTransmit);
        Assert.Equal(TransmitBlock.None, p.Block);
        Assert.Equal("", p.Reason);
    }

    [Fact]
    public void MicrophoneDenied_Blocks()
    {
        var p = new TransmitPolicy { MicrophoneAllowed = false };
        Assert.Equal(TransmitBlock.MicrophoneDenied, p.Block);
        Assert.False(p.CanTransmit);
        Assert.NotEqual("", p.Reason);
    }

    [Fact]
    public void Changed_FiresOnlyWhenAValueActuallyChanges()
    {
        var p = new TransmitPolicy();
        int n = 0;
        p.Changed += () => n++;

        p.MicrophoneAllowed = true;   // already true
        Assert.Equal(0, n);

        p.MicrophoneAllowed = false;
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Ptt_IsRefused_WhenBlocked_AndNothingIsSent()
    {
        using var s = new Iax2Session { LocalNodeNumber = "1234", RemoteNodeNumber = "2000" };
        s.SetStateForTests(Iax2SessionState.Connected);   // keying is refused on a call that is not up
        var policy = new TransmitPolicy { MicrophoneAllowed = false };
        using var c = new NodePttController(s) { Policy = policy };
        var warnings = new List<string>();
        var states = new List<bool>();
        c.WarningLogged += warnings.Add;
        c.PttStateChanged += (k, _) => states.Add(k);

        await c.HandleUiPttAsync(true);
        await c.SetPttStateAsync(true, PttSource.BluetoothBle);
        await c.HandleMediaKeyPttAsync();

        Assert.False(c.IsPttActive);
        Assert.Empty(states);
        Assert.Equal(3, warnings.Count);
        Assert.Contains("Microphone", warnings[0]);
    }

    [Fact]
    public async Task Ptt_Works_OnceAllowed_AndUnkeyingIsNeverBlocked()
    {
        using var s = new Iax2Session { LocalNodeNumber = "1234", RemoteNodeNumber = "2000" };
        s.SetStateForTests(Iax2SessionState.Connected);   // keying is refused on a call that is not up
        var policy = new TransmitPolicy { MicrophoneAllowed = false };
        using var c = new NodePttController(s) { Policy = policy };

        await c.HandleUiPttAsync(true);
        Assert.False(c.IsPttActive);

        policy.MicrophoneAllowed = true;      // user granted the permission
        await c.HandleUiPttAsync(true);
        Assert.True(c.IsPttActive);

        await c.HandleUiPttAsync(false);
        Assert.False(c.IsPttActive);
    }

    [Fact]
    public async Task ActiveKey_IsReleased_WhenPolicyBecomesBlocked()
    {
        using var s = new Iax2Session { LocalNodeNumber = "1234", RemoteNodeNumber = "2000" };
        s.SetStateForTests(Iax2SessionState.Connected);   // keying is refused on a call that is not up
        var policy = new TransmitPolicy();
        using var c = new NodePttController(s) { Policy = policy };
        var released = new TaskCompletionSource();
        c.PttStateChanged += (k, _) => { if (!k) released.TrySetResult(); };

        await c.HandleUiPttAsync(true);
        Assert.True(c.IsPttActive);

        policy.MicrophoneAllowed = false;     // permission revoked while keyed
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(c.IsPttActive);
    }
}
