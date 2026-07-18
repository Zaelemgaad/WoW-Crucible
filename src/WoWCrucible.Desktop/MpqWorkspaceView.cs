using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class MpqWorkspaceView : UserControl, IDisposable
{
    private sealed class PatchRow(string sourcePath, string archivePath)
    {
        public string SourcePath { get; } = sourcePath;
        public string ArchivePath { get; set; } = archivePath;
        public string Assessment => SafeAssessment(ArchivePath);
        private static string SafeAssessment(string path) { try { return PatchInputMapper.AssessArchivePath(path).Message; } catch (Exception exception) { return exception.Message; } }
    }

    private readonly DesktopWorkspaceSession _session;
    private readonly TabControl _tabs = new() { Margin = new Thickness(10) };
    private readonly List<PatchRow> _entries = [];
    private readonly ListBox _builderItems = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _allowed = new() { AcceptsReturn = true, PlaceholderText = "Optional allowed globs, one per line" };
    private readonly TextBox _forbidden = new() { AcceptsReturn = true, PlaceholderText = "Optional forbidden globs, one per line" };
    private readonly TextBox _required = new() { AcceptsReturn = true, PlaceholderText = "Optional required globs, one per line" };
    private readonly NumericUpDown _expectedCount = new() { Minimum = 0, Maximum = 10_000_000, AllowSpin = true };
    private readonly TextBlock _builderStatus = StatusText("Build a tiny manifest-driven patch. Large source archives remain immutable.");
    private readonly TextBlock _bindingStatus = StatusText("No client executable binding.");
    private string? _existingPatch;
    private string? _requiredClientExecutableSha256;

    private readonly TextBox _archivePath = new() { PlaceholderText = "MPQ archive" };
    private readonly TextBox _externalListfile = new() { PlaceholderText = "Optional external listfile" };
    private readonly TextBox _browserSearch = new() { PlaceholderText = "Filter paths or use * and ? wildcards…" };
    private readonly ListBox _browserItems = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _browserStatus = StatusText("Open an MPQ to browse it.");
    private IReadOnlyList<MpqFileEntry> _allArchiveEntries = [];
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;

    public MpqWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session;
        _builderItems.ItemTemplate = new FuncDataTemplate<PatchRow>((row, _) => row is null ? new Grid() : BuildPatchRow(row));
        _browserItems.ItemTemplate = new FuncDataTemplate<MpqFileEntry>((entry, _) => entry is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(3,2), Children =
            {
                new TextBlock { Text = entry.ArchivePath, TextTrimming = TextTrimming.CharacterEllipsis },
                WithColumn(new TextBlock { Text = FormatBytes(entry.Size) }, 1),
                WithColumn(new TextBlock { Text = FormatBytes(entry.CompressedSize) }, 2),
                WithColumn(new TextBlock { Text = entry.IsMetadata ? "metadata" : entry.Size == 0 ? "—" : $"{100d * entry.CompressedSize / entry.Size:0.#}%" }, 3)
            }
        });
        _browserSearch.TextChanged += (_, _) => FilterArchive();

        var back = new Button { Content = "← Editor", HorizontalAlignment = HorizontalAlignment.Left }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12,8), Children = { back, WithColumn(new TextBlock { Text = "MPQ PATCHES & ARCHIVES", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0) }, 1) } };
        _tabs.Items.Add(new TabItem { Header = "Patch builder", Content = BuildBuilderPage() });
        _tabs.Items.Add(new TabItem { Header = "Archive browser", Content = BuildBrowserPage() });
        _tabs.Items.Add(new TabItem { Header = "Client deployment", Content = BuildDeploymentPage() });
        Content = new Grid { RowDefinitions = new("Auto,*"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,0,0,1), Child = heading }, WithRow(_tabs, 1) } };
    }

    public async Task OpenArchiveAsync(string path)
    {
        _archivePath.Text = Path.GetFullPath(path);
        _tabs.SelectedIndex = 1;
        await LoadArchiveAsync();
    }

    public void StagePaths(IEnumerable<string> paths)
    {
        _tabs.SelectedIndex = 0;
        AddPaths(paths);
    }

    private Control BuildBuilderPage()
    {
        var addFiles = new Button { Content = "Add files" }; addFiles.Click += async (_, _) => await AddFilesAsync();
        var addFolder = new Button { Content = "Add folder tree" }; addFolder.Click += async (_, _) => await AddFolderAsync();
        var openExisting = new Button { Content = "Update existing small MPQ" }; openExisting.Click += async (_, _) => await ChooseExistingPatchAsync();
        var remove = new Button { Content = "Remove selected" }; remove.Click += (_, _) => RemoveSelected();
        var clear = new Button { Content = "Clear" }; clear.Click += (_, _) => { _entries.Clear(); _existingPatch = null; RefreshBuilder(); };
        var loadManifest = new Button { Content = "Load manifest" }; loadManifest.Click += async (_, _) => await LoadManifestAsync();
        var saveManifest = new Button { Content = "Save manifest" }; saveManifest.Click += async (_, _) => await SaveManifestAsync();
        var bind = new Button { Content = "Bind client Wow.exe" }; bind.Click += async (_, _) => await BindClientAsync();
        var build = AccentButton("Build / update MPQ"); build.Click += async (_, _) => await BuildPatchAsync();
        var toolbar = new WrapPanel { Children = { addFiles, addFolder, openExisting, remove, clear, loadManifest, saveManifest, bind, build } };
        var policy = new Expander { Header = "Manifest content policy", Content = new Grid { ColumnDefinitions = new("*,*,*,Auto"), ColumnSpacing = 8, Children = { _allowed, WithColumn(_forbidden, 1), WithColumn(_required, 2), WithColumn(new StackPanel { Children = { new TextBlock { Text = "Expected count" }, _expectedCount } }, 3) } } };
        var dropTarget = new Border
        {
            BorderBrush = Brush.Parse("#293347"),
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                RowDefinitions = new("Auto,*"),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Drop edited DBCs, UI files, folders, or an entire prepared patch tree here. Folder names are preserved as MPQ paths.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush.Parse("#8995A9"),
                        Margin = new Thickness(8, 6)
                    },
                    WithRow(_builderItems, 1)
                }
            }
        };
        DragDrop.SetAllowDrop(dropTarget, true);
        DragDrop.AddDragOverHandler(dropTarget, (_, args) =>
        {
            args.DragEffects = args.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        });
        DragDrop.AddDropHandler(dropTarget, (_, args) =>
        {
            var paths = args.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray() ?? [];
            if (paths.Length > 0) AddPaths(paths);
            args.Handled = true;
        });
        return new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(8), RowSpacing = 8, Children = { toolbar, WithRow(policy, 1), WithRow(dropTarget, 2), WithRow(_bindingStatus, 3), WithRow(_builderStatus, 4) } };
    }

    private Control BuildBrowserPage()
    {
        var open = new Button { Content = "Open MPQ" }; open.Click += async (_, _) => await ChooseArchiveAsync();
        var listfile = new Button { Content = "External listfile…" }; listfile.Click += async (_, _) => await ChooseListfileAsync();
        var extractSelected = new Button { Content = "Extract selected" }; extractSelected.Click += async (_, _) => await ExtractAsync(_browserItems.SelectedItems?.OfType<MpqFileEntry>().ToArray() ?? []);
        var extractAll = AccentButton("Extract all visible"); extractAll.Click += async (_, _) => await ExtractAsync((_browserItems.ItemsSource as IEnumerable<MpqFileEntry>)?.ToArray() ?? []);
        var cancel = new Button { Content = "Cancel operation" }; cancel.Click += (_, _) => _operation?.Cancel();
        var toolbar = new WrapPanel { Children = { open, listfile, extractSelected, extractAll, cancel } };
        var paths = new Grid { ColumnDefinitions = new("2*,*,2*"), ColumnSpacing = 8, Children = { _archivePath, WithColumn(_externalListfile, 1), WithColumn(_browserSearch, 2) } };
        return new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(8), RowSpacing = 8, Children = { toolbar, WithRow(paths, 1), WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _browserItems }, 2), WithRow(_browserStatus, 3) } };
    }

    private Control BuildDeploymentPage()
    {
        var clientRoot = new TextBox { Text = ClientRootFromSettings(), PlaceholderText = "WotLK client root containing Data and Wow.exe" };
        var patch = new TextBox { PlaceholderText = "Patch MPQ to install" };
        var targetName = new TextBox { Text = "patch-W.MPQ", PlaceholderText = "Target MPQ filename" };
        var status = StatusText("Installation verifies SHA-256, backs up an existing target, and always deletes the entire client Cache folder.");
        var browseClient = new Button { Content = "Client…" }; browseClient.Click += async (_, _) => { var path = await PickFolderAsync("Select the WotLK client root"); if (path is not null) clientRoot.Text = path; };
        var browsePatch = new Button { Content = "Patch…" }; browsePatch.Click += async (_, _) => { var path = await PickFileAsync("Select the patch MPQ", ["*.mpq"]); if (path is not null) { patch.Text = path; targetName.Text = Path.GetFileName(path); } };
        var install = AccentButton("Install patch and clear Cache"); install.Click += async (_, _) =>
        {
            try { status.Text = "Installing and verifying patch…"; var patchPath = patch.Text ?? string.Empty; var root = clientRoot.Text ?? string.Empty; var name = targetName.Text; var result = await Task.Run(() => ClientPatchDeploymentService.Install(patchPath, root, name)); status.Text = $"Installed {result.InstalledPath}\nSHA-256 {result.Sha256}\nCache: {(result.Cache.Existed ? $"deleted {result.Cache.DeletedFiles:N0} files / {FormatBytes(result.Cache.DeletedBytes)}" : "already absent")}"; }
            catch (Exception exception) { status.Text = $"Install failed: {exception.Message}"; DesktopCrashLogger.Log("Client patch install failed", exception); }
        };
        var clearCache = new Button { Content = "Clear Cache now" }; clearCache.Click += async (_, _) =>
        {
            try { var root = clientRoot.Text ?? string.Empty; var result = await Task.Run(() => ClientPatchDeploymentService.InvalidateCache(root)); status.Text = result.Existed ? $"Deleted the entire Cache folder: {result.DeletedFiles:N0} files / {FormatBytes(result.DeletedBytes)}." : "Cache was already absent."; }
            catch (Exception exception) { status.Text = $"Cache clear failed: {exception.Message}"; DesktopCrashLogger.Log("Client cache clear failed", exception); }
        };
        var form = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), RowSpacing = 8, ColumnSpacing = 8 };
        AddField(form, 0, "Client root", clientRoot, browseClient); AddField(form, 1, "Patch MPQ", patch, browsePatch); AddField(form, 2, "Target name", targetName, null);
        return new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { new TextBlock { Text = "Atomic client deployment", FontSize = 22, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Use this after building or selecting a patch. The game client is treated as a deploy target; the source patch remains unchanged.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") }, form, new WrapPanel { Children = { install, clearCache } }, status } };
    }

    private Grid BuildPatchRow(PatchRow row)
    {
        var archive = new TextBox { Text = row.ArchivePath };
        var assessment = new TextBlock { Text = row.Assessment, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7") };
        archive.TextChanged += (_, _) => { row.ArchivePath = archive.Text ?? string.Empty; assessment.Text = row.Assessment; };
        return new Grid { ColumnDefinitions = new("2*,2*,*"), ColumnSpacing = 8, Margin = new Thickness(3,2), Children = { new TextBlock { Text = row.SourcePath, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, WithColumn(archive, 1), WithColumn(assessment, 2) } };
    }

    private async Task AddFilesAsync() { var storage = Storage(); var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add files to the MPQ patch", AllowMultiple = true }); AddPaths(files.Select(file => file.TryGetLocalPath()).Where(path => path is not null)!); }
    private async Task AddFolderAsync() { var path = await PickFolderAsync("Add a folder tree and preserve client-relative paths"); if (path is not null) AddPaths([path]); }
    private void AddPaths(IEnumerable<string> paths)
    {
        try
        {
            foreach (var mapped in PatchInputMapper.Map(paths)) { var existing = _entries.FindIndex(row => row.ArchivePath.Equals(mapped.ArchivePath, StringComparison.OrdinalIgnoreCase)); var row = new PatchRow(mapped.SourcePath, mapped.ArchivePath); if (existing >= 0) _entries[existing] = row; else _entries.Add(row); }
            RefreshBuilder();
        }
        catch (Exception exception) { _builderStatus.Text = exception.Message; DesktopCrashLogger.Log("Patch input mapping failed", exception); }
    }

    private void RemoveSelected() { var selected = _builderItems.SelectedItems?.OfType<PatchRow>().ToHashSet() ?? []; _entries.RemoveAll(selected.Contains); RefreshBuilder(); }
    private void RefreshBuilder() { _builderItems.ItemsSource = _entries.OrderBy(row => row.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray(); var warnings = _entries.Count(row => PatchInputMapper.AssessArchivePath(row.ArchivePath).HasWarning); _builderStatus.Text = $"{_entries.Count:N0} file(s) · {warnings:N0} path warning(s) · {(_existingPatch is null ? "new tiny MPQ" : $"transaction-safe update of {Path.GetFileName(_existingPatch)}")}"; }
    private PatchEntry[] CurrentEntries() => _entries.Select(row => new PatchEntry(row.SourcePath, row.ArchivePath)).ToArray();
    private PatchManifestPolicy? CurrentPolicy()
    {
        static string[] Lines(string? text) => (text ?? string.Empty).Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowed = Lines(_allowed.Text); var forbidden = Lines(_forbidden.Text); var required = Lines(_required.Text); var expected = _expectedCount.Value is > 0 ? (int?)_expectedCount.Value.Value : null;
        return allowed.Length == 0 && forbidden.Length == 0 && required.Length == 0 && expected is null ? null : new(allowed, forbidden, expected, required);
    }

    private async Task ChooseExistingPatchAsync() { var path = await PickFileAsync("Select a deliberately small patch MPQ to update", ["*.mpq"]); if (path is null) return; _existingPatch = path; _builderStatus.Text = $"Updating {path}. Existing entries remain; matching internal paths are replaced. Archives above {PatchArchiveService.MaximumSafeUpdateBytes / (1024 * 1024):N0} MiB are refused."; }
    private async Task BindClientAsync() { var path = await PickFileAsync("Bind a compatible build-12340 Wow.exe", ["Wow.exe", "*.exe"]); if (path is null) return; _requiredClientExecutableSha256 = await Task.Run(() => PatchManifestService.ComputeExecutableSha256(path)); _bindingStatus.Text = $"Bound {Path.GetFileName(path)} · SHA-256 {_requiredClientExecutableSha256}"; }

    private async Task SaveManifestAsync()
    {
        try
        {
            var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save Crucible patch manifest", SuggestedFileName = "patch.crucible-patch.json", FileTypeChoices = [new FilePickerFileType("Crucible patch manifest") { Patterns = ["*.crucible-patch.json", "*.json"] }] }); var path = file?.TryGetLocalPath(); if (path is null) return;
            PatchManifestService.Save(path, Path.GetFileNameWithoutExtension(path), _existingPatch is null ? "patch-W.mpq" : Path.GetFileName(_existingPatch), CurrentEntries(), _requiredClientExecutableSha256, CurrentPolicy()); _builderStatus.Text = $"Saved manifest {path}";
        }
        catch (Exception exception) { _builderStatus.Text = $"Manifest save failed: {exception.Message}"; DesktopCrashLogger.Log("Manifest save failed", exception); }
    }

    private async Task LoadManifestAsync()
    {
        try
        {
            var path = await PickFileAsync("Load Crucible patch manifest", ["*.crucible-patch.json", "*.json"]); if (path is null) return; var manifest = await Task.Run(() => PatchManifestService.Load(path));
            _entries.Clear(); _entries.AddRange(manifest.Entries.Select(entry => new PatchRow(entry.SourcePath, entry.ArchivePath))); _existingPatch = null; _requiredClientExecutableSha256 = manifest.RequiredClientExecutableSha256; ApplyPolicy(manifest.Policy); _bindingStatus.Text = _requiredClientExecutableSha256 is null ? "No client executable binding." : $"Required client SHA-256 {_requiredClientExecutableSha256}"; RefreshBuilder();
        }
        catch (Exception exception) { _builderStatus.Text = $"Manifest load failed: {exception.Message}"; DesktopCrashLogger.Log("Manifest load failed", exception); }
    }

    private void ApplyPolicy(PatchManifestPolicy? policy) { _allowed.Text = string.Join(Environment.NewLine, policy?.AllowedGlobs ?? []); _forbidden.Text = string.Join(Environment.NewLine, policy?.ForbiddenGlobs ?? []); _required.Text = string.Join(Environment.NewLine, policy?.RequiredGlobs ?? []); _expectedCount.Value = policy?.ExpectedEntryCount ?? 0; }

    private async Task BuildPatchAsync()
    {
        try
        {
            var entries = CurrentEntries(); var validation = PatchManifestService.ValidateEntries(entries, CurrentPolicy()); if (!validation.Passed) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
            var output = _existingPatch;
            if (output is null) { var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Build tiny patch MPQ", SuggestedFileName = "patch-W.MPQ", FileTypeChoices = [new FilePickerFileType("MPQ patch") { Patterns = ["*.mpq"] }] }); output = file?.TryGetLocalPath(); if (output is null) return; }
            _builderStatus.Text = $"Building {entries.Length:N0} manifest entries…"; var service = new PatchArchiveService(); var update = _existingPatch is not null; await Task.Run(() => { if (update) service.Update(output, entries); else service.Create(output, entries); });
            ClientCacheInvalidationResult? cache = null; if (ClientPatchDeploymentService.IsInsideClientData(output, _session.Settings.ClientDataPath)) { var root = Directory.GetParent(_session.Settings.ClientDataPath)?.FullName; if (root is not null) cache = await Task.Run(() => ClientPatchDeploymentService.InvalidateCache(root)); }
            _builderStatus.Text = $"{(update ? "Updated" : "Created")} {output} with {entries.Length:N0} file(s).{(cache is null ? string.Empty : cache.Existed ? $" Deleted the entire client Cache ({cache.DeletedFiles:N0} files)." : " Client Cache was already absent.")}";
        }
        catch (Exception exception) { _builderStatus.Text = $"Build failed: {exception.Message}"; DesktopCrashLogger.Log("MPQ build failed", exception); }
    }

    private async Task ChooseArchiveAsync() { var path = await PickFileAsync("Open an MPQ archive", ["*.mpq"]); if (path is null) return; _archivePath.Text = path; await LoadArchiveAsync(); }
    private async Task ChooseListfileAsync() { var path = await PickFileAsync("Select an external MPQ listfile", ["*.txt", "*"]); if (path is null) return; _externalListfile.Text = path; if (File.Exists(_archivePath.Text)) await LoadArchiveAsync(); }
    private async Task LoadArchiveAsync()
    {
        BeginOperation();
        try { _browserStatus.Text = "Reading archive index…"; var archive = _archivePath.Text ?? string.Empty; var listfile = string.IsNullOrWhiteSpace(_externalListfile.Text) ? null : _externalListfile.Text; _allArchiveEntries = await Task.Run(() => new PatchArchiveService().ListFiles(archive, "*", listfile), _operation!.Token); FilterArchive(); _browserStatus.Text = $"Loaded {_allArchiveEntries.Count:N0} entries from {archive}."; }
        catch (Exception exception) { _browserStatus.Text = $"Open failed: {exception.Message}"; DesktopCrashLogger.Log("MPQ open failed", exception); }
    }

    private void FilterArchive() { var filtered = _allArchiveEntries.Where(entry => MpqPathFilter.Matches(entry.ArchivePath, _browserSearch.Text)).ToArray(); _browserItems.ItemsSource = filtered; if (_allArchiveEntries.Count > 0) _browserStatus.Text = $"Showing {filtered.Length:N0} of {_allArchiveEntries.Count:N0} archive entries."; }
    private async Task ExtractAsync(IReadOnlyList<MpqFileEntry> entries)
    {
        if (entries.Count == 0) { _browserStatus.Text = "Select at least one archive entry."; return; } var destination = await PickFolderAsync("Select extraction destination"); if (destination is null) return; BeginOperation();
        try { var archivePath = _archivePath.Text ?? string.Empty; var progress = new Progress<(int Done, int Total, string Path)>(value => _browserStatus.Text = $"Extracting {value.Done:N0}/{value.Total:N0} · {value.Path}"); await Task.Run(() => new PatchArchiveService().Extract(archivePath, destination, entries, progress, _operation!.Token)); _browserStatus.Text = $"Extracted {entries.Count:N0} entries to {destination}."; }
        catch (OperationCanceledException) { _browserStatus.Text = "Extraction cancelled."; }
        catch (Exception exception) { _browserStatus.Text = $"Extraction failed: {exception.Message}"; DesktopCrashLogger.Log("MPQ extraction failed", exception); }
    }

    private void BeginOperation() { _operation?.Cancel(); _operation?.Dispose(); _operation = new(); }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The MPQ workspace is not attached to the main window.");
    private async Task<string?> PickFolderAsync(string title) { var result = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }); return result.FirstOrDefault()?.TryGetLocalPath(); }
    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns) { var result = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }] }); return result.FirstOrDefault()?.TryGetLocalPath(); }
    private string ClientRootFromSettings() { var data = _session.Settings.ClientDataPath; return Directory.Exists(data) ? Directory.GetParent(data)?.FullName ?? string.Empty : string.Empty; }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); }

    private static TextBlock StatusText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KiB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MiB" : $"{bytes / (1024d * 1024 * 1024):0.##} GiB";
    private static void AddField(Grid grid, int row, string label, Control field, Control? button) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text,row); grid.Children.Add(text); Grid.SetRow(field,row); Grid.SetColumn(field,1); grid.Children.Add(field); if (button is not null) { Grid.SetRow(button,row); Grid.SetColumn(button,2); grid.Children.Add(button); } }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
