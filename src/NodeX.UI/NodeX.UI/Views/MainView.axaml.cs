using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using NodeX.UI.ViewModels;

namespace NodeX.UI.Views;

public partial class MainView : UserControl
{
    /// <summary>Widths below this use the single-column phone layout with the PTT button docked at the bottom.</summary>
    public const double NarrowBreakpoint = 720;

    /// <summary>The one-row header (brand, station, badges, settings) needs more room than the body does.</summary>
    public const double CompactHeaderBreakpoint = 920;

    /// <summary>
    /// Heights below this (a phone turned sideways, a split-screen window) keep the PTT bar docked at the bottom
    /// even when the width is large enough for two columns, so the key is never scrolled out of reach.
    /// </summary>
    public const double ShortHeightBreakpoint = 520;

    private enum HeaderMode { Wide, Compact, Short }

    private bool? _narrow;
    private bool? _short;
    private HeaderMode? _headerMode;

    public MainView()
    {
        InitializeComponent();

        var pttButton = this.FindControl<Button>("PttButton");
        if (pttButton != null)
        {
            pttButton.AddHandler(InputElement.PointerPressedEvent, OnPttPointerPressed, RoutingStrategies.Tunnel);
            pttButton.AddHandler(InputElement.PointerReleasedEvent, OnPttPointerReleased, RoutingStrategies.Tunnel);
            pttButton.AddHandler(InputElement.PointerCaptureLostEvent, OnPttPointerReleased, RoutingStrategies.Tunnel);
        }

        SizeChanged += (_, e) =>
        {
            var isShort = e.NewSize.Height < ShortHeightBreakpoint;
            ApplyHeaderLayout(e.NewSize.Width < CompactHeaderBreakpoint
                ? (isShort ? HeaderMode.Short : HeaderMode.Compact)
                : HeaderMode.Wide);
            ApplyLayout(e.NewSize.Width < NarrowBreakpoint, isShort);
        };
    }

