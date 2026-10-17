using Avalonia.Platform.Storage;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

public partial class MainWindow
{
    private ClassStudioView? _classStudio;

    internal void OpenClassStudio()
    {
        if (_classStudio is null)
        {
            var session = new ClassStudioSession(file => _tableSchemas.Resolve(file).Schema,
                PickClassStudioFolderAsync, ExportClassStudioAsync, _workspaceSession);
            _classStudio = new ClassStudioView(Path.Combine(AppContext.BaseDirectory, "ClassStudio"), session);
            _classStudio.ShellKeyRequested += (_, e) => OnKeyDown(e);
            // Native child windows cannot be covered by Avalonia's in-window overlays.
            DialogOverlayHost.PropertyChanged += UpdateClassStudioOcclusion;
            CommandPaletteHost.PropertyChanged += UpdateClassStudioOcclusion;
            Closed += (_, _) => _classStudio.Dispose();
        }
        OpenFeatureWorkspace(_classStudio, "Class Studio");
    }

    private void UpdateClassStudioOcclusion(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Avalonia.Controls.Control.IsVisibleProperty)
            _classStudio?.SetOccluded(DialogOverlayHost.IsVisible || CommandPaletteHost.IsVisible);
    }

    private async Task<string?> PickClassStudioFolderAsync(string kind)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = kind switch { "client" => "Select client root, Data or extracted DBC folder", "core" => "Select CoA core source checkout", _ => "Select server root, data or DBC folder" }, AllowMultiple = false });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<bool> ExportClassStudioAsync(string name, string text)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "Export Class Studio", SuggestedFileName = name, ShowOverwritePrompt = true });
        if (file is null) return false;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
        return true;
    }
}
