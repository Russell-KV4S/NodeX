using System;
using System.Collections.Generic;
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

    /// <summary>Phone bottom-navigation tabs. On wide layouts everything is on one page and this is ignored.</summary>
    private enum Tab { Radio, Nodes, Keypad, Log }
    private Tab _tab = Tab.Radio;
    private HeaderMode? _headerMode;

    public MainView()
    {
        InitializeComponent();
        AppServices.TabBackHandler = GoBackTab;

        var pttButton = this.FindControl<Button>("PttButton");
        if (pttButton != null)
        {
            pttButton.AddHandler(InputElement.PointerPressedEvent, OnPttPointerPressed, RoutingStrategies.Tunnel);
            pttButton.AddHandler(InputElement.PointerReleasedEvent, OnPttPointerReleased, RoutingStrategies.Tunnel);
            pttButton.AddHandler(InputElement.PointerCaptureLostEvent, OnPttPointerReleased, RoutingStrategies.Tunnel);
        }

        FavoritesList.AddHandler(InputElement.PointerPressedEvent, OnFavPointerPressed, RoutingStrategies.Tunnel);
        FavoritesList.AddHandler(InputElement.PointerMovedEvent, OnFavPointerMoved, RoutingStrategies.Tunnel);
        FavoritesList.AddHandler(InputElement.PointerReleasedEvent, OnFavPointerReleased, RoutingStrategies.Tunnel);
        FavoritesList.AddHandler(InputElement.PointerCaptureLostEvent, OnFavCaptureLost, RoutingStrategies.Tunnel);

        SizeChanged += (_, e) =>
        {
            var isShort = e.NewSize.Height < ShortHeightBreakpoint;
            ApplyHeaderLayout(e.NewSize.Width < CompactHeaderBreakpoint
                ? (isShort ? HeaderMode.Short : HeaderMode.Compact)
                : HeaderMode.Wide);
            ApplyLayout(e.NewSize.Width < NarrowBreakpoint, isShort);
        };
    }

    private void OnRemoveFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FavoriteItem item } && DataContext is MainViewModel vm)
            _ = vm.RemoveFavoriteAsync(item.Node);
    }

    // --- Drag favourites to reorder. A plain click still connects (the Button gets it); once the pointer moves
    //     past a small threshold the drag takes over the pointer, so no click fires on release. ---

    private const double FavDragThreshold = 8;
    private int _favDragIndex = -1;
    private Point _favDragStart;
    private bool _favDragging;

    /// <summary>Index of the favourite chip under <paramref name="p"/> (relative to the list), or -1.</summary>
    private int FavIndexAt(Point p)
    {
        for (int i = 0; i < FavoritesList.ItemCount; i++)
        {
            var c = FavoritesList.ContainerFromIndex(i);
            if (c == null) continue;
            var origin = c.TranslatePoint(new Point(0, 0), FavoritesList);
            if (origin == null) continue;
            if (new Rect(origin.Value, c.Bounds.Size).Contains(p)) return i;
        }
        return -1;
    }

    private void SetFavDragVisual(int index)
    {
        for (int i = 0; i < FavoritesList.ItemCount; i++)
            if (FavoritesList.ContainerFromIndex(i) is { } c) c.Opacity = i == index ? 0.45 : 1;
    }

    private void OnFavPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FavoritesList).Properties.IsLeftButtonPressed) return;
        _favDragStart = e.GetPosition(FavoritesList);
        _favDragIndex = FavIndexAt(_favDragStart);
        _favDragging = false;
    }

    private void OnFavPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_favDragIndex < 0 || DataContext is not MainViewModel vm) return;
        if (!e.GetCurrentPoint(FavoritesList).Properties.IsLeftButtonPressed) { EndFavDrag(save: false); return; }

        var pos = e.GetPosition(FavoritesList);
        if (!_favDragging)
        {
            if (Math.Abs(pos.X - _favDragStart.X) < FavDragThreshold && Math.Abs(pos.Y - _favDragStart.Y) < FavDragThreshold)
                return;
            _favDragging = true;
            e.Pointer.Capture(FavoritesList);   // takes the pointer from the chip's Button: no click on release
            SetFavDragVisual(_favDragIndex);
        }

        int over = FavIndexAt(pos);
        if (over >= 0 && over != _favDragIndex)
        {
            vm.MoveFavorite(_favDragIndex, over);
            _favDragIndex = over;
            // The containers are re-laid out after a move: dim the dragged chip once they are.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SetFavDragVisual(_favDragIndex),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
        e.Handled = true;
    }

    private void OnFavPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_favDragging) { e.Pointer.Capture(null); e.Handled = true; }
        EndFavDrag(save: true);
    }

    private void OnFavCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Our own Capture() call makes the Button lose the pointer; only a loss while dragging elsewhere ends it.
        if (_favDragging && e.Source == FavoritesList) EndFavDrag(save: true);
    }

    private void EndFavDrag(bool save)
    {
        bool wasDragging = _favDragging;
        _favDragIndex = -1;
        _favDragging = false;
        SetFavDragVisual(-1);
        if (wasDragging && save && DataContext is MainViewModel vm)
            _ = vm.SaveFavoriteOrderAsync();
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

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int i) && i is >= 0 and <= 3)
            SelectTab((Tab)i);
    }

    private void OnGoToNodesClick(object? sender, RoutedEventArgs e) => SelectTab(Tab.Nodes);

    /// <summary>Tabs visited before the current one (most recent last), so Back returns to where you were.</summary>
    private readonly List<Tab> _tabHistory = [];

    /// <summary>
    /// Back on a phone tab other than Radio: return to the previous tab, or Radio when there is no history.
    /// False on the Radio tab (the platform then sends the app to the background) and on wide layouts.
    /// </summary>
    public bool GoBackTab()
    {
        if (_narrow != true || _tab == Tab.Radio) return false;

        var target = Tab.Radio;
        if (_tabHistory.Count > 0)
        {
            target = _tabHistory[^1];
            _tabHistory.RemoveAt(_tabHistory.Count - 1);
        }
        SelectTab(target, record: false);
        return true;
    }

    private void SelectTab(Tab tab, bool record = true)
    {
        if (_narrow != true)
        {
            // Wide layout: one page with everything on it. Highlight the tab and scroll its card into view
            // (also when it is already the selected tab, since the user may have scrolled away since).
            _tab = tab;
            UpdateNavSelection();
            switch (tab)
            {
                case Tab.Radio: BodyScroll.Offset = default; break;
                case Tab.Nodes: ConnectCard.BringIntoView(); break;
                case Tab.Keypad: DtmfCard.BringIntoView(); break;
                case Tab.Log: LogCard.BringIntoView(); break;
            }
            return;
        }

        if (_tab == tab) return;
        if (record)
        {
            _tabHistory.Remove(tab);   // no loops: a tab appears once, at its latest visit
            _tabHistory.Add(_tab);
            if (_tabHistory.Count > 8) _tabHistory.RemoveAt(0);
        }
        _tab = tab;
        if (_narrow == true)
        {
            ApplyBodyLayout(narrow: true, dock: true);
            BodyScroll.Offset = default;   // each tab starts at its top
        }
        UpdateNavSelection();
    }

    private void UpdateNavSelection()
    {
        NavRadio.Classes.Set("selected", _tab == Tab.Radio);
        NavNodes.Classes.Set("selected", _tab == Tab.Nodes);
        NavKeypad.Classes.Set("selected", _tab == Tab.Keypad);
        NavLog.Classes.Set("selected", _tab == Tab.Log);
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

        // The bottom bar is always shown. On phones its tabs switch pages (and the links summary in the header is
        // replaced by the Radio tab's rows); on wide layouts everything is already on screen, so its tabs scroll to
        // the matching card instead.
        NavBar.IsVisible = true;
        HeaderLinksStrip.IsVisible = !narrow;
        if (!narrow) _tabHistory.Clear();
        UpdateNavSelection();
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
            Place(HeaderActions, row: 0, column: 2, columnSpan: 1);
            Place(BadgesPanel, row: 1, column: 0, columnSpan: 3);

            StationBadge.HorizontalAlignment = HorizontalAlignment.Center;
            StationBadge.Margin = new Thickness(0);
            BadgesPanel.Margin = new Thickness(0, 6, 0, 0);
            HeaderActions.Margin = new Thickness(0);
            BrandTagline.IsVisible = false;
        }
        else if (mode == HeaderMode.Compact)
        {
            // Row 0: brand + settings. Row 1: station. Row 2: badges.
            HeaderGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,Auto");
            HeaderGrid.RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto");

            Place(BrandPanel, row: 0, column: 0, columnSpan: 1);
            Place(HeaderActions, row: 0, column: 1, columnSpan: 1);
            Place(StationBadge, row: 1, column: 0, columnSpan: 2);
            Place(BadgesPanel, row: 2, column: 0, columnSpan: 2);

            StationBadge.HorizontalAlignment = HorizontalAlignment.Left;
            StationBadge.Margin = new Thickness(0, 10, 0, 0);
            BadgesPanel.Margin = new Thickness(0, 8, 0, 0);
            HeaderActions.Margin = new Thickness(0);
            BrandTagline.IsVisible = false;
        }
        else
        {
            HeaderGrid.ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto");
            HeaderGrid.RowDefinitions = RowDefinitions.Parse("Auto");

            Place(BrandPanel, row: 0, column: 0, columnSpan: 1);
            Place(StationBadge, row: 0, column: 1, columnSpan: 1);
            Place(BadgesPanel, row: 0, column: 2, columnSpan: 1);
            Place(HeaderActions, row: 0, column: 3, columnSpan: 1);

            StationBadge.HorizontalAlignment = HorizontalAlignment.Center;
            StationBadge.Margin = new Thickness(0);
            BadgesPanel.Margin = new Thickness(0);
            HeaderActions.Margin = new Thickness(10, 0, 0, 0);
            BrandTagline.IsVisible = true;
        }
    }

    private void ApplyBodyLayout(bool narrow, bool dock)
    {
        // Take every card out of whatever currently holds it, then place it for the new layout.
        foreach (var card in new Control[] { HeroCard, LinksCard, RxTxRow, MetersCard, PttCard, ConnectCard, DtmfCard, LogCard, IdReminderCard, CompactLinksCard })
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

            // One tab at a time. Radio: slim links + RX/TX + meters (PTT is docked below). Nodes: connect, favourites,
            // full link rows. Keypad and Log are one card each.
            Control[] cards = _tab switch
            {
                Tab.Nodes => [ConnectCard, LinksCard],
                Tab.Keypad => [DtmfCard],
                Tab.Log => [LogCard],
                _ => [IdReminderCard, CompactLinksCard, RxTxRow, MetersCard],
            };
            foreach (var card in cards)
            {
                LeftColumn.Children.Add(card);
            }

            LogCard.Height = double.NaN;
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

        // On phones only the Radio tab carries the PTT key.
        bool showDock = dock && (!narrow || _tab == Tab.Radio);
        PttDock.Child = showDock ? PttCard : null;
        PttDock.IsVisible = showDock;
    }

    private void ApplyPttSizing(bool narrow, bool isShort)
    {
        bool phone = _narrow == true;   // phone layout: the key shares a row with the speaker chip, extras are hidden
        if (narrow)
        {
            PttRow.ColumnDefinitions = ColumnDefinitions.Parse("*,Auto");
            PttRow.HorizontalAlignment = HorizontalAlignment.Stretch;
            // A full-width bar the thumb can find without looking. On short screens the bar is slimmer and the
            // time-out/mode strip is hidden (the key face already shows the running transmit time).
            PttCard.Padding = new Thickness(isShort ? 8 : 12);
            PttCard.CornerRadius = new CornerRadius(12);
            PttInner.Spacing = 8;
            PttInner.HorizontalAlignment = HorizontalAlignment.Stretch;
            PttButton.Width = double.NaN;
            PttButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            PttButton.Height = isShort ? 64 : phone ? 112 : 96;
            PttInfo.Width = double.NaN;
            PttInfo.HorizontalAlignment = HorizontalAlignment.Stretch;
            PttInfo.IsVisible = !isShort && !phone;   // phones: nothing but the key and the chip (mode lives in Settings)
            PttCaption.IsVisible = false;
            PttModeHint.IsVisible = true;
        }
        else
        {
            PttRow.ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto");
            PttRow.HorizontalAlignment = HorizontalAlignment.Center;
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
        // Same stacked layout at every width (the card is only ~360 px wide even on desktop, so one row
        // left the node box too cramped): star + node box on the first row, then two equal full-width buttons.
        ConnectGrid.ColumnDefinitions = ColumnDefinitions.Parse("Auto,*");
        ConnectGrid.RowDefinitions = RowDefinitions.Parse("Auto,Auto");
        Place(TargetFavButtons, row: 0, column: 0, columnSpan: 1);
        Place(TargetBox, row: 0, column: 1, columnSpan: 1);
        Place(ConnectButtons, row: 1, column: 0, columnSpan: 2);
        TargetFavButtons.Margin = new Thickness(0, 0, 8, 8);
        TargetBox.Margin = new Thickness(0, 0, 0, 8);
        ConnectButtons.ColumnDefinitions = ColumnDefinitions.Parse("*,*");
        ConnectButtons.HorizontalAlignment = HorizontalAlignment.Stretch;
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
