namespace NodeX.Core.Tests;

public class DesktopBlePttTransportTests
{
    private sealed class Peer : IDesktopBlePeer
    {
        public string Name => "Test button";
        public bool IsConnected { get; set; } = true;
        public bool FailSubscription;
        public TaskCompletionSource? SubscribeReady;
        public bool Disposed;
        public event Action? Disconnected;
        public event Action<string, byte[]>? Notification;
        public void Send() => Notification?.Invoke("button", [1]);
        public void Drop() { IsConnected = false; Disconnected?.Invoke(); }
        public async Task SubscribeAsync(CancellationToken ct)
        {
            if (FailSubscription) throw new IOException("Permission denied");
            if (SubscribeReady != null) await SubscribeReady.Task;
        }
        public ValueTask DisposeAsync() { Disposed = true; IsConnected = false; return ValueTask.CompletedTask; }
    }
    private sealed class Central : IDesktopBleCentral
    {
        public bool IsSupported => true;
        public Peer Next = new();
        public event Action<BleDeviceInfo>? DeviceFound;
        public bool Disposed;
        public Task StartScanAsync(CancellationToken ct) { DeviceFound?.Invoke(new("AA", "Test button", -40, false)); return Task.CompletedTask; }
        public Task StopScanAsync() => Task.CompletedTask;
        public Task<IDesktopBlePeer> ConnectAsync(string address, CancellationToken ct) => Task.FromResult<IDesktopBlePeer>(Next);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task ScanAndConnect_DoNotEmitAPress_OnlyReadyNotificationsAreForwarded()
    {
        var central = new Central();
        await using var transport = new DesktopBlePttTransport(central);
        int devices = 0, packets = 0;
        transport.DeviceFound += _ => devices++;
        transport.Notification += (_, _) => packets++;
        await transport.StartScanAsync();
        await transport.ConnectAsync("AA");
        Assert.Equal(1, devices);
        Assert.Equal(0, packets);
        Assert.True(transport.IsConnected);
        central.Next.Send();
        Assert.Equal(1, packets);
    }

    [Fact]
    public async Task LinkLoss_ReportsDisconnectedImmediately_AndIgnoresLatePackets()
    {
        var central = new Central();
        await using var transport = new DesktopBlePttTransport(central);
        int disconnects = 0, packets = 0;
        transport.ConnectionChanged += (up, _) => { if (!up) disconnects++; };
        transport.Notification += (_, _) => packets++;
        await transport.ConnectAsync("AA");
        central.Next.Drop();
        central.Next.Send();
        Assert.False(transport.IsConnected);
        Assert.Equal(1, disconnects);
        Assert.Equal(0, packets);
    }

    [Fact]
    public async Task Reconnect_DoesNotAcceptOldPeerPacketsOrDisconnects()
    {
        var central = new Central();
        await using var transport = new DesktopBlePttTransport(central);
        int packets = 0;
        transport.Notification += (_, _) => packets++;
        await transport.ConnectAsync("AA");
        var old = central.Next;
        central.Next = new();
        await transport.ConnectAsync("BB");
        old.Send(); old.Drop();
        Assert.True(old.Disposed);
        Assert.True(transport.IsConnected);
        Assert.Equal("BB", transport.ConnectedAddress);
        Assert.Equal(0, packets);
        central.Next.Send();
        Assert.Equal(1, packets);
    }

    [Fact]
    public async Task FailedSubscription_DisposesPeer_AndNeverReportsReady()
    {
        var central = new Central { Next = new() { FailSubscription = true } };
        await using var transport = new DesktopBlePttTransport(central);
        bool ready = false;
        transport.ConnectionChanged += (up, _) => ready |= up;
        await Assert.ThrowsAsync<IOException>(() => transport.ConnectAsync("AA"));
        Assert.False(ready);
        Assert.False(transport.IsConnected);
        Assert.Null(transport.ConnectedAddress);
        Assert.True(central.Next.Disposed);
    }

    [Fact]
    public async Task Dispose_ReleasesConnectionBeforeDriver_AndCannotReconnect()
    {
        var central = new Central();
        var transport = new DesktopBlePttTransport(central);
        await transport.ConnectAsync("AA");
        await transport.DisposeAsync();
        await transport.DisposeAsync();
        Assert.True(central.Disposed);
        Assert.True(central.Next.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.ConnectAsync("AA"));
    }

    [Fact]
    public async Task DisposalDuringSetup_WaitsForNativeOperation_ThenRejectsLatePackets()
    {
        var central = new Central();
        central.Next.SubscribeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new DesktopBlePttTransport(central);
        int packets = 0;
        transport.Notification += (_, _) => packets++;
        var connecting = transport.ConnectAsync("AA");
        central.Next.Send();
        Assert.Equal(0, packets); // not ready yet
        var disposing = transport.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        central.Next.SubscribeReady.SetResult();
        await connecting;
        await disposing;
        central.Next.Send();
        Assert.Equal(0, packets);
        Assert.True(central.Next.Disposed);
        Assert.True(central.Disposed);
    }

    [Fact]
    public async Task CancelledNativeSetup_DisconnectsBeforeReturningCancellation()
    {
        var central = new Central();
        central.Next.SubscribeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var transport = new DesktopBlePttTransport(central);
        using var cancellation = new CancellationTokenSource();
        var connecting = transport.ConnectAsync("AA", cancellation.Token);
        cancellation.Cancel();
        central.Next.SubscribeReady.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.False(transport.IsConnected);
        Assert.True(central.Next.Disposed);
        Assert.Null(transport.ConnectedAddress);
    }
}
