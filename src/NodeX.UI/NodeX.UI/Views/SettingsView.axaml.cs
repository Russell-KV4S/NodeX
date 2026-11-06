using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NodeX.UI.ViewModels;

namespace NodeX.UI.Views;

public partial class SettingsView : UserControl
{
    private const string SuggestedFileName = "nodex-settings.json";

    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    /// <summary>Saves the settings on screen to a file the user picks (Android: the system "save to" sheet).</summary>
    private async void OnExportFileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        string? text = vm.BuildExport();
        if (text == null) return;

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null || !storage.CanSave)
            {
                vm.NoteTransferError("This device can't save files from here. Use “Copy to clipboard” instead.");
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export NodeX settings",
                SuggestedFileName = SuggestedFileName,
                DefaultExtension = "json",
                ShowOverwritePrompt = true
            });
            if (file == null) return;   // cancelled

            await using (var stream = await file.OpenWriteAsync())
            {
                stream.SetLength(0);
                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text));
            }
            vm.NoteExported("saved to a file");
        }
        catch (Exception ex)
        {
            vm.NoteTransferError($"Could not save the file: {ex.Message}");
        }
    }

    /// <summary>Loads settings from a file into the form (nothing is saved until the user presses Save).</summary>
    private async void OnImportFileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null || !storage.CanOpen)
            {
                vm.NoteTransferError("This device can't open files from here. Use “Paste from clipboard” instead.");
                return;
            }

            // No type filter: Android pickers often mislabel .json files shared from other apps.
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import NodeX settings",
                AllowMultiple = false
            });
            var file = files.FirstOrDefault();
            if (file == null) return;   // cancelled

            string text;
            await using (var stream = await file.OpenReadAsync())
            {
                if (stream.CanSeek && stream.Length > 256 * 1024)
                {
                    vm.NoteTransferError("That file is too large to be a NodeX settings file.");
                    return;
                }
                using var reader = new StreamReader(stream, Encoding.UTF8);
                text = await reader.ReadToEndAsync();
            }
            vm.ApplyImport(text);
        }
        catch (Exception ex)
        {
            vm.NoteTransferError($"Could not read the file: {ex.Message}");
        }
    }

    /// <summary>Copies the export text, handy for sending to yourself in a chat or e-mail.</summary>
    private async void OnCopyExportClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        string? text = vm.BuildExport();
        if (text == null) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                vm.NoteTransferError("The clipboard isn't available.");
                return;
            }
            await clipboard.SetTextAsync(text);
            vm.NoteExported("copied to the clipboard");
        }
        catch (Exception ex)
        {
            vm.NoteTransferError($"Could not copy: {ex.Message}");
        }
    }

    private async void OnPasteImportClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                vm.NoteTransferError("The clipboard isn't available.");
                return;
            }
            vm.ApplyImport(await clipboard.TryGetTextAsync());
        }
        catch (Exception ex)
        {
            vm.NoteTransferError($"Could not read the clipboard: {ex.Message}");
        }
    }
}
