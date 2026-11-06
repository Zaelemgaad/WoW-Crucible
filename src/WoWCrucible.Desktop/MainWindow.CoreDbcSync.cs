using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

public partial class MainWindow
{
    private void InitializeFileDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) => e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(this, async (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths is not { Length: > 0 }) return;
            e.Handled = true;
            await RouteDroppedPathsAsync(paths);
        });
    }

    internal async Task RouteDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        bool Is(string path, string extension) => File.Exists(path) && Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
        if (paths.Count == 1 && Is(paths[0], ".mpq") || paths.All(path => Is(path, ".dbc")))
            foreach (var path in paths) await LoadPathAsync(path);
        else OpenPatchBuilderWithPaths(paths);
    }

    private async void SyncToCoreDataClick(object? sender, RoutedEventArgs e)
    {
        if (Current is not { } document || !CommitPendingDbcEdits()) return;
        try
        {
            if (document.File.IsDirty)
            {
                if (!await ConfirmAsync("Save before syncing?", $"Save {Path.GetFileName(document.File.SourcePath)} first? Only the saved file can be copied to core data.")) return;
                if (!await SaveCurrentAsync(false) || document.File.IsDirty) return;
            }
            var coreDbc = _workspaceSession.Settings.CoreDbcPath;
            var start = Directory.Exists(coreDbc) ? await StorageProvider.TryGetFolderFromPathAsync(coreDbc) : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the core's data\\dbc directory", AllowMultiple = false, SuggestedStartLocation = start });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } directory) StatusText.Text = SyncDbcToDirectory(document.File.SourcePath, directory);
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("Sync to core data failed", exception);
            await ShowErrorAsync("Could not sync to core data", exception.Message);
        }
    }

    internal static string SyncDbcToDirectory(string source, string directory)
    {
        var destination = Path.Combine(directory, Path.GetFileName(source));
        if (Path.GetFullPath(destination).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) return "The open DBC is already in that core data directory.";
        var backup = File.Exists(destination) ? CrucibleBackupService.Create(destination, "CoreDbcSync") : null;
        var temporary = destination + ".tmp";
        File.Copy(source, temporary, true);
        File.Move(temporary, destination, true);
        return backup is null ? $"Synchronized to {destination} · no retained backup" : $"Synchronized to {destination} · backup {backup}";
    }
}