    /// <summary>Copies the whole event log (oldest line first) to the system clipboard.</summary>
    private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        try
        {
            await clipboard.SetTextAsync(vm.GetLogText());
            vm.NoteLogCopied();
        }
        catch (Exception)
        {
            // Clipboard can be unavailable (locked by another app); nothing useful to show.
        }
    }

    private void OnPttPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsToggleMode)
        {
            _ = vm.PttDownAsync();
        }
    }

    private void OnPttPointerReleased(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsToggleMode)
        {
            _ = vm.PttUpAsync();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Responsive layout. The same cards are re-parented between a wide two-column arrangement
    // (desktop/tablet landscape) and a single scrolling column with a docked PTT bar (phones), so
    // there is one set of controls and bindings for both.
    // ------------------------------------------------------------------------------------------

    private void ApplyLayout(bool narrow, bool isShort)
    {
        if (_narrow == narrow && _short == isShort) return;
        _narrow = narrow;
        _short = isShort;

        // The PTT bar docks whenever the screen is narrow OR short; the column count follows width only.
        var dock = narrow || isShort;

        Classes.Set("narrow", dock);   // finger-sized targets wherever the PTT bar is docked

        ApplyBodyLayout(narrow, dock);
        ApplyPttSizing(dock, isShort);
        ApplyConnectLayout(narrow);

        RxCard.Padding = TxCard.Padding = new Thickness(dock ? 12 : 16);
    }

    private void ApplyHeaderLayout(HeaderMode mode)
    {
        if (_headerMode == mode) return;
        _headerMode = mode;

        if (mode == HeaderMode.Short)
        {
            // Short screens (phone in landscape): brand | station | settings on one row, badges on a thin second row.
            HeaderGrid.ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto");
            HeaderGrid.RowDefinitions = RowDefinitions.Parse("Auto,Auto");

            Place(BrandPanel, row: 0, column: 0, columnSpan: 1);
            Place(StationBadge, row: 0, column: 1, columnSpan: 1);
            Place(SettingsButton, row: 0, column: 2, columnSpan: 1);
            Place(BadgesPanel, row: 1, column: 0, columnSpan: 3);

            StationBadge.HorizontalAlignment = HorizontalAlignment.Center;
            StationBadge.Margin = new Thickness(0);
            BadgesPanel.Margin = new Thickness(0, 6, 0, 0);
            SettingsButton.Margin = new Thickness(0);
            BrandTagline.IsVisible = false;
        }
        else if (mode == HeaderMode.Compact)
        {
            // Row 0: brand + settings. Row 1: station. Row 2: badges.
            HeaderGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,Auto");
            HeaderGrid.RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto");

            Place(BrandPanel, row: 0, column: 0, columnSpan: 1);
            Place(SettingsButton, row: 0, column: 1, columnSpan: 1);
            Place(StationBadge, row: 1, column: 0, columnSpan: 2);
            Place(BadgesPanel, row: 2, column: 0, columnSpan: 2);

            StationBadge.HorizontalAlignment = HorizontalAlignment.Left;
            StationBadge.Margin = new Thickness(0, 10, 0, 0);
            BadgesPanel.Margin = new Thickness(0, 8, 0, 0);
            SettingsButton.Margin = new Thickness(0);
            BrandTagline.IsVisible = false;
        }
        else
        {
            HeaderGrid.ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto");
            HeaderGrid.RowDefinitions = RowDefinitions.Parse("Auto");

            Place(BrandPanel, row: 0, column: 0, columnSpan: 1);
            Place(StationBadge, row: 0, column: 1, columnSpan: 1);
            Place(BadgesPanel, row: 0, column: 2, columnSpan: 1);
            Place(SettingsButton, row: 0, column: 3, columnSpan: 1);

            StationBadge.HorizontalAlignment = HorizontalAlignment.Center;
            StationBadge.Margin = new Thickness(0);
            BadgesPanel.Margin = new Thickness(0);
            SettingsButton.Margin = new Thickness(10, 0, 0, 0);
            BrandTagline.IsVisible = true;
        }
    }

    private void ApplyBodyLayout(bool narrow, bool dock)
    {
        // Take every card out of whatever currently holds it, then place it for the new layout.
        foreach (var card in new Control[] { HeroCard, LinksCard, RxTxRow, MetersCard, PttCard, ConnectCard, DtmfCard, LogCard })
        {
            Detach(card);
        }

        if (narrow)
        {
            BodyGrid.ColumnDefinitions = ColumnDefinitions.Parse("*");
            BodyScroll.Padding = new Thickness(12);
            LeftColumn.Margin = new Thickness(0);
            LeftColumn.Spacing = 12;
            RightColumn.IsVisible = false;
            Grid.SetColumn(RightColumn, 0);     // the grid has a single column now; a stale index crashes measure

            // Order for operating: who am I linked to, connect, activity, meters, keypad, log.
            foreach (var card in new Control[] { HeroCard, LinksCard, ConnectCard, RxTxRow, MetersCard, DtmfCard, LogCard })
            {
                LeftColumn.Children.Add(card);
            }

            LogCard.Height = 200;
        }
        else
        {
            BodyGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,360");
            BodyScroll.Padding = new Thickness(16);
            LeftColumn.Margin = new Thickness(0, 0, 16, 0);
            LeftColumn.Spacing = 16;
            RightColumn.IsVisible = true;
            Grid.SetColumn(RightColumn, 1);

            // Two columns. PTT stays in the left column on tall screens; on short ones it is docked below.
            foreach (var card in dock
                         ? new Control[] { HeroCard, RxTxRow, MetersCard }
                         : new Control[] { HeroCard, RxTxRow, MetersCard, PttCard })
            {
                LeftColumn.Children.Add(card);
            }
            foreach (var card in new Control[] { LinksCard, ConnectCard, DtmfCard, LogCard })
            {
                RightColumn.Children.Add(card);
            }

            LogCard.Height = 220;
        }

        PttDock.Child = dock ? PttCard : null;
        PttDock.IsVisible = dock;
    }

    private void ApplyPttSizing(bool narrow, bool isShort)
    {
        if (narrow)
        {
            // A full-width bar the thumb can find without looking. On short screens the bar is slimmer and the
            // time-out/mode strip is hidden (the key face already shows the running transmit time).
            PttCard.Padding = new Thickness(isShort ? 8 : 12);
            PttCard.CornerRadius = new CornerRadius(12);
            PttInner.Spacing = 8;
            PttInner.HorizontalAlignment = HorizontalAlignment.Stretch;
            PttButton.Width = double.NaN;
            PttButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            PttButton.Height = isShort ? 64 : 96;
            PttInfo.Width = double.NaN;
            PttInfo.HorizontalAlignment = HorizontalAlignment.Stretch;
            PttInfo.IsVisible = !isShort;
            PttCaption.IsVisible = false;
            PttModeHint.IsVisible = true;
        }
        else
        {
            PttCard.Padding = new Thickness(24);
            PttCard.CornerRadius = new CornerRadius(16);
            PttInner.Spacing = 18;
            PttInner.HorizontalAlignment = HorizontalAlignment.Center;
            PttButton.Width = 260;
            PttButton.HorizontalAlignment = HorizontalAlignment.Center;
            PttButton.Height = 140;
            PttInfo.Width = 300;
            PttInfo.HorizontalAlignment = HorizontalAlignment.Center;
            PttInfo.IsVisible = true;
            PttCaption.IsVisible = true;
            PttModeHint.IsVisible = true;
        }
    }

    private void ApplyConnectLayout(bool narrow)
    {
        if (narrow)
        {
            // Node box on its own row, then two equal full-width buttons.
            ConnectGrid.ColumnDefinitions = ColumnDefinitions.Parse("*");
            ConnectGrid.RowDefinitions = RowDefinitions.Parse("Auto,Auto");
            Place(TargetBox, row: 0, column: 0, columnSpan: 1);
            Place(ConnectButtons, row: 1, column: 0, columnSpan: 1);
            TargetBox.Margin = new Thickness(0, 0, 0, 8);
            ConnectButtons.ColumnDefinitions = ColumnDefinitions.Parse("*,*");
            ConnectButtons.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            ConnectGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,Auto");
            ConnectGrid.RowDefinitions = RowDefinitions.Parse("Auto");
            Place(TargetBox, row: 0, column: 0, columnSpan: 1);
            Place(ConnectButtons, row: 0, column: 1, columnSpan: 1);
            TargetBox.Margin = new Thickness(0, 0, 8, 0);
            ConnectButtons.ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto");
            ConnectButtons.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private static void Place(Control control, int row, int column, int columnSpan)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
    }

    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
            case ContentControl content when ReferenceEquals(content.Content, control):
                content.Content = null;
                break;
        }
    }
}
