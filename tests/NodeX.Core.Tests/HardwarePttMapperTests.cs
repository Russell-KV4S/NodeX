using NodeX.Core;

namespace NodeX.Core.Tests;

public class HardwarePttMapperTests
{
    private const int VolumeUp = 24, VolumeDown = 25, Back = 4, Home = 3;

    private static (HardwarePttMapper m, List<bool> presses) Create(int mapped = VolumeUp)
    {
        var m = new HardwarePttMapper { MappedKey = mapped };
        var presses = new List<bool>();
        m.Pressed += presses.Add;
        return (m, presses);
    }

    [Fact]
    public void MappedKey_IsConsumed_AndReportsDownThenUp()
    {
        var (m, presses) = Create();

        Assert.True(m.OnKey(VolumeUp, down: true, isRepeat: false));
        Assert.True(m.OnKey(VolumeUp, down: false, isRepeat: false));

        Assert.Equal([true, false], presses);
    }

    [Fact]
    public void AutoRepeat_IsConsumedButNotReportedAgain()
    {
        var (m, presses) = Create();

        m.OnKey(VolumeUp, true, false);
        Assert.True(m.OnKey(VolumeUp, true, true));
        Assert.True(m.OnKey(VolumeUp, true, true));
        m.OnKey(VolumeUp, false, false);

        Assert.Equal([true, false], presses);
    }

    [Fact]
    public void OtherKeys_PassThrough()
    {
        var (m, presses) = Create();

        Assert.False(m.OnKey(VolumeDown, true, false));
        Assert.False(m.OnKey(VolumeDown, false, false));
        Assert.Empty(presses);
    }

    [Fact]
    public void NothingMapped_NothingConsumed()
    {
        var (m, presses) = Create(mapped: 0);
        Assert.False(m.OnKey(VolumeUp, true, false));
        Assert.Empty(presses);
    }

    [Theory]
    [InlineData(Back)]
    [InlineData(Home)]
    [InlineData(26)]    // power
    [InlineData(187)]   // app switch
    public void ReservedKeys_CannotBeMapped_OrLearned(int code)
    {
        var m = new HardwarePttMapper { MappedKey = code };
        Assert.Equal(0, m.MappedKey);

        int learned = -1;
        m.KeyLearned += c => learned = c;
        m.BeginLearn();
        Assert.False(m.OnKey(code, true, false));   // Back and friends keep working while learning
        Assert.True(m.IsLearning);
        Assert.Equal(-1, learned);
    }

    [Fact]
    public void Learning_CapturesTheNextKey_SwallowsItsRelease_ThenStops()
    {
        var (m, presses) = Create(mapped: 0);
        var learned = new List<int>();
        int changes = 0;
        m.KeyLearned += learned.Add;
        m.LearningChanged += () => changes++;

        m.BeginLearn();
        Assert.True(m.IsLearning);

        Assert.True(m.OnKey(VolumeDown, true, false));    // captured, not delivered
        Assert.True(m.OnKey(VolumeDown, false, false));   // its release is swallowed too
        Assert.False(m.IsLearning);
        Assert.Equal([VolumeDown], learned);
        Assert.Empty(presses);                            // learning never keys the radio
        Assert.Equal(2, changes);                         // started, ended

        Assert.False(m.OnKey(VolumeDown, false, false));  // later releases are ordinary again
    }

    [Fact]
    public void CancelLearn_StopsCapturing()
    {
        var m = new HardwarePttMapper();
        m.BeginLearn();
        m.CancelLearn();
        Assert.False(m.IsLearning);
        Assert.False(m.OnKey(VolumeDown, true, false));
    }

    [Fact]
    public void ChangingTheMapping_WhileHeld_ReleasesPtt()
    {
        var (m, presses) = Create();
        m.OnKey(VolumeUp, true, false);

        m.MappedKey = VolumeDown;   // e.g. settings saved with a different button
        Assert.Equal([true, false], presses);

        Assert.False(m.OnKey(VolumeUp, false, false));   // the old key's late release is not ours any more
    }
}

public class PttButtonTests
{
    private static Iax2Session NewSession()
    {
        var s = new Iax2Session { LocalNodeNumber = "1234", RemoteNodeNumber = "2000" };
        s.SetStateForTests(Iax2SessionState.Connected);   // keying is refused on a call that is not up
        return s;
    }

    [Fact]
    public async Task Momentary_HoldKeys_ReleaseUnkeys()
    {
        using var s = NewSession();
        using var c = new NodePttController(s);

        await c.HandleButtonAsync(true, PttSource.MediaKey);
        Assert.True(c.IsPttActive);
        Assert.Equal(PttSource.MediaKey, c.CurrentSource);

        await c.HandleButtonAsync(false, PttSource.MediaKey);
        Assert.False(c.IsPttActive);
    }

    [Fact]
    public async Task Toggle_PressLatches_ReleaseIgnored_NextPressUnkeys()
    {
        using var s = NewSession();
        using var c = new NodePttController(s) { Mode = PttMode.Toggle };

        await c.HandleButtonAsync(true, PttSource.MediaKey);
        await c.HandleButtonAsync(false, PttSource.MediaKey);
        Assert.True(c.IsPttActive);                  // still keyed after the finger lifts

        await c.HandleButtonAsync(true, PttSource.MediaKey);
        await c.HandleButtonAsync(false, PttSource.MediaKey);
        Assert.False(c.IsPttActive);
    }

    [Fact]
    public async Task Button_IsRefused_WhenTransmitIsBlocked()
    {
        using var s = NewSession();
        using var c = new NodePttController(s) { Policy = new TransmitPolicy { ListenOnly = true } };

        await c.HandleButtonAsync(true, PttSource.MediaKey);
        Assert.False(c.IsPttActive);
    }
}
