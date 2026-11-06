using Avalonia.Controls;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

public partial class MainWindow
{
    private readonly List<MpqDocumentSource> _archiveDocuments = [];
    private readonly Dictionary<MpqDocumentSource, Control> _archiveEditors = [];
    private readonly HashSet<string> _openingArchiveDocuments = new(StringComparer.OrdinalIgnoreCase);

    private async Task OpenArchiveFileAsync(string archivePath, MpqFileEntry entry)
    {
        if (!CommitPendingDbcEdits()) return;
        archivePath = Path.GetFullPath(archivePath);
        var key = $"{archivePath}|{entry.ArchivePath}|{entry.Locale}";
        if (!_openingArchiveDocuments.Add(key)) return;
        try
        {
            var source = _archiveDocuments.FirstOrDefault(source =>
                source.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase) &&
                source.Entry.ArchivePath.Equals(entry.ArchivePath, StringComparison.OrdinalIgnoreCase) &&
                source.Entry.Locale == entry.Locale);
            if (source is null)
            {
                source = await Task.Run(() => new MpqDocumentSource(archivePath, entry));
                _archiveDocuments.Add(source);
            }
            var existing = _documents.FindIndex(document => ReferenceEquals(document.ArchiveSource, source));
            if (existing >= 0) { ActivateDocument(existing); return; }
            var title = $"{Path.GetFileName(source.WorkingPath)} [{Path.GetFileName(archivePath)}]";
            if (_archiveEditors.TryGetValue(source, out var editor)) { OpenFeatureWorkspace(editor, title); return; }
            var kind = await Task.Run(() => FileOpenCatalog.Detect(source.WorkingPath).Kind);
            if (kind == FileEditorKind.Table)
            {
                await LoadDbcAsync(source.WorkingPath, source);
                var document = _documents.FirstOrDefault(document => document.FullPath == source.WorkingPath);
                if (document is not null)
                {
                    StatusText.Text = $"{source.DisplayPath} · Save writes back to this MPQ entry; Save As creates a separate file.";
                }
                else { _archiveDocuments.Remove(source); source.Dispose(); }
                return;
            }
            // Independent asset tabs keep another opened member's preview intact.
            switch (kind)
            {
                case FileEditorKind.Texture:
                case FileEditorKind.Image:
                    var texture = new TextureWorkspaceView(_workspaceSession);
                    Register(texture);
                    if (kind == FileEditorKind.Texture) await texture.OpenAsync(source.WorkingPath);
                    else await texture.OpenImageAsync(source.WorkingPath);
                    break;
                case FileEditorKind.Model:
                    var model = new ModelBrowserView(_workspaceSession.Settings);
                    Register(model);
                    await model.OpenAsync(Path.GetDirectoryName(source.WorkingPath), source.WorkingPath);
                    break;
                case FileEditorKind.Map:
                    var map = new MapWorkspaceView(_workspaceSession);
                    Register(map);
                    await map.OpenAsync(source.WorkingPath);
                    break;
                case FileEditorKind.NativeAsset:
                    var native = new NativeConversionWorkspaceView(_workspaceSession.Settings);
                    Register(native);
                    await native.OpenAsync(source.WorkingPath);
                    break;
                case FileEditorKind.Sql:
                    var sql = new SqlWorkspaceView(_workspaceSession);
                    Register(sql);
                    sql.OpenScript(await File.ReadAllTextAsync(source.WorkingPath));
                    break;
                default:
                    await OpenDetectedFileAsync(source.WorkingPath);
                    break;
            }

            void Register(Control view)
            {
                _archiveEditors.Add(source, view);
                OpenFeatureWorkspace(view, title);
            }
        }
        catch (Exception error)
        {
            DesktopCrashLogger.Log("Open MPQ member failed", error);
            await ShowErrorAsync("Could not open MPQ member", error.Message);
        }
        finally { _openingArchiveDocuments.Remove(key); }
    }

    private void DisposeArchiveDocuments()
    {
        foreach (var editor in _archiveEditors.Values.OfType<IDisposable>()) editor.Dispose();
        foreach (var source in _archiveDocuments)
        {
            try { source.Dispose(); }
            catch (IOException error) { DesktopCrashLogger.Log("MPQ working copy cleanup failed", error); }
            catch (UnauthorizedAccessException error) { DesktopCrashLogger.Log("MPQ working copy cleanup failed", error); }
        }
    }

    private void ReleaseArchiveSource(DbcDocumentSession document)
    {
        if (document.ArchiveSource is not { } source) return;
        document.ArchiveSource = null;
        _archiveDocuments.Remove(source);
        try { source.Dispose(); }
        catch (IOException error) { DesktopCrashLogger.Log("MPQ working copy cleanup failed", error); }
        catch (UnauthorizedAccessException error) { DesktopCrashLogger.Log("MPQ working copy cleanup failed", error); }
    }
}
