using NodeX.Core;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

/// <summary>Loopback tests driving a real <see cref="Iax2Session"/> against a scripted fake remote node.</summary>
public class Iax2KeepAliveTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        using var s = new Iax2Session();
        Assert.Equal(TimeSpan.FromSeconds(15), s.KeepAliveInterval);
        Assert.Equal(3, s.MaxMissedKeepAlives);
    }

    [Fact]
    public void LinkListInterval_DefaultsToTenSeconds()
    {
        using var s = new Iax2Session();
        Assert.Equal(TimeSpan.FromSeconds(10), s.LinkListInterval);
    }

    /// <summary>
    /// Hubs such as 27339 hang up a link that sends no app_rpt link text for ~45 s even though it answers PINGs,
    /// so a connected session must send the "L " link list on a timer.
    /// </summary>
    [Fact]
    public async Task SendsLinkListText_WhileConnected()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync(
            TimeSpan.FromSeconds(30), configure: s => s.LinkListInterval = TimeSpan.FromMilliseconds(60));

        int lists = 0;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (lists < 2 && DateTime.UtcNow < deadline)
        {
            var f = await remote.ReceiveFrameAsync(1000);
            if (f.FrameType == FrameType.Text && System.Text.Encoding.UTF8.GetString(f.Payload).TrimEnd('\0') == "L ")
                lists++;
        }

        Assert.Equal(2, lists);
    }

    [Fact]
    public async Task SendsPingAtInterval_AndPongYieldsRtt_LinkStaysUp()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync(TimeSpan.FromMilliseconds(60));
        var rtt = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RoundTripTimeUpdated += t => rtt.TrySetResult(t);

        // Answer six consecutive PINGs (twice the miss limit) with PONGs echoing the timestamp.
        for (int i = 0; i < 6; i++)
        {
            var ping = await remote.WaitForPingAsync();
            await remote.SendIaxAsync(IaxControlSubclass.Pong, ping.Timestamp);
        }

        Assert.Equal(Iax2SessionState.Connected, session.State);
        var measured = await rtt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.InRange(measured.TotalMilliseconds, 0, 500);
        Assert.InRange(session.RoundTripTime.TotalMilliseconds, 0, 500);
    }

    [Fact]
    public async Task SilentRemote_DeclaresLinkDead_AfterMaxMissedKeepAlives()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync(TimeSpan.FromMilliseconds(50), maxMissed: 3);

        var dead = new TaskCompletionSource<Iax2SessionState>();
        string? error = null;
        session.ErrorOccurred += e => error = e;
        session.StateChanged += st => { if (st == Iax2SessionState.Disconnected) dead.TrySetResult(st); };

        // Remote never answers: expect exactly MaxMissed PINGs, then the watchdog trips.
        int pings = 0;
        using var drain = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            try
            {
                while (!drain.IsCancellationRequested)
                {
                    var f = await remote.ReceiveFrameAsync(5000);
                    if (f.FrameType == FrameType.IaxControl && f.Subclass == (byte)IaxControlSubclass.Ping)
                        Interlocked.Increment(ref pings);
                }
            }
            catch (OperationCanceledException) { }
        });

        await dead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        drain.Cancel();
        await drainTask;

        Assert.Equal(Iax2SessionState.Disconnected, session.State);
        Assert.Equal(3, pings);
        Assert.Contains("keep-alive", error);
    }

    [Fact]
    public async Task AnyInboundTraffic_ResetsWatchdog()
    {
        using var remote = new FakeIaxRemote();
        using var session = await remote.ConnectSessionAsync(TimeSpan.FromMilliseconds(50), maxMissed: 2);

        // Never PONG, but send some other valid frame after each PING: link must stay up.
        for (int i = 0; i < 6; i++)
        {
            await remote.WaitForPingAsync();
            await remote.SendControlAsync(ControlSubclass.AstControlUnkey);
        }

        Assert.Equal(Iax2SessionState.Connected, session.State);
    }
}
