using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class NativeConversionWorkspaceView : UserControl, IDisposable
{
    private static readonly EnumerationOptions RecursiveFiles = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    private readonly ListBox _assets = new();
    private readonly TextBlock _summary = Status("Drop M2/WMO files or folders here, or add them with the buttons above.");
    private readonly TextBlock _previewStatus = Status("Select a compatible Wrath M2 or version-17 WMO for a live geometry preview.");
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly M2PreviewView _preview = new();
    private readonly WmoPreviewView _wmoPreview = new() { IsVisible = false };
    private readonly Grid _previewHost = new();
    private readonly List<AssetInspection> _inspections = [];
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _previewOperation;
    private NativeConversionWorkspace? _workspace;
    private int _previewGeneration;

    public event EventHandler? BackRequested;

    public NativeConversionWorkspaceView()
    {
        _previewHost.Children.Add(_preview); _previewHost.Children.Add(_wmoPreview);
        _assets.ItemTemplate = new FuncDataTemplate<AssetInspection>((asset, _) =>
        {
            if (asset is null) return new TextBlock();
            var panel = new StackPanel { Spacing = 2, Margin = new Thickness(5, 4) };
            panel.Children.Add(new TextBlock { Text = Path.GetFileName(asset.Path), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock
            {
                Text = $"{asset.Format} · {CompatibilityLabel(asset.Compatibility)} · {asset.Size:N0} bytes · {asset.Dependencies.Count:N0} dependencies",
                Foreground = Brush.Parse(CompatibilityColor(asset.Compatibility)), TextWrapping = TextWrapping.Wrap, FontSize = 10
            });
            panel.Children.Add(new TextBlock { Text = asset.Path, Foreground = Brush.Parse("#7F8A9F"), TextWrapping = TextWrapping.Wrap, FontSize = 10 });
            return panel;
        });
        _assets.SelectionChanged += async (_, _) => await ShowSelectedAsync();

        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var addFiles = Accent("Add M2/WMO files"); addFiles.Click += async (_, _) => await AddFilesAsync();
        var addFolder = new Button { Content = "Add folder recursively" }; addFolder.Click += async (_, _) => await AddFolderAsync();
        var remove = new Button { Content = "Remove selected" }; remove.Click += (_, _) => RemoveSelected();
        var clear = new Button { Content = "Clear" }; clear.Click += (_, _) => Clear();
        var create = Accent("Create verified workspace"); create.Click += async (_, _) => await CreateWorkspaceAsync();
        var open = new Button { Content = "Open conversion report" }; open.Click += async (_, _) => await OpenReportAsync();
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _operation?.Cancel();

        var header = new Border
        {
            BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8),
            Child = new WrapPanel { Children = { back, new TextBlock { Text = "MODERN ASSET CONVERSION", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, addFiles, addFolder, remove, clear, create, open, cancel } }
        };

        var dropTarget = new Border
        {
            BorderBrush = Brush.Parse("#35506F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(12, 9, 12, 0),
            Child = new TextBlock { Text = "DROP M2 / WMO FILES, FOLDERS, OR conversion-report.json", HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#94B9E4") }
        };
        DragDrop.SetAllowDrop(dropTarget, true);
        DragDrop.AddDragOverHandler(dropTarget, (_, args) => { args.DragEffects = args.DataTransfer.TryGetFiles()?.Any() == true ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; });
        DragDrop.AddDropHandler(dropTarget, async (_, args) =>
        {
            var paths = args.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
            if (paths.Length == 1 && Path.GetFileName(paths[0]).Equals("conversion-report.json", StringComparison.OrdinalIgnoreCase)) await LoadReportAsync(paths[0]);
            else await AnalyzeAsync(paths);
            args.Handled = true;
        });

        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 7, Children = { _summary, WithRow(_assets, 1) } };
        var right = new Grid
        {
            RowDefinitions = new("*,Auto,*"), RowSpacing = 5,
            Children =
            {
                new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = new Grid { RowDefinitions = new("*,Auto"), Children = { _previewHost, WithRow(new Border { Padding = new Thickness(8), Background = Brush.Parse("#101722"), Child = _previewStatus }, 1) } } },
                WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 1),
                WithRow(_details, 2)
            }
        };
        var body = new Grid
        {
            ColumnDefinitions = new("*,Auto,2*"), ColumnSpacing = 5, Margin = new Thickness(12, 9, 12, 12),
            Children = { left, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(right, 2) }
        };
        Content = new Grid { RowDefinitions = new("Auto,Auto,*"), Children = { header, WithRow(dropTarget, 1), WithRow(body, 2) } };
    }

    private async Task AddFilesAsync()
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add M2/WMO assets", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("WoW models") { Patterns = ["*.m2", "*.wmo"] }]
        });
        await AnalyzeAsync(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    private async Task AddFolderAsync()
    {
        var folder = (await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Scan a model folder recursively", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        if (folder is not null) await AnalyzeAsync([folder]);
    }

    private async Task AnalyzeAsync(IEnumerable<string> inputs)
    {
        var requested = inputs.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) return;
        var operation = BeginOperation("Discovering and inspecting assets…"); var token = operation.Token;
        try
        {
            var known = _inspections.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = await Task.Run(() =>
            {
                var assets = new List<AssetInspection>(); var failures = new List<string>();
                foreach (var path in Expand(requested).Where(path => !known.Contains(path)))
                {
                    token.ThrowIfCancellationRequested();
                    try { assets.Add(NativeAssetConversionService.Inspect(path)); }
                    catch (Exception exception) { failures.Add($"{path}: {exception.Message}"); }
                }
                token.ThrowIfCancellationRequested();
                return (Assets: assets, Failures: failures);
            }, token);
            _inspections.AddRange(result.Assets); _inspections.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
            RefreshList();
            _summary.Text = SummaryText() + (result.Failures.Count == 0 ? string.Empty : $" · {result.Failures.Count:N0} failed (see details)");
            if (result.Failures.Count > 0) _details.Text = "INSPECTION FAILURES\n\n" + string.Join("\n", result.Failures);
            DesktopCrashLogger.Debug("CONVERT", "asset-inspection-complete", ("added", result.Assets.Count), ("failed", result.Failures.Count), ("total", _inspections.Count));
        }
        catch (OperationCanceledException) { _summary.Text = "Asset inspection cancelled. Already completed results were not partially added."; }
        finally { EndOperation(operation); }
    }

    private async Task CreateWorkspaceAsync()
    {
        if (_inspections.Count == 0) { _summary.Text = "Add at least one asset first."; return; }
        var output = (await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select or create an empty conversion workspace folder", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        if (output is null) return;
        var operation = BeginOperation("Copying verified immutable source snapshots…"); var token = operation.Token;
        try
        {
            _workspace = await Task.Run(() => NativeAssetConversionService.CreateWorkspace(_inspections.Select(item => item.Path), output, token), token);
            _inspections.Clear(); _inspections.AddRange(_workspace.Assets); RefreshList();
            _summary.Text = $"Verified workspace created · {_workspace.CompatibleAssets:N0} Wrath-ready · {_workspace.ConversionRequired:N0} require conversion · {_workspace.BlockedAssets:N0} blocked · {_workspace.RootPath}";
            DesktopCrashLogger.Debug("CONVERT", "workspace-created", ("root", _workspace.RootPath), ("assets", _workspace.Assets.Count));
        }
        catch (OperationCanceledException) { _summary.Text = "Workspace creation cancelled before publication."; }
        catch (Exception exception) { _summary.Text = $"Workspace creation failed: {exception.Message}"; DesktopCrashLogger.Log("Native conversion workspace creation failed", exception); }
        finally { EndOperation(operation); }
    }

    private async Task OpenReportAsync()
    {
        var file = (await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open a Crucible conversion report", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Conversion report") { Patterns = ["*.json"] }] })).FirstOrDefault()?.TryGetLocalPath();
        if (file is not null) await LoadReportAsync(file);
    }

    private async Task LoadReportAsync(string path)
    {
        var operation = BeginOperation("Verifying every stored workspace snapshot…"); var token = operation.Token;
        try
        {
            _workspace = await Task.Run(() => NativeAssetConversionService.LoadWorkspace(path, cancellationToken: token), token);
            _inspections.Clear(); _inspections.AddRange(_workspace.Assets); RefreshList();
            _summary.Text = $"Verified {_workspace.Assets.Count:N0} immutable snapshot(s) · {_workspace.RootPath}";
        }
        catch (OperationCanceledException) { _summary.Text = "Workspace verification cancelled."; }
        catch (Exception exception) { _summary.Text = $"Workspace verification failed: {exception.Message}"; DesktopCrashLogger.Log("Native conversion workspace verification failed", exception); }
        finally { EndOperation(operation); }
    }

    private async Task ShowSelectedAsync()
    {
        _previewOperation?.Cancel(); _previewOperation?.Dispose(); _previewOperation = new CancellationTokenSource(); var previewToken = _previewOperation.Token;
        var generation = ++_previewGeneration; _preview.ClearGeometry(); _wmoPreview.ClearGeometry(); _preview.IsVisible = true; _wmoPreview.IsVisible = false;
        if (_assets.SelectedItem is not AssetInspection asset) { _details.Text = string.Empty; _previewStatus.Text = "Select an asset."; return; }
        var source = File.Exists(asset.Path) ? asset.Path : _workspace is not null ? NativeAssetConversionService.ResolveSnapshotPath(_workspace, asset) : asset.Path;
        _details.Text = $"{asset.Path}\n\nFormat: {asset.Format}\nCompatibility: {CompatibilityLabel(asset.Compatibility)}\nMagic: {asset.Magic}\nVersion: {asset.Version?.ToString() ?? "unknown"}\nBytes: {asset.Size:N0}\nSHA-256: {asset.Sha256}\nPreview source: {source}\n\nFINDINGS\n{Lines(asset.Findings)}\n\nCHUNKS\n{Lines(asset.Chunks.Select(chunk => $"{chunk.Id} · offset {chunk.Offset:N0} · {chunk.Size:N0} bytes"))}\n\nDEPENDENCIES\n{Lines(asset.Dependencies.Select(dependency => $"{dependency.Kind} · {(dependency.Exists ? "found" : "missing")} · {dependency.Path}"))}";
        if (asset.Format == AssetFormat.Wmo && asset.Version == 17)
        {
            _preview.IsVisible = false; _wmoPreview.IsVisible = true; _previewStatus.Text = "Loading bounded WMO root/group geometry and available materials…";
            try
            {
                var groupFiles = WorkspaceGroupSnapshots(asset);
                var loaded = await Task.Run(() => { var geometry = WmoPreviewGeometryService.Load(source, groupFiles, previewToken); var textures = WmoPreviewGeometryService.LoadTextures(geometry, cancellationToken: previewToken); return (geometry, textures); }, previewToken);
                if (generation != _previewGeneration) return;
                _wmoPreview.SetGeometry(loaded.geometry); _wmoPreview.SetDecodedTextures(loaded.textures.Textures);
                _previewStatus.Text = $"{loaded.geometry.Groups.Count:N0} groups · {loaded.geometry.Vertices.Count:N0} vertices · {loaded.geometry.TriangleIndices.Count / 3:N0} triangles · {loaded.textures.Textures.Count:N0}/{loaded.geometry.Materials.Count:N0} textures" + (loaded.textures.Findings.Count == 0 ? string.Empty : $" · {loaded.textures.Findings.Count:N0} texture finding(s)");
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (generation == _previewGeneration) _previewStatus.Text = $"WMO preview unavailable: {exception.Message}"; }
            return;
        }
        if (asset.Format != AssetFormat.M2 || asset.Compatibility != AssetCompatibility.AlreadyWotlk335) { _previewStatus.Text = "This asset must be converted to the verified Wrath layout before preview can load it."; return; }
        _previewStatus.Text = "Loading WotLK M2 + SKIN geometry…";
        try
        {
            var geometry = await Task.Run(() => M2PreviewGeometryService.Load(source));
            if (generation != _previewGeneration) return;
            _preview.SetGeometry(geometry); _previewStatus.Text = $"{geometry.Vertices.Count:N0} vertices · {geometry.TriangleIndices.Count / 3:N0} triangles · drag to rotate · wheel to zoom";
        }
        catch (Exception exception) { if (generation == _previewGeneration) _previewStatus.Text = $"Preview unavailable: {exception.Message}"; }
    }

    private void RemoveSelected()
    {
        if (_assets.SelectedItem is not AssetInspection selected) return;
        _inspections.Remove(selected); _workspace = null; RefreshList(); _summary.Text = SummaryText();
    }

    private void Clear()
    {
        _operation?.Cancel(); _previewOperation?.Cancel(); _workspace = null; _inspections.Clear(); _assets.ItemsSource = null; _details.Text = string.Empty; _preview.ClearGeometry(); _wmoPreview.ClearGeometry(); _summary.Text = "Asset list cleared."; _previewStatus.Text = "Select a compatible Wrath M2 or version-17 WMO for a live geometry preview.";
    }

    private void RefreshList() { _assets.ItemsSource = null; _assets.ItemsSource = _inspections.ToArray(); }
    private string SummaryText() => $"{_inspections.Count:N0} assets · {_inspections.Count(item => item.Compatibility == AssetCompatibility.AlreadyWotlk335):N0} Wrath-ready · {_inspections.Count(item => item.Compatibility == AssetCompatibility.RequiresNativeConversion):N0} require conversion · {_inspections.Count(item => item.Compatibility is AssetCompatibility.Invalid or AssetCompatibility.Unsupported):N0} blocked";
    private CancellationTokenSource BeginOperation(string text) { _operation?.Cancel(); var operation = new CancellationTokenSource(); _operation = operation; _summary.Text = text; return operation; }
    private void EndOperation(CancellationTokenSource operation) { if (ReferenceEquals(_operation, operation)) _operation = null; operation.Dispose(); }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The conversion workspace is not attached to the main window.");
    private static IEnumerable<string> Expand(IEnumerable<string> inputs)
    {
        foreach (var path in inputs)
        {
            if (File.Exists(path)) { if (IsAsset(path)) yield return path; continue; }
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", RecursiveFiles).Where(IsAsset)) yield return Path.GetFullPath(file);
        }
    }
    private static bool IsAsset(string path) => Path.GetExtension(path).Equals(".m2", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".wmo", StringComparison.OrdinalIgnoreCase);
    private static string Lines(IEnumerable<string> values) { var array = values.ToArray(); return array.Length == 0 ? "—" : string.Join("\n", array.Select(value => $"• {value}")); }
    private IReadOnlyDictionary<int, string>? WorkspaceGroupSnapshots(AssetInspection asset)
    {
        if (File.Exists(asset.Path) || _workspace is null) return null;
        var result = new Dictionary<int, string>();
        foreach (var dependency in asset.Dependencies.Where(value => value.Exists && value.Kind.Equals("WMO group", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(dependency.Path); var marker = name.LastIndexOf('_');
            if (marker < 0 || !int.TryParse(name[(marker + 1)..], out var index)) continue;
            result[index] = NativeAssetConversionService.ResolveDependencySnapshotPath(_workspace, asset, dependency);
        }
        return result;
    }
    private static string CompatibilityLabel(AssetCompatibility value) => value switch { AssetCompatibility.AlreadyWotlk335 => "Wrath 3.3.5 ready", AssetCompatibility.RequiresNativeConversion => "native conversion required", AssetCompatibility.Unsupported => "unsupported layout", _ => "invalid asset" };
    private static string CompatibilityColor(AssetCompatibility value) => value switch { AssetCompatibility.AlreadyWotlk335 => "#79C793", AssetCompatibility.RequiresNativeConversion => "#E5B768", _ => "#E27B7B" };
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }

    public void Dispose()
    {
        _operation?.Cancel(); _operation?.Dispose(); _previewOperation?.Cancel(); _previewOperation?.Dispose(); _preview.Dispose(); _wmoPreview.Dispose();
    }
}
