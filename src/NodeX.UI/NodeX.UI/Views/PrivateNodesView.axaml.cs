using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using NodeX.UI.ViewModels;

namespace NodeX.UI.Views;

public partial class PrivateNodesView : UserControl
{
    public PrivateNodesView()
    {
        InitializeComponent();
    }

    private async void OnCopyRptConfClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PrivateNodesViewModel vm || string.IsNullOrEmpty(vm.RptConfLine)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try
        {
            await clipboard.SetTextAsync(vm.RptConfLine);
            vm.NoteRptConfCopied();
        }
        catch (Exception) { }
    }

    private async void OnCopyShareUriClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PrivateNodesViewModel vm || string.IsNullOrEmpty(vm.ShareUri)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try
        {
            await clipboard.SetTextAsync(vm.ShareUri);
            vm.NoteShareUriCopied();
        }
        catch (Exception) { }
    }
}
