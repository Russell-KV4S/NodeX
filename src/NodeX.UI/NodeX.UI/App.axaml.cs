using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NodeX.UI.ViewModels;
using NodeX.UI.Views;

namespace NodeX.UI;

public partial class App : Application
{
    internal bool IsTestHost { get; init; }
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Appearance.Apply(this, new NodeX.Core.Settings.NodeSettings());
#if DEBUG
        if (!IsTestHost) this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (IsTestHost) { base.OnFrameworkInitializationCompleted(); return; }
        AppServices.Runtime.ConfigService.Changed += config => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => Appearance.Apply(this, config.Settings));
        // One view model for the whole app session; platform heads have already supplied their
        // secure storage and audio backend through AppServices.Initialize.
        var viewModel = new MainViewModel();
        // Back: close an open panel first, then step back through the phone tabs; false = nothing left to go back to.
        AppServices.BackHandler = () => viewModel.CloseTopOverlay() || (AppServices.TabBackHandler?.Invoke() ?? false);
        viewModel.ExitRequested += () =>
        {
            if (AppServices.ExitHandler is { } exit) exit();
            else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Unkey, hang up and stop registering before the process goes away.
            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                AppServices.Shutdown(TimeSpan.FromSeconds(2));
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView { DataContext = viewModel };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView { DataContext = viewModel };
        }

        base.OnFrameworkInitializationCompleted();

        // Stop the UI refresh timer while the app is not visible (mobile).
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, _) => viewModel.SetForeground(true);
            activatable.Deactivated += (_, _) => viewModel.SetForeground(false);
        }

        _ = InitializeViewModelAsync(viewModel);
    }

    private async System.Threading.Tasks.Task InitializeViewModelAsync(MainViewModel viewModel)
    {
        await viewModel.InitializeAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => Appearance.Apply(this, AppServices.Runtime.Config.Settings));
    }
}
