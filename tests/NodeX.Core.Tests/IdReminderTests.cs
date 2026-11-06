using NodeX.Core;
using NodeX.Core.Settings;
using NodeX.UI.ViewModels;

namespace NodeX.Core.Tests;

public class IdReminderTests
{
    /// <summary>Deterministic clock: timers fire only when <see cref="Advance"/> moves time past them.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new ManualTimer(this, callback, state, _now + dueTime);
            _timers.Add(t);
            return t;
        }

        public int ActiveTimers => _timers.Count(t => !t.Disposed);

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var t in _timers.Where(t => !t.Disposed && t.Due <= _now).ToList())
            {
                t.Dispose();
                t.Callback(t.State);
            }
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset Due { get; private set; } = due;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = owner._now + dueTime; return true; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    private static (IdReminder r, ManualTime t, List<bool> events) Make(bool enabled = true)
    {
        var t = new ManualTime();
        var r = new IdReminder(t);
        var events = new List<bool>();
        r.DueChanged += events.Add;
        r.Configure(enabled, Ten);
        return (r, t, events);
    }

    [Fact]
    public void Disabled_NeverBecomesDue()
    {
        var (r, t, events) = Make(enabled: false);
        r.TransmissionStarted();
        t.Advance(TimeSpan.FromHours(1));
        Assert.False(r.IsDue);
        Assert.Empty(events);
        Assert.Equal(0, t.ActiveTimers);
    }

    [Fact]
    public void NoTransmission_NoReminder()
    {
        var (r, t, events) = Make();
        t.Advance(TimeSpan.FromHours(1));
        Assert.False(r.IsDue);
        Assert.Empty(events);
    }

    [Fact]
    public void BecomesDue_OnlyAfterTheIntervalSinceFirstTransmission()
    {
        var (r, t, events) = Make();
        r.TransmissionStarted();

        t.Advance(Ten - TimeSpan.FromSeconds(1));
        Assert.False(r.IsDue);

        t.Advance(TimeSpan.FromSeconds(1));
        Assert.True(r.IsDue);
        Assert.Equal([true], events);
    }

    [Fact]
    public void FurtherTransmissions_DoNotRestartTheClock()
    {
        var (r, t, _) = Make();
        r.TransmissionStarted();
        t.Advance(TimeSpan.FromMinutes(6));
        r.TransmissionStarted();               // still the same QSO
        t.Advance(TimeSpan.FromMinutes(4));
        Assert.True(r.IsDue);                  // measured from the first key-up, not the second
    }

    [Fact]
    public void StaysDueUntilAcknowledged_ThenNextTransmissionStartsAFreshClock()
    {
        var (r, t, events) = Make();
        r.TransmissionStarted();
        t.Advance(Ten);
        t.Advance(TimeSpan.FromMinutes(30));
        Assert.True(r.IsDue);
        Assert.Equal([true], events);          // raised once, not repeatedly

        r.Acknowledge();
        Assert.False(r.IsDue);
        Assert.Equal([true, false], events);

        t.Advance(TimeSpan.FromHours(1));      // no transmission -> stays quiet
        Assert.False(r.IsDue);

        r.TransmissionStarted();
        t.Advance(Ten);
        Assert.True(r.IsDue);
    }

    [Fact]
    public void Reset_ClearsDueAndCancelsTheClock()
    {
        var (r, t, events) = Make();
        r.TransmissionStarted();
        r.Reset();
        t.Advance(TimeSpan.FromHours(1));
        Assert.False(r.IsDue);
        Assert.Empty(events);
        Assert.Equal(0, t.ActiveTimers);
    }

    [Fact]
    public void SwitchingOff_ClearsAPendingReminder()
    {
        var (r, t, events) = Make();
        r.TransmissionStarted();
        t.Advance(Ten);
        Assert.True(r.IsDue);

        r.Configure(false, Ten);
        Assert.False(r.IsDue);
        Assert.Equal([true, false], events);
    }

    [Fact]
    public void ShorteningTheInterval_AppliesToARunningClock()
    {
        var (r, t, _) = Make();
        r.TransmissionStarted();
        t.Advance(TimeSpan.FromMinutes(3));

        r.Configure(true, TimeSpan.FromMinutes(5));
        Assert.False(r.IsDue);
        t.Advance(TimeSpan.FromMinutes(2));
        Assert.True(r.IsDue);

        // and shortening below what has already elapsed fires immediately
        r.Acknowledge();
        r.TransmissionStarted();
        t.Advance(TimeSpan.FromMinutes(4));
        r.Configure(true, TimeSpan.FromMinutes(2));
        Assert.True(r.IsDue);
    }

    [Fact]
    public void Dispose_StopsTheTimer_AndIgnoresLaterCalls()
    {
        var (r, t, events) = Make();
        r.TransmissionStarted();
        r.Dispose();
        t.Advance(TimeSpan.FromHours(1));
        r.TransmissionStarted();
        Assert.False(r.IsDue);
        Assert.Empty(events);
    }

    // ----- settings -----

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void IdReminderMinutes_Range(int minutes, bool valid)
    {
        var s = new NodeSettings { NodeNumber = "55553", Callsign = "W1AW", IdReminderMinutes = minutes };
        Assert.Equal(valid, NodeSettingsValidator.Validate(s, "pw").Count == 0);
    }

    [Fact]
    public void Defaults_AreOffAndTenMinutes()
    {
        var s = new NodeSettings();
        Assert.False(s.IdReminderEnabled);
        Assert.Equal(10, s.IdReminderMinutes);
    }

    [Fact]
    public async Task SettingsScreen_RoundTripsTheReminder_AndRejectsNonNumbers()
    {
        string path = Path.Combine(Path.GetTempPath(), "nodex-id-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var config = new NodeConfigService(new JsonFileSettingsStore(path), new InMemorySecretStore());
            var vm = new SettingsViewModel(config)
            {
                NodeNumber = "55553", Callsign = "W1AW", Password = "pw", IdReminderEnabled = true, IdReminderMinutesText = "15"
            };
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.False(vm.HasError);

            var reload = new SettingsViewModel(config);
            await reload.LoadAsync();
            Assert.True(reload.IdReminderEnabled);
            Assert.Equal("15", reload.IdReminderMinutesText);

            reload.IdReminderMinutesText = "abc";
            await reload.SaveCommand.ExecuteAsync(null);
            Assert.True(reload.HasError);
        }
        finally
        {
            foreach (string f in new[] { path, path + ".tmp" })
                try { File.Delete(f); } catch (IOException) { }
        }
    }
}
