using Avalonia.Controls;
using Avalonia.Interactivity;
using NodeX.UI.ViewModels;

namespace NodeX.UI.Views;

public partial class CastView : UserControl
{
    public CastView()
    {
        InitializeComponent();
    }

    private void OnCastDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CastDeviceItem item } && DataContext is CastViewModel vm)
            vm.ConnectDeviceCommand.Execute(item);
    }
}
