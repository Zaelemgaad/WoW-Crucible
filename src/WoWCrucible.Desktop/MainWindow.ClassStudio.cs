using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace WoWCrucible.Desktop;

public partial class MainWindow
{
    private ClassStudioHost? _classStudio;
    private async void OpenClassStudioClick(object? sender, RoutedEventArgs e)
    {
        try { OpenClassStudio(); }
        catch (Exception exception) { await ShowErrorAsync("Class Studio", exception.Message); }
    }

    internal void OpenClassStudio()
    {
        if (_classStudio is null)
        {
            _classStudio = new(Path.Combine(AppContext.BaseDirectory, "ClassStudio"), file => _tableSchemas.Resolve(file).Schema, PickClassStudioFolderAsync, _workspaceSession);
            Closed += (_, _) => _classStudio.Dispose();
        }
        Process.Start(new ProcessStartInfo(_classStudio.Url) { UseShellExecute = true });
    }

    private async Task<string?> PickClassStudioFolderAsync(string kind)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = kind switch { "client" => "Select client root, Data or extracted DBC folder", "core" => "Select CoA core source checkout", _ => "Select server root, data or DBC folder" }, AllowMultiple = false });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        });
    }
}
