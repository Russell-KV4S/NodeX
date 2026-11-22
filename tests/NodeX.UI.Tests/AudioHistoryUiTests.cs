using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NodeX.Core.Audio;
using NodeX.Core.Settings;
using NodeX.UI;
using NodeX.UI.ViewModels;
using NodeX.UI.Views;

namespace NodeX.Core.Tests;

public sealed class AudioHistoryUiTests
{
    [AvaloniaTheory]
    [InlineData(420, 860)]
    [InlineData(860, 420)]
    [InlineData(1050, 760)]
    public void HeaderActionButtons_ExitAndReplay_SitSideBySideInsideWindow(int width, int height)
    {
        Appearance.Apply(Application.Current!, new() { Theme = AppThemes.Dark, TextScale = 1.0 });
        using var vm = new MainViewModel();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

            var exitBtn = view.FindControl<Button>("ExitButton")!;
            var replayBtn = view.FindControl<Button>("ReplayButton")!;
            Assert.True(exitBtn.IsEffectivelyVisible);
            Assert.True(replayBtn.IsEffectivelyVisible);

            var exitOrigin = exitBtn.TranslatePoint(default, window)!.Value;
            var replayOrigin = replayBtn.TranslatePoint(default, window)!.Value;

            // Replay button sits cleanly to the right of Exit with zero overlap and stays inside the window.
            Assert.True(exitOrigin.X + exitBtn.Bounds.Width <= replayOrigin.X + 1);
            Assert.True(replayOrigin.X + replayBtn.Bounds.Width <= width + 1);

            var output = Environment.GetEnvironmentVariable("NODEX_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                using var bitmap = window.CaptureRenderedFrame();
                using var file = File.Create(Path.Combine(output, $"HeaderButtons-{width}x{height}.png"));
                bitmap!.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AudioHistoryView_RendersZelloStyleClipsAndFilters()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nodex-ui-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            Appearance.Apply(Application.Current!, new() { Theme = AppThemes.Dark, TextScale = 1.0 });
            var store = new AudioHistoryStore(dir, maxClips: 25);
            short[] clipSamples = new short[AudioFormat.SampleRate * 6]; // 6 seconds
            var now = DateTimeOffset.UtcNow;
            store.SaveClip("523800", "KV4S Cloud", AudioClipDirection.Rx, clipSamples, now.AddMinutes(-5));
            store.SaveClip("523800", "KV4S Cloud", AudioClipDirection.Tx, clipSamples, now.AddMinutes(-3));
            store.SaveClip("48168", "Alabama Link", AudioClipDirection.Rx, clipSamples, now.AddMinutes(-1));

            await using var player = new AudioClipPlayer(() => new NullAudioDeviceService());
            using var vm = new AudioHistoryViewModel(store, player, () => true);
            var view = new AudioHistoryView { DataContext = vm };
            var window = new Window { Content = view, Width = 420, Height = 860 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

                Assert.Equal(3, vm.Items.Count);
                Assert.True(vm.HasLatestRx);
                Assert.Contains("48168", vm.LatestRxTitle);

                var output = Environment.GetEnvironmentVariable("NODEX_SCREENSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    using var bitmap = window.CaptureRenderedFrame();
                    using var file = File.Create(Path.Combine(output, "AudioHistory-420x860.png"));
                    bitmap!.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                vm.SetFilterCommand.Execute("rx");
                Assert.Equal(2, vm.Items.Count);
                vm.SetFilterCommand.Execute("tx");
                Assert.Single(vm.Items);
            }
            finally { window.Close(); }
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task AudioHistoryView_MultiSelectAndCombine_CreatesCombinedClipAtTop()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nodex-ui-combine-" + Guid.NewGuid().ToString("N"));
        try
        {
            Appearance.Apply(Application.Current!, new() { Theme = AppThemes.Dark, TextScale = 1.0 });
            var store = new AudioHistoryStore(dir, maxClips: 25);
            short[] clipSamples = new short[AudioFormat.SampleRate * 2]; // 2 seconds
            var now = DateTimeOffset.UtcNow;
            store.SaveClip("523800", "KV4S Cloud", AudioClipDirection.Rx, clipSamples, now.AddMinutes(-5));
            store.SaveClip("523800", "KV4S Cloud", AudioClipDirection.Tx, clipSamples, now.AddMinutes(-3));
            store.SaveClip("48168", "Alabama Link", AudioClipDirection.Rx, clipSamples, now.AddMinutes(-1));

            await using var player = new AudioClipPlayer(() => new NullAudioDeviceService());
            using var vm = new AudioHistoryViewModel(store, player, () => true);
            var view = new AudioHistoryView { DataContext = vm };
            var window = new Window { Content = view, Width = 420, Height = 860 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

                Assert.Contains("3 / 25 clips", vm.StorageSummaryText);

                await vm.BeginSelectCommand.ExecuteAsync(null);
                Assert.True(vm.IsSelecting);
                Assert.All(vm.Items, i => Assert.True(i.IsSelecting));

                vm.Items[0].IsSelected = true;
                vm.Items[2].IsSelected = true;
                Assert.Equal(2, vm.SelectedCount);
                Assert.True(vm.CanCombineSelected);

                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);

                vm.CombineTitle = "Sunday Net";
                await vm.CombineSelectedCommand.ExecuteAsync(null);

                Assert.False(vm.IsSelecting);
                Assert.Equal(4, vm.Items.Count);
                Assert.True(vm.Items[0].IsCombined);
                Assert.Equal("COMBINED", vm.Items[0].DirectionBadge);
                Assert.Contains("Sunday Net", vm.Items[0].Title);
                Assert.True(vm.HasLastCombinedClip);
                Assert.Contains("4 / 25 clips", vm.StorageSummaryText);

                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            }
            finally { window.Close(); }
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
