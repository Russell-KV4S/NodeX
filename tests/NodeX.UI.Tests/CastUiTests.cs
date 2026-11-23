using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NodeX.Core.Audio;
using NodeX.Core.Cast;
using NodeX.Core.Settings;
using NodeX.UI;
using NodeX.UI.ViewModels;
using NodeX.UI.Views;

namespace NodeX.Core.Tests;

public sealed class CastUiTests
{
    private sealed class MemStore(NodeSettings initial) : ISettingsStore
    {
        public NodeSettings Current = initial;
        public Task<NodeSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveAsync(NodeSettings settings, CancellationToken ct = default) { Current = settings; return Task.CompletedTask; }
    }

    private sealed class Network : INetworkMonitor
    {
        public event Action? Changed { add { } remove { } }
        public bool IsAvailable => true;
        public string GetSignature() => "test";
        public void Dispose() { }
    }

    private sealed class FakeCastEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private TcpClient? _client;
        private readonly Task _loop;

        public FakeCastEndpoint(string appId = CastProtocol.DefaultMediaReceiverAppId)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            EndPoint = (IPEndPoint)_listener.LocalEndpoint;
            _loop = Task.Run(() => ServeAsync(appId, _cts.Token));
        }

        public IPEndPoint EndPoint { get; }

        public Task<Stream> OpenStreamAsync(IPEndPoint ep, CancellationToken ct)
        {
            var tcp = new TcpClient();
            tcp.Connect(ep.Address, ep.Port);
            return Task.FromResult<Stream>(tcp.GetStream());
        }

        private async Task ServeAsync(string appId, CancellationToken ct)
        {
            try
            {
                _client = await _listener.AcceptTcpClientAsync(ct);
                using var s = _client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    var msg = await CastProtocol.ReadFrameAsync(s, ct);
                    if (msg == null) break;
                    if (msg.Namespace == CastProtocol.NsReceiver && msg.PayloadUtf8?.Contains("\"LAUNCH\"") == true)
                    {
                        string status = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            type = "RECEIVER_STATUS",
                            requestId = 1,
                            status = new
                            {
                                volume = new { level = 0.75, muted = false },
                                applications = new[] { new { appId, sessionId = "ui-s1", transportId = "ui-t1" } }
                            }
                        });
                        await CastProtocol.WriteFrameAsync(s,
                            new CastMessage("receiver-0", msg.SourceId, CastProtocol.NsReceiver, CastPayloadType.String, status), ct);
                    }
                    else if (msg.Namespace == CastProtocol.NsHeartbeat && msg.PayloadUtf8?.Contains("\"PING\"") == true)
                    {
                        await CastProtocol.WriteFrameAsync(s,
                            new CastMessage("receiver-0", msg.SourceId, CastProtocol.NsHeartbeat, CastPayloadType.String, """{"type":"PONG"}"""), ct);
                    }
                }
            }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _client?.Dispose(); } catch { }
            try { await _loop; } catch { }
            _cts.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("Dark", 1.0, 420, 860)]
    [InlineData("HighContrast", 1.5, 420, 860)]
    [InlineData("Dark", 1.0, 860, 420)]
    [InlineData("Dark", 1.0, 1050, 760)]
    [InlineData("Light", 1.5, 1050, 760)]
    public async Task HeaderCastButton_FitsWithoutOverlappingStationBadge(string theme, double textScale, int width, int height)
    {
        Appearance.Apply(Application.Current!, new() { Theme = theme, TextScale = textScale });
        using var vm = new MainViewModel
        {
            LocalNodeNumber = "523802",
            StationDisplayText = "KV4S · 523802 · NodeX Portable",
            IsAslRegistered = true,
            AslStatusText = "ASL Registered · 1 h 50 min",
            BleStatusText = "BLE PTT: not available"
        };
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

            Rect BoundsInWindow(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
            static bool Overlaps(Rect a, Rect b) =>
                a.Right > b.Left + 1 && b.Right > a.Left + 1 && a.Bottom > b.Top + 1 && b.Bottom > a.Top + 1;

            var exitBtn = view.FindControl<Button>("ExitButton")!;
            var replayBtn = view.FindControl<Button>("ReplayButton")!;
            var castBtn = view.FindControl<Button>("CastButton")!;
            var stationBadge = view.FindControl<Border>("StationBadge")!;

            Assert.True(castBtn.IsEffectivelyVisible);

            var exitRect = BoundsInWindow(exitBtn);
            var replayRect = BoundsInWindow(replayBtn);
            var castRect = BoundsInWindow(castBtn);
            var stationRect = BoundsInWindow(stationBadge);

            Assert.True(exitRect.Right <= replayRect.Left + 1);
            Assert.True(replayRect.Right <= castRect.Left + 1);
            Assert.True(castRect.Right <= width + 1, $"CastButton right edge {castRect.Right:F1} exceeded {width}");
            Assert.False(Overlaps(castRect, stationRect), "CastButton overlaps StationBadge");

            var output = Environment.GetEnvironmentVariable("NODEX_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = window.CaptureRenderedFrame();
                using var file = File.Create(Path.Combine(output, $"CastHeader-{theme}-{textScale:F1}-{width}x{height}.png"));
                bitmap!.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CastView_IdleAndActiveCasting_AndSettingsPersistence_Work()
    {
        Appearance.Apply(Application.Current!, new() { Theme = AppThemes.Dark, TextScale = 1.0 });
        await using var fakeTv = new FakeCastEndpoint("A1B2C3D4");
        var store = new MemStore(new NodeSettings
        {
            NodeNumber = "523802",
            CastAppId = "A1B2C3D4",
            CastMuteLocalSpeaker = true,
            RegisterOnStartup = false,
            AutoReconnect = false
        });
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(SecretKeys.NodePassword, "pw");

        await using var rt = new NodeRuntime(
            new NodeConfigService(store, secrets),
            () => new NullAudioDeviceService(),
            networkMonitor: new Network());
        await rt.StartAsync();

        var castManager = new CastSessionManager(rt, clientFactory: () => new CastClient(fakeTv.OpenStreamAsync));
        using var mainVm = new MainViewModel(rt, castManager: castManager);

        try
        {
            mainVm.Cast.Devices.Add(new CastDeviceItem(new CastDevice("tv1", "Shack TV", "Chromecast with Google TV", fakeTv.EndPoint.Address, fakeTv.EndPoint.Port, true)));
            mainVm.Cast.Devices.Add(new CastDeviceItem(new CastDevice("spk1", "Bench Speaker", "Nest Audio", IPAddress.Parse("192.168.1.99"), 8009, false)));

            mainVm.OpenCastCommand.Execute(null);
            Assert.True(mainVm.IsCastOpen);

            var view = new CastView { DataContext = mainVm.Cast };
            var window = new Window { Content = view, Width = 420, Height = 860 };
            window.Show();
            try
            {
                await Task.Delay(80);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

                string? output = Environment.GetEnvironmentVariable("NODEX_SCREENSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    using var idleBmp = window.CaptureRenderedFrame();
                    using var idleFile = File.Create(Path.Combine(output, "CastView-Idle-420x860.png"));
                    idleBmp!.Save(idleFile, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                // Start casting against loopback FakeCastEndpoint
                await mainVm.Cast.ConnectDeviceCommand.ExecuteAsync(mainVm.Cast.Devices[0]);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

                Assert.True(mainVm.IsCasting);
                Assert.True(mainVm.Cast.IsCasting);
                Assert.True(mainVm.Cast.IsCustomDashboardMode);
                Assert.Equal("Shack TV", mainVm.Cast.ActiveDeviceName);
                Assert.Equal("TV Dashboard + Audio", mainVm.Cast.ModeBadgeText);

                if (!string.IsNullOrWhiteSpace(output))
                {
                    using var activeBmp = window.CaptureRenderedFrame();
                    using var activeFile = File.Create(Path.Combine(output, "CastView-Active-420x860.png"));
                    activeBmp!.Save(activeFile, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                    window.Width = 1050;
                    window.Height = 760;
                    await Task.Delay(60);
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                    using var wideBmp = window.CaptureRenderedFrame();
                    using var wideFile = File.Create(Path.Combine(output, "CastView-Active-1050x760.png"));
                    wideBmp!.Save(wideFile, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                await mainVm.Cast.StopCastCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(mainVm.IsCasting);
            }
            finally
            {
                window.Close();
            }

            Assert.True(mainVm.CloseTopOverlay());
            Assert.False(mainVm.IsCastOpen);

            // Settings ViewModel loads & persists CastAppId + CastMuteLocalSpeaker
            await mainVm.Settings.LoadAsync();
            Assert.Equal("A1B2C3D4", mainVm.Settings.CastAppId);
            Assert.True(mainVm.Settings.CastMuteLocalSpeaker);
            mainVm.Settings.CastAppId = "f1e2d3c4";
            mainVm.Settings.CastMuteLocalSpeaker = false;
            await mainVm.Settings.SaveCommand.ExecuteAsync(null);
            Assert.False(mainVm.Settings.HasError);
            Assert.Equal("F1E2D3C4", store.Current.CastAppId);
            Assert.False(store.Current.CastMuteLocalSpeaker);
        }
        finally
        {
            await castManager.DisposeAsync();
        }
    }
}
