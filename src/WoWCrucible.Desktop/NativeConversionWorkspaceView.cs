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
    private readonly DesktopSettings _settings;
    private readonly TextBlock _summary = Status("Drop M2/WMO files or folders here, or add them with the buttons above.");
    private readonly TextBlock _previewStatus = Status("Select a compatible Wrath M2 or version-17 WMO for a live geometry preview.");
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _listfile = new() { PlaceholderText = "Optional FileDataID listfile (.csv/.txt) for modern external texture paths" };
    private readonly CheckBox _publishReadyOnly = new() { Content = "Path payload: publish eligible models while retaining blockers in receipt" };
    private readonly M2PreviewView _preview = new();
    private readonly WmoPreviewView _wmoPreview = new() { IsVisible = false };
    private readonly Grid _previewHost = new();
    private readonly List<AssetInspection> _inspections = [];
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _previewOperation;
    private NativeConversionWorkspace? _workspace;
    private int _previewGeneration;

    public event EventHandler? BackRequested;

    public NativeConversionWorkspaceView(DesktopSettings settings)
    {
        _settings = settings;
        if (File.Exists(settings.ModernFileDataIdListfilePath)) _listfile.Text = settings.ModernFileDataIdListfilePath;
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
        var convert = Accent("Convert selected verified static M2"); convert.Click += async (_, _) => await ConvertSelectedAsync();
        var convertEligible = new Button { Content = "Convert all eligible snapshots" }; convertEligible.Click += async (_, _) => await ConvertEligibleAsync();
        var pathPayload = Accent("Build path-preserving payload"); pathPayload.Click += async (_, _) => await BuildPathPayloadAsync();
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _operation?.Cancel();
        var browseListfile = new Button { Content = "Choose listfile" }; browseListfile.Click += async (_, _) => await ChooseListfileAsync();
        var autoListfile = new Button { Content = "Auto-detect" }; autoListfile.Click += async (_, _) => await AutoDetectListfileAsync();
        var clearListfile = new Button { Content = "Clear listfile" }; clearListfile.Click += (_, _) => RememberListfile(null);

        var header = new Border
        {
            BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8),
            Child = new WrapPanel { Children = { back, new TextBlock { Text = "MODERN ASSET CONVERSION", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, addFiles, addFolder, remove, clear, create, open, convert, convertEligible, pathPayload, _publishReadyOnly, cancel } }
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

        var listfileStrip = new Grid
        {
            ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 7, Margin = new Thickness(12, 7, 12, 0),
            Children =
            {
                new TextBlock { Text = "External texture IDs", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#9AA5B7") },
                WithColumn(_listfile, 1), WithColumn(browseListfile, 2), WithColumn(autoListfile, 3), WithColumn(clearListfile, 4)
            }
        };

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
        var body = new ResponsiveSplitGrid(left, right, 1, 2) { Margin = new Thickness(12, 9, 12, 12) };
        Content = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), Children = { header, WithRow(listfileStrip, 1), WithRow(dropTarget, 2), WithRow(body, 3) } };
    }

    private async Task ChooseListfileAsync()
    {
        var file = (await Storage().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a FileDataID-to-client-path listfile", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("FileDataID listfiles") { Patterns = ["*.csv", "*.txt"] }]
        })).FirstOrDefault()?.TryGetLocalPath();
        if (file is not null) RememberListfile(file);
    }

    private async Task AutoDetectListfileAsync()
    {
        var models = _inspections.Where(asset => asset.Format == AssetFormat.M2 && asset.Magic == "MD21")
            .Select(asset => _workspace is null ? asset.Path : NativeAssetConversionService.ResolveSnapshotPath(_workspace, asset)).ToArray();
        if (models.Length == 0) { _summary.Text = "Add or open modern M2 assets first; auto-detection verifies candidates against their exact FileDataIDs."; return; }
        var operation = BeginOperation($"Checking nearby listfiles against {models.Length:N0} modern M2 model(s)…"); var token = operation.Token;
        try
        {
            var discoveryContexts = models.Concat(_inspections.Select(asset => asset.Path)).ToArray();
            var discovery = await Task.Run(() => FileDataIdListfileDiscoveryService.ResolveBest(StaticM2DownportService.RequiredExternalTextureIds(models, token), discoveryContexts, token), token);
            if (discovery.Selected is null) throw new InvalidOperationException(string.Join(" ", discovery.Findings));
            RememberListfile(discovery.Selected.SourcePath);
            _details.Text = $"FILEDATAID LISTFILE AUTO-DETECTION\n\nSelected: {discovery.Selected.SourcePath}\nSHA-256: {discovery.Selected.SourceSha256}\nRequested IDs: {discovery.Selected.RequestedIds.Count:N0}\nResolved IDs: {discovery.Selected.Resolved.Count:N0}\nCandidates checked: {discovery.Candidates.Count:N0}\n\n{Lines(discovery.Findings)}";
            _summary.Text = $"Verified and remembered a complete FileDataID listfile · {discovery.Selected.SourcePath}";
        }
        catch (OperationCanceledException) { _summary.Text = "Listfile auto-detection cancelled."; }
        catch (Exception exception) { _summary.Text = $"Listfile auto-detection needs review: {exception.Message}"; DesktopCrashLogger.Log("FileDataID listfile auto-detection failed", exception); }
        finally { EndOperation(operation); }
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

    private async Task ConvertSelectedAsync()
    {
        if (_workspace is null) { _summary.Text = "Create or open a verified workspace before conversion; loose originals are never mutated."; return; }
        if (_assets.SelectedItem is not AssetInspection asset || asset.Format != AssetFormat.M2 || asset.Magic != "MD21") { _summary.Text = "Select a modern MD21 M2 snapshot first."; return; }
        var workspace = _workspace; var source = NativeAssetConversionService.ResolveSnapshotPath(workspace, asset); var skin = WorkspaceSkinSnapshot(asset, workspace); var listfile = SelectedListfile();
        var output = Path.Combine(workspace.RootPath, "converted", asset.Sha256[..12].ToUpperInvariant());
        var operation = BeginOperation("Planning and validating the selected immutable M2/SKIN snapshot…"); var token = operation.Token;
        try
        {
            var completed = await Task.Run(() =>
            {
                FileDataIdListfileDiscoveryResult? discovery = null; FileDataIdListfileSnapshot? snapshot = null;
                if (listfile is not null) snapshot = StaticM2DownportService.PrepareListfile(listfile, [source], token);
                else
                {
                    var required = StaticM2DownportService.RequiredExternalTextureIds([source], token);
                    discovery = FileDataIdListfileDiscoveryService.ResolveBest(required, [source, asset.Path, workspace.RootPath], token);
                    if (required.Count > 0 && discovery.Selected is null) throw new InvalidOperationException(string.Join(" ", discovery.Findings));
                    snapshot = discovery.Selected;
                }
                var plan = snapshot is null ? StaticM2DownportService.Plan(source, skin, cancellationToken: token) : StaticM2DownportService.PlanWithListfileSnapshot(source, skin, snapshot, token);
                if (!plan.Ready) throw new InvalidOperationException("Static profile blocked this model:\n- " + string.Join("\n- ", plan.Blockers));
                return (Result: StaticM2DownportService.Convert(plan, output, token), Discovery: discovery);
            }, token);
            var result = completed.Result; if (completed.Discovery?.Selected is not null) RememberListfile(completed.Discovery.Selected.SourcePath);
            var geometry = await Task.Run(() => M2PreviewGeometryService.Load(result.OutputModelPath, result.OutputSkinPath, M2PreviewVisibilityMode.AllGeosets), token);
            _preview.IsVisible = true; _wmoPreview.IsVisible = false; _preview.SetGeometry(geometry);
            _previewStatus.Text = $"Converted and independently reloaded · {geometry.Vertices.Count:N0} vertices · {geometry.TotalTriangleIndices / 3:N0} triangles";
            _details.Text = $"CONVERSION COMPLETE\n\nModel: {result.OutputModelPath}\nModel SHA-256: {result.OutputModelSha256}\nSKIN: {result.OutputSkinPath}\nSKIN SHA-256: {result.OutputSkinSha256}\nListfile: {result.Plan.SourceListfilePath ?? "not required"}\nReceipt: {result.ReceiptPath}\n\nRESOLVED TEXTURE PATHS\n{Lines(result.Plan.ResolvedTexturePaths.Select(value => $"slot {value.TextureIndex} · FileDataID {value.FileDataId} · {value.ClientPath}"))}\n\nTRANSFORMATIONS\n{Lines(result.Plan.Transformations)}\n\nDECLARED LOSSES\n{Lines(result.Plan.Losses)}";
            _summary.Text = $"Verified static M2 conversion published atomically · {result.OutputDirectory}";
            DesktopCrashLogger.Debug("CONVERT", "static-m2-downport-complete", ("source", asset.Path), ("output", result.OutputDirectory), ("vertices", result.ValidatedVertices), ("triangles", result.ValidatedTriangles));
        }
        catch (OperationCanceledException) { _summary.Text = "Static M2 conversion cancelled before publication."; }
        catch (Exception exception) { _summary.Text = $"Static M2 conversion failed: {exception.Message}"; DesktopCrashLogger.Log("Static M2 downport failed", exception); }
        finally { EndOperation(operation); }
    }

    private async Task ConvertEligibleAsync()
    {
        if (_workspace is null) { _summary.Text = "Create or open a verified workspace before batch conversion."; return; }
        var workspace = _workspace; var candidates = workspace.Assets.Where(asset => asset.Format == AssetFormat.M2 && asset.Magic == "MD21").ToArray(); var listfilePath = SelectedListfile();
        if (candidates.Length == 0) { _summary.Text = "This workspace has no modern MD21 M2 snapshots."; return; }
        var operation = BeginOperation($"Planning {candidates.Length:N0} immutable M2 snapshot(s)…"); var token = operation.Token;
        try
        {
            var batch = await Task.Run(() =>
            {
                var converted = new List<string>(); var skipped = new List<string>(); var blocked = new List<string>(); var failed = new List<string>(); FileDataIdListfileDiscoveryResult? discovery = null;
                var sources = candidates.ToDictionary(asset => asset, asset => NativeAssetConversionService.ResolveSnapshotPath(workspace, asset));
                FileDataIdListfileSnapshot? listfile;
                if (listfilePath is not null) listfile = StaticM2DownportService.PrepareListfile(listfilePath, sources.Values, token);
                else
                {
                    var required = StaticM2DownportService.RequiredExternalTextureIds(sources.Values, token);
                    discovery = FileDataIdListfileDiscoveryService.ResolveBest(required, sources.Values.Concat(candidates.Select(asset => asset.Path)).Append(workspace.RootPath), token);
                    if (required.Count > 0 && discovery.Selected is null) throw new InvalidOperationException(string.Join(" ", discovery.Findings));
                    listfile = discovery.Selected;
                }
                foreach (var asset in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var source = sources[asset]; var plan = listfile is null ? StaticM2DownportService.Plan(source, WorkspaceSkinSnapshot(asset, workspace), cancellationToken: token) : StaticM2DownportService.PlanWithListfileSnapshot(source, WorkspaceSkinSnapshot(asset, workspace), listfile, token);
                        if (!plan.Ready) { blocked.Add($"{asset.Path} :: {string.Join(" | ", plan.Blockers)}"); continue; }
                        var output = Path.Combine(workspace.RootPath, "converted", asset.Sha256[..12].ToUpperInvariant());
                        if (File.Exists(Path.Combine(output, "conversion-receipt.json"))) { skipped.Add($"{asset.Path} :: existing receipt {output}"); continue; }
                        var result = StaticM2DownportService.Convert(plan, output, token); converted.Add($"{asset.Path} -> {result.OutputDirectory}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { failed.Add($"{asset.Path} :: {exception.Message}"); }
                }
                return (Converted: converted, Skipped: skipped, Blocked: blocked, Failed: failed, Discovery: discovery);
            }, token);
            if (batch.Discovery?.Selected is not null) RememberListfile(batch.Discovery.Selected.SourcePath);
            _details.Text = $"BATCH STATIC M2 CONVERSION\n\nCONVERTED ({batch.Converted.Count:N0})\n{Lines(batch.Converted)}\n\nALREADY PRESENT ({batch.Skipped.Count:N0})\n{Lines(batch.Skipped)}\n\nBLOCKED WITHOUT WRITING ({batch.Blocked.Count:N0})\n{Lines(batch.Blocked)}\n\nFAILED ({batch.Failed.Count:N0})\n{Lines(batch.Failed)}";
            _summary.Text = $"Batch complete · {batch.Converted.Count:N0} converted · {batch.Skipped.Count:N0} already present · {batch.Blocked.Count:N0} blocked · {batch.Failed.Count:N0} failed";
            DesktopCrashLogger.Debug("CONVERT", "static-m2-batch-complete", ("converted", batch.Converted.Count), ("skipped", batch.Skipped.Count), ("blocked", batch.Blocked.Count), ("failed", batch.Failed.Count));
        }
        catch (OperationCanceledException) { _summary.Text = "Batch conversion cancelled. Already published per-model outputs remain valid; no partial model folder was published."; }
        catch (Exception exception) { _summary.Text = $"Batch conversion failed: {exception.Message}"; DesktopCrashLogger.Log("Static M2 batch conversion failed", exception); }
        finally { EndOperation(operation); }
    }

    private async Task BuildPathPayloadAsync()
    {
        var source = (await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the source root whose relative paths must be preserved", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        if (source is null) return;
        var output = (await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a new or empty batch output folder", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        if (output is null) return;
        var listfile = SelectedListfile(); var readyOnly = _publishReadyOnly.IsChecked == true;
        var operation = BeginOperation("Planning the complete path-preserving source tree…"); var token = operation.Token;
        try
        {
            var completed = await Task.Run(() =>
            {
                var automatic = listfile is null ? StaticM2BatchDownportService.PlanAuto(source, cancellationToken: token) : null;
                var plan = automatic?.Plan ?? StaticM2BatchDownportService.Plan(source, listfile, token);
                if (!readyOnly && (plan.Blocked > 0 || plan.Failed > 0))
                    throw new InvalidOperationException($"The tree has {plan.Ready:N0} eligible, {plan.Blocked:N0} blocked, and {plan.Failed:N0} failed model(s). Enable the explicit eligible-only option to publish the verified subset while retaining every blocker in the receipt.");
                return (Result: StaticM2BatchDownportService.Convert(plan, output, readyOnly, cancellationToken: token), Discovery: automatic?.Discovery);
            }, token);
            var result = completed.Result; if (completed.Discovery?.Selected is not null) RememberListfile(completed.Discovery.Selected.SourcePath);
            _details.Text = $"PATH-PRESERVING BATCH COMPLETE\n\nPayload: {result.PayloadDirectory}\nReceipt: {result.ReceiptPath}\nWorkers: {result.Workers:N0}\nConverted: {result.Outputs.Count:N0}\nBlocked retained in receipt: {result.Plan.Blocked:N0}\nFailed retained in receipt: {result.Plan.Failed:N0}\n\nOUTPUTS\n{Lines(result.Outputs.Select(value => $"{value.ModelRelativePath} · {value.Vertices:N0} vertices · {value.Triangles:N0} triangles"))}";
            _summary.Text = $"MPQ-ready relative tree published atomically · {result.Outputs.Count:N0} model(s) · {result.PayloadDirectory}";
            DesktopCrashLogger.Debug("CONVERT", "path-batch-complete", ("source", source), ("payload", result.PayloadDirectory), ("converted", result.Outputs.Count), ("blocked", result.Plan.Blocked));
        }
        catch (OperationCanceledException) { _summary.Text = "Path-preserving batch conversion cancelled before publication."; }
        catch (Exception exception) { _summary.Text = $"Path-preserving batch blocked: {exception.Message}"; DesktopCrashLogger.Log("Path-preserving M2 batch failed", exception); }
        finally { EndOperation(operation); }
    }

    private async Task ShowSelectedAsync()
    {
        _previewOperation?.Cancel(); _previewOperation?.Dispose(); _previewOperation = new CancellationTokenSource(); var previewToken = _previewOperation.Token;
        var generation = ++_previewGeneration; _preview.ClearGeometry(); _wmoPreview.ClearGeometry(); _preview.IsVisible = true; _wmoPreview.IsVisible = false;
        if (_assets.SelectedItem is not AssetInspection asset) { _details.Text = string.Empty; _previewStatus.Text = "Select an asset."; return; }
        var source = _workspace is not null ? NativeAssetConversionService.ResolveSnapshotPath(_workspace, asset) : asset.Path;
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
        if (asset.Format == AssetFormat.M2 && asset.Compatibility == AssetCompatibility.RequiresNativeConversion)
        {
            _previewStatus.Text = "Checking the loss-accounted static M2 conversion profile…";
            try
            {
                var listfile = SelectedListfile(); var plan = await Task.Run(() => StaticM2DownportService.Plan(source, WorkspaceSkinSnapshot(asset), listfile, previewToken), previewToken);
                if (generation != _previewGeneration) return;
                _details.Text += $"\n\nVERIFIED M2 DOWNPORT PLAN\nReady: {plan.Ready}\nOutput flags: 0x{plan.OutputFlags:X}\nListfile: {plan.SourceListfilePath ?? "not required/supplied"}\nGeometry: {plan.VertexCount:N0} vertices · {plan.TriangleCount:N0} triangles · {plan.SubmeshCount:N0} submeshes · {plan.MaterialCount:N0} materials\nAnimation/effects: {plan.AnimationSequenceCount:N0} embedded sequences · {plan.GlobalSequenceCount:N0} global clocks · {plan.ConstantColorTrackCount:N0} constant color tracks · {plan.ParticleEmitterCount:N0} particle emitters\n\nRESOLVED TEXTURE PATHS\n{Lines(plan.ResolvedTexturePaths.Select(value => $"slot {value.TextureIndex} · FileDataID {value.FileDataId} · {value.ClientPath}"))}\n\nTRANSFORMATIONS\n{Lines(plan.Transformations)}\n\nDECLARED LOSSES\n{Lines(plan.Losses)}\n\nBLOCKERS\n{Lines(plan.Blockers)}";
                _previewStatus.Text = plan.Ready ? "Eligible for verified static M2 downport. Create/open a workspace, then use Convert selected." : $"Static profile blocked this model with {plan.Blockers.Count:N0} explicit finding(s).";
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (generation == _previewGeneration) _previewStatus.Text = $"Conversion planning unavailable: {exception.Message}"; }
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
    private string? WorkspaceSkinSnapshot(AssetInspection asset, NativeConversionWorkspace? workspace = null)
    {
        workspace ??= _workspace; if (workspace is null) return null;
        var expected = Path.GetFileNameWithoutExtension(asset.Path) + "00.skin";
        var skins = asset.Dependencies.Where(value => value.Exists && value.Kind.Equals("skin", StringComparison.OrdinalIgnoreCase)).ToArray();
        var dependency = skins.FirstOrDefault(value => Path.GetFileName(value.Path).Equals(expected, StringComparison.OrdinalIgnoreCase)) ?? (skins.Length == 1 ? skins[0] : null);
        return dependency is null ? null : NativeAssetConversionService.ResolveDependencySnapshotPath(workspace, asset, dependency);
    }
    private string? SelectedListfile() => string.IsNullOrWhiteSpace(_listfile.Text) ? null : Path.GetFullPath(_listfile.Text.Trim());
    private void RememberListfile(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        _listfile.Text = normalized; _settings.ModernFileDataIdListfilePath = normalized; _settings.Save();
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
