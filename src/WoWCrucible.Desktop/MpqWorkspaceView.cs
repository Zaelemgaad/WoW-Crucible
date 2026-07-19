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
    private PatchTargetClientRequirement? _targetClientRequirement;
    private readonly TextBox _dependencyLibrary = new() { PlaceholderText = "Processed asset library root" };
    private readonly TextBox _dependencyRoot = new() { PlaceholderText = "Root M2, WMO, ADT, or WDT inside the processed library" };
    private readonly TextBox _dependencyTargetIndex = new() { PlaceholderText = "Optional complete target-client index (omit byte-identical base assets)" };
    private readonly ListBox _dependencyNodes = new();
    private readonly ComboBox _dependencyCandidates = new() { PlaceholderText = "Select an explicit provenance candidate" };
    private readonly TextBlock _dependencyStatus = StatusText("Choose one client asset to calculate its recursive patch closure.");
    private ClientAssetDependencyGraph? _dependencyGraph;
    private readonly Dictionary<string, string> _dependencyOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dependencyTargetOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string _dependencyOverrideScope = string.Empty;
    private readonly object _dependencyTargetCacheLock = new();
    private ClientEffectiveAssetCatalog? _dependencyTargetCatalog;
    private string _dependencyTargetCatalogPath = string.Empty;
    private long _dependencyTargetCatalogLength;
    private long _dependencyTargetCatalogWriteTicks;

    private readonly TextBox _archivePath = new() { PlaceholderText = "MPQ archive" };
    private readonly TextBox _externalListfile = new() { PlaceholderText = "Optional external listfile" };
    private readonly TextBox _browserSearch = new() { PlaceholderText = "Global flat-path search (* and ? supported)…" };
    private readonly ComboBox _extractionWorkers = new() { ItemsSource = new[] { $"Auto (up to {PatchArchiveService.RecommendedExtractionWorkers})", "1 worker", "2 workers", "4 workers", "8 workers", "16 workers" }, SelectedIndex = 0 };
    private readonly ListBox _browserItems = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _treeItems = new() { SelectionMode = SelectionMode.Multiple };
    private readonly WrapPanel _treeBreadcrumb = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _browserStatus = StatusText("Open an MPQ to browse it.");
    private IReadOnlyList<MpqFileEntry> _allArchiveEntries = [];
    private string _treeFolder = string.Empty;
    private readonly TextBox _cascStoragePath = new() { PlaceholderText = "CASC client/storage folder" };
    private readonly TextBox _cascListfile = new() { PlaceholderText = "Optional external CASC listfile" };
    private readonly TextBox _cascSearch = new() { PlaceholderText = "Global CASC path search (* and ? supported)…" };
    private readonly ListBox _cascItems = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _cascTreeItems = new() { SelectionMode = SelectionMode.Multiple };
    private readonly WrapPanel _cascBreadcrumb = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _cascStatus = StatusText("Choose a later-client CASC storage. Crucible opens it read-only.");
    private IReadOnlyList<CascFileEntry> _allCascEntries = [];
    private IReadOnlyList<MpqFileEntry> _cascTreeEntries = [];
    private string _cascTreeFolder = string.Empty;
    private readonly ListBox _mergeInputs = new() { SelectionMode = SelectionMode.Multiple };
    private readonly List<string> _mergePaths = [];
    private readonly ComboBox _mergePolicy = new() { ItemsSource = Enum.GetValues<MpqMergeConflictPolicy>(), SelectedItem = MpqMergeConflictPolicy.BlockDifferentEntries };
    private readonly TextBlock _mergeStatus = StatusText("Merge deliberately small patches without modifying any source archive.");
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;

    public MpqWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _dependencyLibrary.Text = session.Settings.ProcessedAssetLibraryPath; _dependencyTargetIndex.Text = session.Settings.ClientIndexPath;
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
        _treeItems.ItemTemplate = new FuncDataTemplate<MpqBrowserNode>((node, _) => node is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = node.IsFolder ? "▸" : node.Kind == "anonymous" ? "?" : "·", Foreground = Brush.Parse(node.IsFolder ? "#D2A95F" : node.Kind == "anonymous" ? "#D47B67" : "#8290A6") },
                WithColumn(new TextBlock { Text = node.Name, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                WithColumn(new TextBlock { Text = node.IsFolder ? $"{node.FileCount:N0} files" : node.Entry?.Locale is > 0 ? $"{node.Kind} · locale 0x{node.Entry.Locale:X}" : node.Kind }, 2),
                WithColumn(new TextBlock { Text = FormatBytes(node.Size) }, 3),
                WithColumn(new TextBlock { Text = node.IsFolder || node.Size == 0 ? FormatBytes(node.CompressedSize) : $"{100d * node.CompressedSize / node.Size:0.#}%" }, 4)
            }
        });
        _treeItems.DoubleTapped += (_, _) => OpenSelectedTreeFolder();
        _cascItems.ItemTemplate = new FuncDataTemplate<CascFileEntry>((entry, _) => entry is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = entry.ArchivePath, TextTrimming = TextTrimming.CharacterEllipsis },
                WithColumn(new TextBlock { Text = FormatBytes(entry.Size) }, 1),
                WithColumn(new TextBlock { Text = entry.FileDataId == uint.MaxValue ? "—" : entry.FileDataId.ToString() }, 2),
                WithColumn(new TextBlock { Text = entry.IsAvailableLocally ? entry.NameType.ToString() : $"remote · {entry.NameType}" }, 3)
            }
        });
        _cascTreeItems.ItemTemplate = _treeItems.ItemTemplate;
        _cascTreeItems.DoubleTapped += (_, _) => OpenSelectedCascTreeFolder();
        _cascSearch.TextChanged += (_, _) => FilterCasc();
        _dependencyNodes.ItemTemplate = new FuncDataTemplate<ClientAssetDependencyNode>((node, _) => node is null ? new Grid() : BuildDependencyRow(node));
        _dependencyNodes.SelectionChanged += (_, _) => _dependencyCandidates.ItemsSource = (_dependencyNodes.SelectedItem as ClientAssetDependencyNode)?.Candidates;

        var back = new Button { Content = "← Editor", HorizontalAlignment = HorizontalAlignment.Left }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12,8), Children = { back, WithColumn(new TextBlock { Text = "MPQ & CASC ARCHIVES", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0) }, 1) } };
        _tabs.Items.Add(new TabItem { Header = "Patch builder", Content = BuildBuilderPage() });
        _tabs.Items.Add(new TabItem { Header = "Dependency closure", Content = BuildDependencyPage() });
        _tabs.Items.Add(new TabItem { Header = "Archive browser", Content = BuildBrowserPage() });
        _tabs.Items.Add(new TabItem { Header = "CASC browser", Content = BuildCascBrowserPage() });
        _tabs.Items.Add(new TabItem { Header = "Merge small patches", Content = BuildMergePage() });
        _tabs.Items.Add(new TabItem { Header = "Client deployment", Content = BuildDeploymentPage() });
        Content = new Grid { RowDefinitions = new("Auto,*"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,0,0,1), Child = heading }, WithRow(_tabs, 1) } };
    }

    public async Task OpenArchiveAsync(string path)
    {
        _archivePath.Text = Path.GetFullPath(path);
        _tabs.SelectedIndex = 2;
        await LoadArchiveAsync();
    }

    public void StagePaths(IEnumerable<string> paths)
    {
        _tabs.SelectedIndex = 0;
        AddPaths(paths);
    }

    public void StageEntries(IEnumerable<PatchEntry> entries)
    {
        _tabs.SelectedIndex = 0; _targetClientRequirement = null;
        foreach (var entry in entries)
        {
            var source = Path.GetFullPath(entry.SourcePath); var archive = PatchInputMapper.NormalizeArchivePath(entry.ArchivePath); var existing = _entries.FindIndex(row => row.ArchivePath.Equals(archive, StringComparison.OrdinalIgnoreCase)); var row = new PatchRow(source, archive);
            if (existing >= 0) _entries[existing] = row; else _entries.Add(row);
        }
        _expectedCount.Value = _entries.Count;
        if (_entries.Any(row => row.ArchivePath.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase)))
        {
            var required = (_required.Text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!required.Contains("DBFilesClient\\*.dbc", StringComparer.OrdinalIgnoreCase)) required.Add("DBFilesClient\\*.dbc"); _required.Text = string.Join(Environment.NewLine, required);
        }
        RefreshBuilder();
    }

    private Control BuildBuilderPage()
    {
        var addFiles = new Button { Content = "Add files" }; addFiles.Click += async (_, _) => await AddFilesAsync();
        var addFolder = new Button { Content = "Add folder tree" }; addFolder.Click += async (_, _) => await AddFolderAsync();
        var openExisting = new Button { Content = "Update existing small MPQ" }; openExisting.Click += async (_, _) => await ChooseExistingPatchAsync();
        var remove = new Button { Content = "Remove selected" }; remove.Click += (_, _) => RemoveSelected();
        var clear = new Button { Content = "Clear" }; clear.Click += (_, _) => { _entries.Clear(); _existingPatch = null; _targetClientRequirement = null; RefreshBuilder(); };
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

    private Control BuildDependencyPage()
    {
        var browseLibrary = new Button { Content = "Library…" }; browseLibrary.Click += async (_, _) => { var path = await PickFolderAsync("Select the processed asset library"); if (path is not null) { _dependencyLibrary.Text = path; ClearDependencyChoices(); } };
        var browseRoot = new Button { Content = "Root asset…" }; browseRoot.Click += async (_, _) => { var path = await PickFileAsync("Select a dependency root", ["*.m2", "*.wmo", "*.adt", "*.wdt"]); if (path is not null) { _dependencyRoot.Text = path; ClearDependencyChoices(); } };
        var browseTarget = new Button { Content = "Target index…" }; browseTarget.Click += async (_, _) => { var path = await PickFolderAsync("Select a complete target-client index"); if (path is not null) { _dependencyTargetIndex.Text = path; ClearDependencyChoices(); } };
        var analyze = AccentButton("Analyze recursive closure"); analyze.Click += async (_, _) => await AnalyzeDependenciesAsync();
        var stage = AccentButton("Stage complete closure in patch builder"); stage.Click += (_, _) => StageDependencyClosure();
        var useCandidate = new Button { Content = "Use selected candidate explicitly" }; useCandidate.Click += async (_, _) => await UseDependencyCandidateAsync();
        var clearChoices = new Button { Content = "Clear explicit choices" }; clearChoices.Click += async (_, _) => { ClearDependencyChoices(); await AnalyzeDependenciesAsync(); };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _operation?.Cancel();
        var paths = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6, Children = { _dependencyLibrary, WithColumn(browseLibrary, 1), WithRow(_dependencyRoot, 1), WithRow(WithColumn(browseRoot, 1), 1), WithRow(_dependencyTargetIndex, 2), WithRow(WithColumn(browseTarget, 1), 2) } };
        var explanation = new TextBlock { Text = "Crucible follows WotLK M2 → exact view SKINs/embedded textures, WMO → groups/textures/doodad models, and ADT/WDT → terrain textures/models/WMOs recursively. Every local file must come from the root provenance unless selected explicitly. A complete optional target index probes even anonymous MPQ entries by exact path: byte-identical effective assets are inherited and omitted, different bytes remain intentional overrides, and uncertain target order is never used to satisfy an unstaged dependency.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
        var toolbar = new WrapPanel { Children = { analyze, stage, _dependencyCandidates, useCandidate, clearChoices, cancel } };
        var dropTarget = new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = new Grid { RowDefinitions = new("Auto,*"), Children = { new TextBlock { Text = "Drop one M2, WMO, ADT, or WDT root here", Foreground = Brush.Parse("#8995A9") }, WithRow(_dependencyNodes, 1) } } };
        DragDrop.SetAllowDrop(dropTarget, true); DragDrop.AddDragOverHandler(dropTarget, (_, args) => { args.DragEffects = args.DataTransfer.TryGetFiles()?.Any(file => file.TryGetLocalPath() is { } path && IsDependencyRoot(path)) == true ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; });
        DragDrop.AddDropHandler(dropTarget, async (_, args) => { var path = args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).FirstOrDefault(path => path is not null && IsDependencyRoot(path)); if (path is not null) { _dependencyRoot.Text = path; ClearDependencyChoices(); await AnalyzeDependenciesAsync(); } args.Handled = true; });
        return new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(10), RowSpacing = 8, Children = { paths, WithRow(explanation, 1), WithRow(toolbar, 2), WithRow(dropTarget, 3), WithRow(_dependencyStatus, 4) } };
    }

    private async Task AnalyzeDependenciesAsync()
    {
        BeginOperation(); _dependencyGraph = null; _dependencyNodes.ItemsSource = null; _dependencyStatus.Text = "Indexing the processed library and walking dependency edges…";
        try
        {
            var library = Path.GetFullPath(_dependencyLibrary.Text?.Trim() ?? string.Empty); var root = Path.GetFullPath(_dependencyRoot.Text?.Trim() ?? string.Empty); var targetIndexPath = string.IsNullOrWhiteSpace(_dependencyTargetIndex.Text) ? null : Path.GetFullPath(_dependencyTargetIndex.Text.Trim()); var token = _operation!.Token; var scope = library + "|" + root + "|" + targetIndexPath;
            if (!_dependencyOverrideScope.Equals(scope, StringComparison.OrdinalIgnoreCase)) { _dependencyOverrides.Clear(); _dependencyTargetOverrides.Clear(); _dependencyOverrideScope = scope; }
            var overrides = _dependencyOverrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var targetOverrides = _dependencyTargetOverrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var graph = await Task.Run(() => { var index = AssetComparisonService.BuildIndex(library, token); var location = ClientAssetDependencyService.InferLocation(index, root); var target = LoadDependencyTargetCatalog(targetIndexPath, token); return ClientAssetDependencyService.Analyze(index, location, overrides, target, targetOverrides, token); }, token);
            _dependencyGraph = graph; _dependencyNodes.ItemsSource = graph.Nodes;
            _dependencyNodes.SelectedItem = graph.Blocking.FirstOrDefault();
            _dependencyStatus.Text = $"{graph.Nodes.Count:N0} graph node(s) · {graph.PatchEntries.Count:N0} patch file(s) · {graph.Inherited.Count:N0} inherited from target · {graph.ExternalBindings.Count:N0} runtime binding(s) · {graph.Blocking.Count:N0} BLOCKING · {_dependencyOverrides.Count:N0} provenance choice(s) · {_dependencyTargetOverrides.Count:N0} target-archive choice(s)\nRoot: {graph.Root.ClientPath} · provenance {graph.Root.Provenance}";
            _session.Settings.ProcessedAssetLibraryPath = library; if (targetIndexPath is not null) _session.Settings.ClientIndexPath = targetIndexPath; _session.Settings.Save(); DesktopCrashLogger.Debug("ASSET", "dependency-closure-success", ("root", graph.Root.ClientPath), ("nodes", graph.Nodes.Count), ("files", graph.PatchEntries.Count), ("inherited", graph.Inherited.Count), ("blocking", graph.Blocking.Count));
        }
        catch (OperationCanceledException) { _dependencyStatus.Text = "Dependency analysis cancelled."; }
        catch (Exception exception) { _dependencyStatus.Text = $"Dependency analysis failed: {exception.Message}"; DesktopCrashLogger.Log("Asset dependency analysis failed", exception); }
    }

    private void StageDependencyClosure()
    {
        if (_dependencyGraph is null) { _dependencyStatus.Text = "Analyze a root asset first."; return; }
        if (_dependencyGraph.Blocking.Count > 0) { _dependencyStatus.Text = $"Staging blocked by {_dependencyGraph.Blocking.Count:N0} missing, cross-source, or invalid dependency node(s). Resolve them explicitly first."; return; }
        foreach (var entry in _dependencyGraph.PatchEntries) { var index = _entries.FindIndex(row => row.ArchivePath.Equals(entry.ArchivePath, StringComparison.OrdinalIgnoreCase)); var row = new PatchRow(entry.SourcePath, entry.ArchivePath); if (index >= 0) _entries[index] = row; else _entries.Add(row); }
        if (_dependencyGraph.PatchEntries.Count == 0) { _dependencyStatus.Text = $"All {_dependencyGraph.Inherited.Count:N0} file dependency node(s) are already byte-identical in or safely supplied by the selected target client. No patch archive is needed for this closure."; return; }
        _targetClientRequirement = _dependencyGraph.TargetRequirement;
        RefreshBuilder(); _tabs.SelectedIndex = 0; _builderStatus.Text = $"Staged minimal dependency-complete closure: {_dependencyGraph.PatchEntries.Count:N0} file(s), {_dependencyGraph.Inherited.Count:N0} inherited target file(s) omitted, {_dependencyGraph.ExternalBindings.Count:N0} visible runtime binding(s). Review archive paths, save the manifest, then build the tiny MPQ.";
    }

    private async Task UseDependencyCandidateAsync()
    {
        if (_dependencyNodes.SelectedItem is not ClientAssetDependencyNode node || node.State is not (ClientAssetDependencyState.CrossSourceConflict or ClientAssetDependencyState.TargetAmbiguous)) { _dependencyStatus.Text = "Select a cross-source or target-order conflict row first."; return; }
        if (_dependencyCandidates.SelectedItem is not string candidate) { _dependencyStatus.Text = "Select one candidate explicitly; Crucible will not guess between layers."; return; }
        if (node.State == ClientAssetDependencyState.CrossSourceConflict) _dependencyOverrides[node.ClientPath] = candidate;
        else _dependencyTargetOverrides[node.ClientPath] = candidate;
        await AnalyzeDependenciesAsync();
    }

    private void ClearDependencyChoices() { _dependencyOverrides.Clear(); _dependencyTargetOverrides.Clear(); _dependencyOverrideScope = string.Empty; _dependencyCandidates.ItemsSource = null; }

    private ClientEffectiveAssetCatalog? LoadDependencyTargetCatalog(string? indexPath, CancellationToken cancellationToken)
    {
        if (indexPath is null) return null;
        var summaryPath = Path.Combine(indexPath, "client-index.json"); var info = new FileInfo(summaryPath);
        if (!info.Exists) return ClientEffectiveAssetCatalog.Load(indexPath, cancellationToken);
        lock (_dependencyTargetCacheLock)
        {
            if (_dependencyTargetCatalog is not null && _dependencyTargetCatalogPath.Equals(indexPath, StringComparison.OrdinalIgnoreCase) && _dependencyTargetCatalogLength == info.Length && _dependencyTargetCatalogWriteTicks == info.LastWriteTimeUtc.Ticks)
                return _dependencyTargetCatalog;
            _dependencyTargetCatalog = ClientEffectiveAssetCatalog.Load(indexPath, cancellationToken); _dependencyTargetCatalogPath = indexPath; _dependencyTargetCatalogLength = info.Length; _dependencyTargetCatalogWriteTicks = info.LastWriteTimeUtc.Ticks;
            return _dependencyTargetCatalog;
        }
    }

    private static Grid BuildDependencyRow(ClientAssetDependencyNode node)
    {
        var color = node.State switch { ClientAssetDependencyState.Root or ClientAssetDependencyState.Resolved => "#76B78B", ClientAssetDependencyState.TargetInherited => "#6EA8D9", ClientAssetDependencyState.TargetOverride => "#D7A15D", ClientAssetDependencyState.ExternalBinding => "#A9A7C8", ClientAssetDependencyState.TargetAmbiguous or ClientAssetDependencyState.Invalid => "#D96F6F", _ => "#E3A35D" };
        var primary = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 9, Children = { new TextBlock { Text = node.State.ToString(), Foreground = Brush.Parse(color), FontWeight = FontWeight.SemiBold }, WithColumn(new TextBlock { Text = node.Kind, Foreground = Brush.Parse("#93A0B3") }, 1), WithColumn(new TextBlock { Text = node.ClientPath, TextTrimming = TextTrimming.CharacterEllipsis }, 2) } };
        var candidates = node.Candidates.Count > 1 ? $" Candidates: {string.Join(" | ", node.Candidates)}" : string.Empty;
        return new Grid { RowDefinitions = new("Auto,Auto"), Margin = new Thickness(4, 3), Children = { primary, WithRow(new TextBlock { Text = node.Message + candidates, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#7F8BA0") }, 1) } };
    }

    private static bool IsDependencyRoot(string path) => File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".m2" or ".wmo" or ".adt" or ".wdt";

    private Control BuildBrowserPage()
    {
        var open = new Button { Content = "Open MPQ" }; open.Click += async (_, _) => await ChooseArchiveAsync();
        var listfile = new Button { Content = "External listfile…" }; listfile.Click += async (_, _) => await ChooseListfileAsync();
        var cancel = new Button { Content = "Cancel operation" }; cancel.Click += (_, _) => _operation?.Cancel();
        var toolbar = new WrapPanel { Children = { open, listfile, cancel, new TextBlock { Text = "Extraction:", VerticalAlignment = VerticalAlignment.Center }, _extractionWorkers } };
        var paths = new Grid { ColumnDefinitions = new("2*,*,2*"), ColumnSpacing = 8, Children = { _archivePath, WithColumn(_externalListfile, 1), WithColumn(_browserSearch, 2) } };
        var modes = new TabControl { Items = { new TabItem { Header = "Folders", Content = BuildFolderBrowser() }, new TabItem { Header = "Flat search", Content = BuildFlatBrowser() } } };
        return new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(8), RowSpacing = 8, Children = { toolbar, WithRow(paths, 1), WithRow(modes, 2), WithRow(_browserStatus, 3) } };
    }

    private Control BuildFolderBrowser()
    {
        var root = new Button { Content = "Root" }; root.Click += (_, _) => NavigateTree(string.Empty); var up = new Button { Content = "↑ Up" }; up.Click += (_, _) => NavigateTree(MpqArchiveBrowser.Parent(_treeFolder)); var open = new Button { Content = "Open selected folder" }; open.Click += (_, _) => OpenSelectedTreeFolder();
        var extractSelected = new Button { Content = "Extract selected files/folders" }; extractSelected.Click += async (_, _) => await ExtractAsync(MpqArchiveBrowser.Select(_allArchiveEntries, _treeItems.SelectedItems?.OfType<MpqBrowserNode>() ?? []));
        var extractFolder = AccentButton("Extract current folder recursively"); extractFolder.Click += async (_, _) => await ExtractAsync(MpqArchiveBrowser.SelectFolder(_allArchiveEntries, _treeFolder));
        var controls = new WrapPanel { Children = { root, up, open, extractSelected, extractFolder, _treeBreadcrumb } };
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6, Children = { controls, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _treeItems }, 1) } };
    }

    private Control BuildFlatBrowser()
    {
        var extractSelected = new Button { Content = "Extract selected" }; extractSelected.Click += async (_, _) => await ExtractAsync(_browserItems.SelectedItems?.OfType<MpqFileEntry>().ToArray() ?? []);
        var extractAll = AccentButton("Extract all visible"); extractAll.Click += async (_, _) => await ExtractAsync((_browserItems.ItemsSource as IEnumerable<MpqFileEntry>)?.ToArray() ?? []);
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6, Children = { new WrapPanel { Children = { extractSelected, extractAll, new TextBlock { Text = "The search box above filters this global path list. Folder mode stays at its current location.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7"), VerticalAlignment = VerticalAlignment.Center } } }, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _browserItems }, 1) } };
    }

    private Control BuildCascBrowserPage()
    {
        var open = new Button { Content = "Open CASC storage" }; open.Click += async (_, _) => await ChooseCascStorageAsync();
        var listfile = new Button { Content = "External listfile…" }; listfile.Click += async (_, _) => await ChooseCascListfileAsync();
        var cancel = new Button { Content = "Cancel operation" }; cancel.Click += (_, _) => _operation?.Cancel();
        var provider = new TextBlock { Text = CascArchiveService.IsNativeProviderAvailable() ? "Native provider ready" : "Native provider missing from this development build", Foreground = Brush.Parse(CascArchiveService.IsNativeProviderAvailable() ? "#76B78B" : "#D7A15D"), VerticalAlignment = VerticalAlignment.Center };
        var toolbar = new WrapPanel { Children = { open, listfile, cancel, provider } };
        var paths = new Grid { ColumnDefinitions = new("2*,*,2*"), ColumnSpacing = 8, Children = { _cascStoragePath, WithColumn(_cascListfile, 1), WithColumn(_cascSearch, 2) } };
        var modes = new TabControl { Items = { new TabItem { Header = "Folders", Content = BuildCascFolderBrowser() }, new TabItem { Header = "Flat search", Content = BuildCascFlatBrowser() } } };
        var explanation = new TextBlock { Text = "CASC support is intentionally read-only: list, search, browse, and extract. It never downloads missing CDN data and never mutates the client storage.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
        return new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(8), RowSpacing = 8, Children = { toolbar, WithRow(paths, 1), WithRow(explanation, 2), WithRow(modes, 3), WithRow(_cascStatus, 4) } };
    }

    private Control BuildCascFolderBrowser()
    {
        var root = new Button { Content = "Root" }; root.Click += (_, _) => NavigateCascTree(string.Empty);
        var up = new Button { Content = "↑ Up" }; up.Click += (_, _) => NavigateCascTree(MpqArchiveBrowser.Parent(_cascTreeFolder));
        var open = new Button { Content = "Open selected folder" }; open.Click += (_, _) => OpenSelectedCascTreeFolder();
        var extractSelected = new Button { Content = "Extract selected files/folders" }; extractSelected.Click += async (_, _) => await ExtractCascAsync(SelectCascTree(_cascTreeItems.SelectedItems?.OfType<MpqBrowserNode>() ?? []));
        var extractFolder = AccentButton("Extract current folder recursively"); extractFolder.Click += async (_, _) => await ExtractCascAsync(SelectCascFolder(_cascTreeFolder));
        var controls = new WrapPanel { Children = { root, up, open, extractSelected, extractFolder, _cascBreadcrumb } };
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6, Children = { controls, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _cascTreeItems }, 1) } };
    }

    private Control BuildCascFlatBrowser()
    {
        var extractSelected = new Button { Content = "Extract selected" }; extractSelected.Click += async (_, _) => await ExtractCascAsync(_cascItems.SelectedItems?.OfType<CascFileEntry>().ToArray() ?? []);
        var extractAll = AccentButton("Extract all visible"); extractAll.Click += async (_, _) => await ExtractCascAsync((_cascItems.ItemsSource as IEnumerable<CascFileEntry>)?.ToArray() ?? []);
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6, Children = { new WrapPanel { Children = { extractSelected, extractAll, new TextBlock { Text = "FileDataId/key-only rows remain visible so unknown names are not silently discarded.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7"), VerticalAlignment = VerticalAlignment.Center } } }, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _cascItems }, 1) } };
    }

    private Control BuildMergePage()
    {
        var add = AccentButton("Add source MPQs"); add.Click += async (_, _) => { var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select small patch MPQs to merge", AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("MPQ patches") { Patterns = ["*.mpq"] }] }); AddMergePaths(files.Select(file => file.TryGetLocalPath()).OfType<string>()); };
        var remove = new Button { Content = "Remove selected" }; remove.Click += (_, _) => { var selected = _mergeInputs.SelectedItems?.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? []; _mergePaths.RemoveAll(selected.Contains); RefreshMergeInputs(); };
        var earlier = new Button { Content = "Move earlier" }; earlier.Click += (_, _) => MoveMergeInput(-1);
        var later = new Button { Content = "Move later" }; later.Click += (_, _) => MoveMergeInput(1);
        var clear = new Button { Content = "Clear" }; clear.Click += (_, _) => { _mergePaths.Clear(); RefreshMergeInputs(); };
        var merge = AccentButton("Merge into new MPQ…"); merge.Click += async (_, _) => await MergePatchesAsync();
        var policy = new StackPanel { Children = { new TextBlock { Text = "Different-byte path conflict policy" }, _mergePolicy } };
        var explanation = new TextBlock { Text = "Drop two or more deliberately small patch MPQs below, or add them with the picker. Exact duplicate internal paths are verified by SHA-256 and byte-for-byte comparison, then stored once. Different bytes at the same path block the merge by default. Choosing earlier/later archive is an explicit global conflict decision; source MPQs are always immutable.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
        var dropTarget = new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _mergeInputs };
        DragDrop.SetAllowDrop(dropTarget, true);
        DragDrop.AddDragOverHandler(dropTarget, (_, args) =>
        {
            args.DragEffects = args.DataTransfer.TryGetFiles()?.Any(file => file.TryGetLocalPath() is { } path && File.Exists(path) && Path.GetExtension(path).Equals(".mpq", StringComparison.OrdinalIgnoreCase)) == true ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        });
        DragDrop.AddDropHandler(dropTarget, (_, args) =>
        {
            AddMergePaths(args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []);
            args.Handled = true;
        });
        return new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(12), Children = { new WrapPanel { Children = { add, remove, earlier, later, clear, policy, merge } }, WithRow(explanation, 1), WithRow(dropTarget, 2), WithRow(_mergeStatus, 3) } };
    }

    private void AddMergePaths(IEnumerable<string> paths)
    {
        var rejected = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !Path.GetExtension(path).Equals(".mpq", StringComparison.OrdinalIgnoreCase)) { rejected++; continue; }
            var full = Path.GetFullPath(path);
            if (!_mergePaths.Contains(full, StringComparer.OrdinalIgnoreCase)) _mergePaths.Add(full);
        }
        RefreshMergeInputs();
        if (rejected > 0) _mergeStatus.Text += $" Ignored {rejected:N0} non-MPQ or missing path(s).";
    }

    private void RefreshMergeInputs() { _mergeInputs.ItemsSource = _mergePaths.ToArray(); _mergeStatus.Text = $"{_mergePaths.Count:N0} source patch(es). Listed order controls earlier/later conflict precedence."; }
    private void MoveMergeInput(int direction) { if (_mergeInputs.SelectedItem is not string path) return; var index = _mergePaths.FindIndex(value => value.Equals(path, StringComparison.OrdinalIgnoreCase)); var target = index + direction; if (index < 0 || target < 0 || target >= _mergePaths.Count) return; (_mergePaths[index], _mergePaths[target]) = (_mergePaths[target], _mergePaths[index]); RefreshMergeInputs(); _mergeInputs.SelectedItem = path; }

    private async Task MergePatchesAsync()
    {
        if (_mergePaths.Count < 2) { _mergeStatus.Text = "Add at least two source MPQs."; return; }
        var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save merged patch MPQ", SuggestedFileName = "patch-merged.MPQ", FileTypeChoices = [new FilePickerFileType("MPQ patch") { Patterns = ["*.mpq"] }] }); var output = file?.TryGetLocalPath(); if (output is null) return;
        BeginOperation(); try
        {
            var inputs = _mergePaths.ToArray(); var policy = _mergePolicy.SelectedItem is MpqMergeConflictPolicy selected ? selected : MpqMergeConflictPolicy.BlockDifferentEntries; var progress = new Progress<(int Done, int Total, string Path)>(value => _mergeStatus.Text = $"Extracting {value.Done:N0}/{value.Total:N0} · {value.Path}");
            var result = await Task.Run(() => new MpqMergeService().Merge(inputs, output, policy, string.IsNullOrWhiteSpace(_externalListfile.Text) ? null : _externalListfile.Text, progress, _operation!.Token), _operation!.Token);
            _mergeStatus.Text = result.Conflicts.Count > 0 && result.OutputFiles == 0
                ? $"Merge blocked: {result.Conflicts.Count:N0} different-byte internal path conflict(s). No output was created.\n" + string.Join("\n", result.Conflicts.Take(100).Select(conflict => conflict.ArchivePath))
                : $"Created {result.OutputPath} with {result.OutputFiles:N0} files. Deduplicated {result.ExactDuplicates:N0} exact copies; resolved {result.Conflicts.Count:N0} explicit different-byte conflict(s) using {result.Policy}.";
        }
        catch (OperationCanceledException) { _mergeStatus.Text = "Merge cancelled; source archives were untouched."; }
        catch (Exception exception) { _mergeStatus.Text = $"Merge failed safely: {exception.Message}"; DesktopCrashLogger.Log("MPQ merge failed", exception); }
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
            _targetClientRequirement = null;
            foreach (var mapped in PatchInputMapper.Map(paths)) { var existing = _entries.FindIndex(row => row.ArchivePath.Equals(mapped.ArchivePath, StringComparison.OrdinalIgnoreCase)); var row = new PatchRow(mapped.SourcePath, mapped.ArchivePath); if (existing >= 0) _entries[existing] = row; else _entries.Add(row); }
            RefreshBuilder();
        }
        catch (Exception exception) { _builderStatus.Text = exception.Message; DesktopCrashLogger.Log("Patch input mapping failed", exception); }
    }

    private void RemoveSelected() { var selected = _builderItems.SelectedItems?.OfType<PatchRow>().ToHashSet() ?? []; _entries.RemoveAll(selected.Contains); _targetClientRequirement = null; RefreshBuilder(); }
    private void RefreshBuilder() { _builderItems.ItemsSource = _entries.OrderBy(row => row.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray(); var warnings = _entries.Count(row => PatchInputMapper.AssessArchivePath(row.ArchivePath).HasWarning); _builderStatus.Text = $"{_entries.Count:N0} file(s) · {warnings:N0} path warning(s) · {(_existingPatch is null ? "new tiny MPQ" : $"transaction-safe update of {Path.GetFileName(_existingPatch)}")}{(_targetClientRequirement is null ? string.Empty : $" · bound to {_targetClientRequirement.ClientName} with {_targetClientRequirement.InheritedAssets.Count:N0} inherited path(s)")}"; }
    private PatchEntry[] CurrentEntries() => _entries.Select(row => new PatchEntry(row.SourcePath, row.ArchivePath)).ToArray();
    private PatchManifestPolicy? CurrentPolicy()
    {
        static string[] Lines(string? text) => (text ?? string.Empty).Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowed = Lines(_allowed.Text); var forbidden = Lines(_forbidden.Text); var required = Lines(_required.Text); var expected = _expectedCount.Value is > 0 ? (int?)_expectedCount.Value.Value : null;
        return allowed.Length == 0 && forbidden.Length == 0 && required.Length == 0 && expected is null ? null : new(allowed, forbidden, expected, required);
    }

    private async Task ChooseExistingPatchAsync() { var path = await PickFileAsync("Select a deliberately small patch MPQ to update", ["*.mpq"]); if (path is null) return; _existingPatch = path; _targetClientRequirement = null; _builderStatus.Text = $"Updating {path}. Existing entries remain; matching internal paths are replaced. Archives above {PatchArchiveService.MaximumSafeUpdateBytes / (1024 * 1024):N0} MiB are refused."; }
    private async Task BindClientAsync() { var path = await PickFileAsync("Bind a compatible build-12340 Wow.exe", ["Wow.exe", "*.exe"]); if (path is null) return; _requiredClientExecutableSha256 = await Task.Run(() => PatchManifestService.ComputeExecutableSha256(path)); _bindingStatus.Text = $"Bound {Path.GetFileName(path)} · SHA-256 {_requiredClientExecutableSha256}"; }

    private async Task SaveManifestAsync()
    {
        try
        {
            var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save Crucible patch manifest", SuggestedFileName = "patch.crucible-patch.json", FileTypeChoices = [new FilePickerFileType("Crucible patch manifest") { Patterns = ["*.crucible-patch.json", "*.json"] }] }); var path = file?.TryGetLocalPath(); if (path is null) return;
            PatchManifestService.Save(path, Path.GetFileNameWithoutExtension(path), _existingPatch is null ? "patch-W.mpq" : Path.GetFileName(_existingPatch), CurrentEntries(), _requiredClientExecutableSha256, CurrentPolicy(), _targetClientRequirement); _builderStatus.Text = $"Saved manifest {path}{(_targetClientRequirement is null ? string.Empty : $" bound to target fingerprint {_targetClientRequirement.IndexFingerprint}")}";
        }
        catch (Exception exception) { _builderStatus.Text = $"Manifest save failed: {exception.Message}"; DesktopCrashLogger.Log("Manifest save failed", exception); }
    }

    private async Task LoadManifestAsync()
    {
        try
        {
            var path = await PickFileAsync("Load Crucible patch manifest", ["*.crucible-patch.json", "*.json"]); if (path is null) return; var manifest = await Task.Run(() => PatchManifestService.Load(path));
            _entries.Clear(); _entries.AddRange(manifest.Entries.Select(entry => new PatchRow(entry.SourcePath, entry.ArchivePath))); _existingPatch = null; _requiredClientExecutableSha256 = manifest.RequiredClientExecutableSha256; _targetClientRequirement = manifest.TargetClient; ApplyPolicy(manifest.Policy); _bindingStatus.Text = (_requiredClientExecutableSha256 is null ? "No client executable binding." : $"Required client SHA-256 {_requiredClientExecutableSha256}") + (_targetClientRequirement is null ? string.Empty : $" · target {_targetClientRequirement.ClientName} fingerprint {_targetClientRequirement.IndexFingerprint}"); RefreshBuilder();
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
            string? companionManifest = null; if (_targetClientRequirement is not null) { companionManifest = output + ".crucible-patch.json"; PatchManifestService.Save(companionManifest, Path.GetFileNameWithoutExtension(output), Path.GetFileName(output), entries, _requiredClientExecutableSha256, CurrentPolicy(), _targetClientRequirement); }
            ClientCacheInvalidationResult? cache = null; if (ClientPatchDeploymentService.IsInsideClientData(output, _session.Settings.ClientDataPath)) { var root = Directory.GetParent(_session.Settings.ClientDataPath)?.FullName; if (root is not null) cache = await Task.Run(() => ClientPatchDeploymentService.InvalidateCache(root)); }
            _builderStatus.Text = $"{(update ? "Updated" : "Created")} {output} with {entries.Length:N0} file(s).{(companionManifest is null ? string.Empty : $" Target-bound companion manifest: {companionManifest}.")}{(cache is null ? string.Empty : cache.Existed ? $" Deleted the entire client Cache ({cache.DeletedFiles:N0} files)." : " Client Cache was already absent.")}";
        }
        catch (Exception exception) { _builderStatus.Text = $"Build failed: {exception.Message}"; DesktopCrashLogger.Log("MPQ build failed", exception); }
    }

    private async Task ChooseArchiveAsync() { var path = await PickFileAsync("Open an MPQ archive", ["*.mpq"]); if (path is null) return; _archivePath.Text = path; await LoadArchiveAsync(); }
    private async Task ChooseListfileAsync() { var path = await PickFileAsync("Select an external MPQ listfile", ["*.txt", "*"]); if (path is null) return; _externalListfile.Text = path; if (File.Exists(_archivePath.Text)) await LoadArchiveAsync(); }
    private async Task LoadArchiveAsync()
    {
        BeginOperation();
        try { _browserStatus.Text = "Reading archive index or app-local cache…"; var archive = _archivePath.Text ?? string.Empty; var listfile = string.IsNullOrWhiteSpace(_externalListfile.Text) ? null : _externalListfile.Text; var indexed = await Task.Run(() => MpqArchiveIndexCache.LoadOrCreate(archive, listfile, () => new PatchArchiveService().ListFiles(archive, "*", listfile), _operation!.Token), _operation!.Token); _allArchiveEntries = indexed.Entries; FilterArchive(); NavigateTree(string.Empty); var anonymous = _allArchiveEntries.Count(entry => !entry.IsMetadata && ClientArchiveIndexService.IsAnonymous(entry.ArchivePath)); _browserStatus.Text = $"Loaded {_allArchiveEntries.Count:N0} entries from {(indexed.Cached ? "app-local index cache" : "the MPQ")} · {archive}.{(anonymous == 0 ? string.Empty : $" {anonymous:N0} anonymous hash-table name(s) remain; load an external listfile to attempt name recovery.")}"; }
        catch (Exception exception) { _browserStatus.Text = $"Open failed: {exception.Message}"; DesktopCrashLogger.Log("MPQ open failed", exception); }
    }

    private void FilterArchive() { var filtered = _allArchiveEntries.Where(entry => MpqPathFilter.Matches(entry.ArchivePath, _browserSearch.Text)).ToArray(); _browserItems.ItemsSource = filtered; if (_allArchiveEntries.Count > 0) _browserStatus.Text = $"Showing {filtered.Length:N0} of {_allArchiveEntries.Count:N0} archive entries."; }
    private void NavigateTree(string folder)
    {
        try { var page = MpqArchiveBrowser.Browse(_allArchiveEntries, folder); _treeFolder = page.CurrentFolder; _treeItems.ItemsSource = page.Nodes; BuildBreadcrumb(page); _browserStatus.Text = $"{(page.CurrentFolder.Length == 0 ? "MPQ root" : page.CurrentFolder)} · {page.Nodes.Count:N0} direct node(s) · {page.RecursiveFiles:N0} recursive file(s) · {FormatBytes(page.RecursiveBytes)}{(page.AnonymousFiles == 0 ? string.Empty : $" · {page.AnonymousFiles:N0} anonymous name(s)")}."; }
        catch (Exception exception) { _browserStatus.Text = $"Folder navigation failed: {exception.Message}"; }
    }
    private void OpenSelectedTreeFolder() { if (_treeItems.SelectedItem is MpqBrowserNode { IsFolder: true } folder) NavigateTree(folder.ArchivePath); }
    private void BuildBreadcrumb(MpqFolderPage page)
    {
        _treeBreadcrumb.Children.Clear(); var root = new Button { Content = "MPQ" }; root.Click += (_, _) => NavigateTree(string.Empty); _treeBreadcrumb.Children.Add(root);
        foreach (var path in page.Breadcrumbs) { _treeBreadcrumb.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0) }); var button = new Button { Content = path.Split('\\')[^1] }; button.Click += (_, _) => NavigateTree(path); _treeBreadcrumb.Children.Add(button); }
    }
    private async Task ExtractAsync(IReadOnlyList<MpqFileEntry> entries)
    {
        if (entries.Count == 0) { _browserStatus.Text = "Select at least one archive entry."; return; } var destination = await PickFolderAsync("Select extraction destination"); if (destination is null) return; BeginOperation();
        try { var archivePath = _archivePath.Text ?? string.Empty; var workers = _extractionWorkers.SelectedIndex switch { 1 => 1, 2 => 2, 3 => 4, 4 => 8, 5 => 16, _ => 0 }; var workerLabel = workers == 0 ? $"auto (up to {PatchArchiveService.RecommendedExtractionWorkers})" : workers.ToString(); var progress = new Progress<(int Done, int Total, string Path)>(value => _browserStatus.Text = $"Extracting {value.Done:N0}/{value.Total:N0} · {value.Path}"); await Task.Run(() => new PatchArchiveService().Extract(archivePath, destination, entries, progress, _operation!.Token, workers: workers)); _browserStatus.Text = $"Extracted {entries.Count:N0} entries to {destination} using {workerLabel} worker(s)."; }
        catch (OperationCanceledException) { _browserStatus.Text = "Extraction cancelled."; }
        catch (Exception exception) { _browserStatus.Text = $"Extraction failed: {exception.Message}"; DesktopCrashLogger.Log("MPQ extraction failed", exception); }
    }

    private async Task ChooseCascStorageAsync()
    {
        var path = await PickFolderAsync("Select a CASC client or storage folder");
        if (path is null) return;
        _cascStoragePath.Text = path;
        await LoadCascAsync();
    }

    private async Task ChooseCascListfileAsync()
    {
        var path = await PickFileAsync("Select an external CASC listfile", ["*.txt", "*"]);
        if (path is null) return;
        _cascListfile.Text = path;
        if (Directory.Exists(_cascStoragePath.Text)) await LoadCascAsync();
    }

    private async Task LoadCascAsync()
    {
        BeginOperation();
        try
        {
            _cascStatus.Text = "Opening CASC storage read-only and enumerating its root…";
            var root = _cascStoragePath.Text ?? string.Empty;
            var listfile = string.IsNullOrWhiteSpace(_cascListfile.Text) ? null : _cascListfile.Text;
            _allCascEntries = await Task.Run(() => new CascArchiveService().ListFiles(root, "*", listfile, _operation!.Token), _operation!.Token);
            _cascTreeEntries = _allCascEntries.GroupBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(group => BestCascEntry(group)).Select(entry => new MpqFileEntry(entry.ArchivePath, entry.Size, entry.Size, entry.ContentFlags, entry.Locale)).ToArray();
            FilterCasc(); NavigateCascTree(string.Empty);
            var unavailable = _allCascEntries.Count(entry => !entry.IsAvailableLocally); var synthetic = _allCascEntries.Count(entry => entry.NameType != CascEntryNameType.FullPath);
            _cascStatus.Text = $"Loaded {_allCascEntries.Count:N0} CASC row(s) · {_cascTreeEntries.Count:N0} unique path(s) · {synthetic:N0} synthetic FileDataId/key name(s) · {unavailable:N0} not stored locally. Storage was not modified.";
        }
        catch (OperationCanceledException) { _cascStatus.Text = "CASC enumeration cancelled."; }
        catch (Exception exception) { _cascStatus.Text = $"CASC open failed: {exception.Message}"; DesktopCrashLogger.Log("CASC open failed", exception); }
    }

    private void FilterCasc()
    {
        var filtered = _allCascEntries.Where(entry => MpqPathFilter.Matches(entry.ArchivePath, _cascSearch.Text)).ToArray();
        _cascItems.ItemsSource = filtered;
        if (_allCascEntries.Count > 0) _cascStatus.Text = $"Showing {filtered.Length:N0} of {_allCascEntries.Count:N0} CASC rows.";
    }

    private void NavigateCascTree(string folder)
    {
        try
        {
            var page = MpqArchiveBrowser.Browse(_cascTreeEntries, folder); _cascTreeFolder = page.CurrentFolder; _cascTreeItems.ItemsSource = page.Nodes; BuildCascBreadcrumb(page);
            _cascStatus.Text = $"{(page.CurrentFolder.Length == 0 ? "CASC root" : page.CurrentFolder)} · {page.Nodes.Count:N0} direct node(s) · {page.RecursiveFiles:N0} recursive file(s) · {FormatBytes(page.RecursiveBytes)}.";
        }
        catch (Exception exception) { _cascStatus.Text = $"CASC folder navigation failed: {exception.Message}"; }
    }

    private void OpenSelectedCascTreeFolder()
    {
        if (_cascTreeItems.SelectedItem is MpqBrowserNode { IsFolder: true } folder) NavigateCascTree(folder.ArchivePath);
    }

    private void BuildCascBreadcrumb(MpqFolderPage page)
    {
        _cascBreadcrumb.Children.Clear(); var root = new Button { Content = "CASC" }; root.Click += (_, _) => NavigateCascTree(string.Empty); _cascBreadcrumb.Children.Add(root);
        foreach (var path in page.Breadcrumbs) { _cascBreadcrumb.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0) }); var button = new Button { Content = path.Split('\\')[^1] }; button.Click += (_, _) => NavigateCascTree(path); _cascBreadcrumb.Children.Add(button); }
    }

    private IReadOnlyList<CascFileEntry> SelectCascTree(IEnumerable<MpqBrowserNode> nodes)
    {
        var paths = MpqArchiveBrowser.Select(_cascTreeEntries, nodes).Select(entry => entry.ArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _allCascEntries.Where(entry => paths.Contains(entry.ArchivePath)).GroupBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(BestCascEntry).ToArray();
    }

    private IReadOnlyList<CascFileEntry> SelectCascFolder(string folder)
    {
        var paths = MpqArchiveBrowser.SelectFolder(_cascTreeEntries, folder).Select(entry => entry.ArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _allCascEntries.Where(entry => paths.Contains(entry.ArchivePath)).GroupBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(BestCascEntry).ToArray();
    }

    private static CascFileEntry BestCascEntry(IEnumerable<CascFileEntry> entries) => entries.OrderByDescending(entry => entry.IsAvailableLocally).ThenBy(entry => entry.NameType).ThenBy(entry => entry.Locale == 0x2 ? 0 : 1).First();

    private async Task ExtractCascAsync(IReadOnlyList<CascFileEntry> entries)
    {
        if (entries.Count == 0) { _cascStatus.Text = "Select at least one CASC entry."; return; }
        var available = entries.Where(entry => entry.IsAvailableLocally).ToArray(); var skipped = entries.Count - available.Length;
        if (available.Length == 0) { _cascStatus.Text = "The selected CASC entries are not stored locally. Crucible will not download CDN data implicitly."; return; }
        var destination = await PickFolderAsync("Select CASC extraction destination"); if (destination is null) return; BeginOperation();
        try
        {
            var root = _cascStoragePath.Text ?? string.Empty; var progress = new Progress<(int Done, int Total, string Path)>(value => _cascStatus.Text = $"Extracting {value.Done:N0}/{value.Total:N0} · {value.Path}");
            await Task.Run(() => new CascArchiveService().Extract(root, destination, available, progress, _operation!.Token), _operation!.Token);
            _cascStatus.Text = $"Extracted {available.Length:N0} CASC file(s) to {destination}.{(skipped == 0 ? string.Empty : $" Skipped {skipped:N0} entry/entries not stored locally; no download was attempted.")}";
        }
        catch (OperationCanceledException) { _cascStatus.Text = "CASC extraction cancelled."; }
        catch (Exception exception) { _cascStatus.Text = $"CASC extraction failed: {exception.Message}"; DesktopCrashLogger.Log("CASC extraction failed", exception); }
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
