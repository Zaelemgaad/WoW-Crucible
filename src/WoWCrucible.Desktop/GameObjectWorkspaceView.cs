using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class GameObjectWorkspaceView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly NumericUpDown _entry = Number(1, uint.MaxValue, 900000);
    private readonly ComboBox _type = new() { ItemsSource = GameObjectTypeCatalog.All };
    private readonly NumericUpDown _displayId = Number(0, uint.MaxValue);
    private readonly TextBox _name = new() { Text = "New Crucible Gameobject" };
    private readonly TextBox _iconName = new(); private readonly TextBox _castCaption = new(); private readonly TextBox _unknownText = new();
    private readonly NumericUpDown _size = Number(0.01m, 100000, 1); private readonly TextBox _aiName = new(); private readonly TextBox _scriptName = new();
    private readonly NumericUpDown[] _data = Enumerable.Range(0, 24).Select(_ => Number(int.MinValue, uint.MaxValue)).ToArray();
    private readonly TextBlock[] _dataLabels = Enumerable.Range(0, 24).Select(_ => new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }).ToArray();
    private readonly CheckBox _includeSpawn = new() { Content = "Create a world spawn with this template" };
    private readonly NumericUpDown _guid = Number(1, uint.MaxValue, 9000000); private readonly NumericUpDown _map = Number(0, ushort.MaxValue); private readonly NumericUpDown _zone = Number(0, ushort.MaxValue); private readonly NumericUpDown _area = Number(0, ushort.MaxValue);
    private readonly NumericUpDown _spawnMask = Number(1, byte.MaxValue, 1); private readonly NumericUpDown _phaseMask = Number(1, uint.MaxValue, 1);
    private readonly NumericUpDown _x = Number(decimal.MinValue, decimal.MaxValue); private readonly NumericUpDown _y = Number(decimal.MinValue, decimal.MaxValue); private readonly NumericUpDown _z = Number(decimal.MinValue, decimal.MaxValue); private readonly NumericUpDown _orientation = Number(decimal.MinValue, decimal.MaxValue);
    private readonly NumericUpDown _rotation0 = Number(-1, 1); private readonly NumericUpDown _rotation1 = Number(-1, 1); private readonly NumericUpDown _rotation2 = Number(-1, 1); private readonly NumericUpDown _rotation3 = Number(-1, 1, 1);
    private readonly NumericUpDown _respawn = Number(int.MinValue, int.MaxValue, 300); private readonly NumericUpDown _animProgress = Number(0, byte.MaxValue, 255); private readonly NumericUpDown _state = Number(0, byte.MaxValue, 1);
    private readonly TextBox _spawnScript = new(); private readonly TextBox _comment = new();
    private readonly StackPanel _lootRows = new() { Spacing = 7 }; private readonly TextBox _startsQuests = new() { PlaceholderText = "Quest IDs separated by commas, spaces, or new lines" }; private readonly TextBox _endsQuests = new() { PlaceholderText = "Quest IDs separated by commas, spaces, or new lines" };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap }; private readonly TextBox _sql = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly M2PreviewView _model = new(); private readonly WmoPreviewView _wmo = new() { IsVisible = false }; private readonly Grid _modelHost = new(); private readonly TextBlock _modelStatus = Status("Load an extracted WotLK M2/SKIN or version-17 root WMO for this display.");
    private readonly CheckBox _showAttachments = new() { Content = "Show attachment points" }; private readonly ComboBox _attachmentPicker = new() { PlaceholderText = "No attachment points loaded" };
    private readonly TextBlock _status = Status("Offline current-core gameobject schema ready."); private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly Button _commit = AccentButton("Insert into connected world database"); private WorldContentWritePlan? _pendingPlan; private uint? _loadedEntry;
    private readonly TextBox _bulkSources = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "One extracted .m2, root .wmo, or folder per line" };
    private readonly TextBox _bulkLibrary = new(); private readonly TextBox _bulkClientRoot = new() { PlaceholderText = "Optional root whose children use exact client paths" };
    private readonly TextBox _bulkIndex = new() { PlaceholderText = "Complete client-index folder from Client workshop" };
    private readonly TextBox _bulkVirtualSources = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "One virtual client path per line, for example World\\Generic\\PassiveDoodads\\model.m2" };
    private readonly TextBox _bulkArchiveChoices = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "Optional ambiguity choices: client path | Data\\patch-X.MPQ" };
    private readonly NumericUpDown _bulkDisplayStart = Number(1, uint.MaxValue, 100000); private readonly NumericUpDown _bulkTemplateStart = Number(1, uint.MaxValue, 100000);
    private readonly TextBlock _bulkStatus = Status("Select models, then build a review plan. Nothing is written while planning.");
    private readonly TextBox _bulkPreview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly Button _bulkApply = AccentButton("Create DBC + SQL + tiny MPQ bundle…"); private GameObjectBulkPlan? _bulkPlan;
    private CancellationTokenSource? _modelOperation; private CancellationTokenSource? _bulkOperation;

    public event EventHandler? BackRequested;
    public event EventHandler? ProjectWorkspaceRequested;
    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public GameObjectWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged; _type.SelectedIndex = 3; _bulkApply.IsEnabled = false; HookEvents(); RefreshDataLabels(); _modelHost.Children.Add(_model); _modelHost.Children.Add(_wmo);
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "GAMEOBJECTS", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, 1) } };
        _bulkLibrary.Text = session.Settings.ProcessedAssetLibraryPath; _bulkIndex.Text = session.Settings.ClientIndexPath;
        var editor = new TabControl { Items = { new TabItem { Header = "Template", Content = new ScrollViewer { Content = TemplateForm() } }, new TabItem { Header = "Type-aware Data0–23", Content = DataPage() }, new TabItem { Header = "World spawn", Content = new ScrollViewer { Content = SpawnForm() } }, new TabItem { Header = "Loot & quests", Content = LootQuestPage() }, new TabItem { Header = "Bulk model import", Content = BulkPage() } } };
        var loadModel = new Button { Content = "Load extracted M2 / WMO…" }; loadModel.Click += async (_, _) => await LoadModelAsync(); var clearModel = new Button { Content = "Clear" }; clearModel.Click += (_, _) => { _modelOperation?.Cancel(); _model.ClearGeometry(); _wmo.ClearGeometry(); _attachmentPicker.ItemsSource = null; _model.SetAttachmentOverlay(false); _modelStatus.Text = "Model preview cleared."; };
        _showAttachments.Click += (_, _) => ApplyAttachmentOverlay(); _attachmentPicker.SelectionChanged += (_, _) => ApplyAttachmentOverlay();
        var modelPage = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { new WrapPanel { Children = { loadModel, clearModel, _showAttachments, _attachmentPicker } }, WithRow(new Border { Background = Brush.Parse("#090D14"), Child = _modelHost }, 1), WithRow(_modelStatus, 2) } };
        var preview = new TabControl { Items = { new TabItem { Header = "Decoded summary", Content = new ScrollViewer { Content = new Border { Padding = new Thickness(16), BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _summary } } }, new TabItem { Header = "3D model", Content = modelPage }, new TabItem { Header = "SQL change plan", Content = _sql } } };
        var workspace = new ResponsiveSplitGrid(editor, preview, 3, 2);
        var reserveId = AccentButton("Reserve project ID"); reserveId.Click += async (_, _) => await ReserveProjectIdAsync(reserveId); var export = new Button { Content = "Export SQL…" }; export.Click += async (_, _) => await ExportAsync(); var exportDraft = new Button { Content = "Export portable draft…" }; exportDraft.Click += async (_, _) => await ExportDraftAsync(); _commit.Click += (_, _) => PrepareCommit();
        Content = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(workspace, 1), WithRow(new WrapPanel { Children = { reserveId, export, exportDraft, _commit, _status } }, 2), WithRow(_confirmation, 3) } };
        RefreshPreview(); RefreshSchemaStatus();
    }

    private async Task ReserveProjectIdAsync(Button button)
    {
        if (string.IsNullOrWhiteSpace(_session.Settings.ActiveProjectPath)) { _status.Text = "Opening Projects & shared IDs to create or choose a project…"; ProjectWorkspaceRequested?.Invoke(this, EventArgs.Empty); return; }
        try
        {
            button.IsEnabled = false; var prior = (uint)(_entry.Value ?? 0); var purpose = _loadedEntry is null ? $"New gameobject: {_name.Text}" : $"Gameobject variant of {prior}: {_name.Text}";
            var reserved = await ProjectIdReservationBridge.ReserveNextAsync(_session, ContentIdDomain.GameObject, purpose); _entry.Value = reserved.SingleId; _loadedEntry = null; _commit.Content = "Insert into connected world database"; RefreshPreview();
            _status.Text = $"Reserved gameobject ID {reserved.SingleId:N0} in {reserved.ProjectName}. The current decoded fields are now a new INSERT draft; no SQL was written.";
        }
        catch (Exception exception) { _status.Text = $"Gameobject ID reservation failed: {exception.Message}"; DesktopCrashLogger.Log("Gameobject ID reservation failed", exception); }
        finally { button.IsEnabled = true; }
    }

    public void OpenGameObjectRow(IReadOnlyDictionary<string, object?> row)
    {
        _entry.Value = Decimal(row, "entry", 1); SetType(Int(row, "type")); _displayId.Value = Decimal(row, "displayId"); _name.Text = Text(row, "name"); _iconName.Text = Text(row, "IconName"); _castCaption.Text = Text(row, "castBarCaption"); _unknownText.Text = Text(row, "unk1"); _size.Value = Decimal(row, "size", 1); _aiName.Text = Text(row, "AIName"); _scriptName.Text = Text(row, "ScriptName");
        for (var index = 0; index < 24; index++) _data[index].Value = Decimal(row, $"Data{index}"); _loadedEntry = (uint)(_entry.Value ?? 0); _commit.Content = "Apply decoded fields to existing gameobject"; RefreshDataLabels(); RefreshPreview(); _status.Text = $"Loaded gameobject template {_loadedEntry}. Additive spawn/loot/quest rows may be staged and committed with the update.";
    }

    private Control TemplateForm() => Form(("Entry ID", _entry), ("Gameobject type", _type), ("Display ID", _displayId), ("Name", _name), ("Icon name", _iconName), ("Cast-bar caption", _castCaption), ("Unknown text (unk1)", _unknownText), ("Scale / size", _size), ("AI name", _aiName), ("Script name", _scriptName));

    private Control DataPage()
    {
        var fields = new StackPanel { Spacing = 7, Margin = new Thickness(12) };
        fields.Children.Add(new TextBlock { Text = "Every raw field remains editable. Labels come from the selected type in the current AzerothCore GameObjectTemplate union; unused fields stay visible for custom cores and power users.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") });
        for (var index = 0; index < 24; index++) fields.Children.Add(new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Children = { _dataLabels[index], WithColumn(_data[index], 1) } });
        return new ScrollViewer { Content = fields };
    }

    private Control SpawnForm() => new StackPanel { Spacing = 8, Margin = new Thickness(12), Children = { _includeSpawn, Form(("Spawn GUID", _guid), ("Map ID", _map), ("Zone ID", _zone), ("Area ID", _area), ("Spawn mask", _spawnMask), ("Phase mask", _phaseMask), ("Position X", _x), ("Position Y", _y), ("Position Z", _z), ("Orientation", _orientation), ("Quaternion X", _rotation0), ("Quaternion Y", _rotation1), ("Quaternion Z", _rotation2), ("Quaternion W", _rotation3), ("Respawn seconds", _respawn), ("Animation progress", _animProgress), ("Initial state", _state), ("Spawn script", _spawnScript), ("Comment", _comment)) } };

    private Control LootQuestPage()
    {
        var add = AccentButton("Add loot row"); add.Click += (_, _) => AddLootRow();
        var findStart = new Button { Content = "Find and add starting quest…" };
        findStart.Click += (_, _) => RequestReference(ReferenceDomain.Quest, "Starting quest", _startsQuests);
        var findEnd = new Button { Content = "Find and add ending quest…" };
        findEnd.Click += (_, _) => RequestReference(ReferenceDomain.Quest, "Ending quest", _endsQuests);
        return new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(10), Children = { new StackPanel { Spacing = 7, Children = { add, new TextBlock { Text = "Loot is valid for chest [3] and fishing-hole [25] types. Quest links require quest-giver [2]. The editor blocks mismatched combinations instead of producing dead records.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") }, new TextBlock { Text = "Starts quests", FontWeight = FontWeight.SemiBold }, _startsQuests, findStart, new TextBlock { Text = "Ends quests", FontWeight = FontWeight.SemiBold }, _endsQuests, findEnd } }, WithRow(new ScrollViewer { Content = _lootRows }, 1) } };
    }

    private Control BulkPage()
    {
        var addFiles = new Button { Content = "Add M2 / WMO files…" }; addFiles.Click += async (_, _) => await AddBulkFilesAsync();
        var addFolder = new Button { Content = "Add folder…" }; addFolder.Click += async (_, _) => await AddBulkFolderAsync();
        var clear = new Button { Content = "Clear list" }; clear.Click += (_, _) => { _bulkSources.Text = string.Empty; _bulkPlan = null; _bulkApply.IsEnabled = false; _bulkPreview.Text = string.Empty; };
        var plan = AccentButton("Build collision/dependency plan"); plan.Click += async (_, _) => await PlanBulkAsync(plan);
        var browseIndex = new Button { Content = "Client index…" }; browseIndex.Click += async (_, _) => await PickBulkIndexAsync();
        var indexedPlan = AccentButton("Build directly from indexed paths…"); indexedPlan.Click += async (_, _) => await PlanIndexedBulkAsync(indexedPlan);
        _bulkApply.Click += async (_, _) => await ApplyBulkAsync();
        var fields = new StackPanel { Spacing = 9, Margin = new Thickness(12), Children =
        {
            new TextBlock { Text = "Bulk model → usable gameobjects", FontSize = 17, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "Adds only genuinely new GameObjectDisplayInfo rows, reuses matching model paths already in the DBC, generates complete Generic gameobject_template rows, resolves SKIN/WMO-group/texture dependencies, and builds a tiny MPQ. Ambiguous provenance or changed inputs block output instead of guessing.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") },
            new WrapPanel { Children = { addFiles, addFolder, clear } },
            _bulkSources,
            Form(("Processed asset library", _bulkLibrary), ("Optional client-path root", _bulkClientRoot), ("First display ID", _bulkDisplayStart), ("First template ID", _bulkTemplateStart)),
            new WrapPanel { Children = { plan, _bulkApply } },
            new Border { BorderBrush = Brush.Parse("#2B3548"), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = new StackPanel { Spacing = 8, Children =
            {
                new TextBlock { Text = "Direct indexed-client source", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Enter virtual M2/root-WMO paths from a complete Client workshop index. Crucible resolves the effective Wrath MPQ layer, extracts the complete recursive dependency closure into a hash-bound source snapshot, then builds the same collision-safe GameObject plan. No manual archive extraction is required.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") },
                new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _bulkIndex, WithColumn(browseIndex, 1) } },
                _bulkVirtualSources,
                _bulkArchiveChoices,
                indexedPlan
            } } },
            _bulkStatus,
            new TextBlock { Text = "Review", FontWeight = FontWeight.SemiBold },
            _bulkPreview
        } };
        return new ScrollViewer { Content = fields };
    }

    private async Task PickBulkIndexAsync()
    {
        var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select a complete Crucible client-index folder", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return; _bulkIndex.Text = Path.GetFullPath(path);
    }

    private async Task AddBulkFilesAsync()
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add extracted WotLK gameobject models", AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("WotLK models") { Patterns = ["*.m2", "*.wmo"] }] });
        AppendBulkSources(files.Select(file => file.TryGetLocalPath()).Where(path => path is not null)!);
    }

    private async Task AddBulkFolderAsync()
    {
        var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add a folder containing M2 / WMO assets", AllowMultiple = false }); var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) AppendBulkSources([path]);
    }

    private void AppendBulkSources(IEnumerable<string?> paths)
    {
        var values = ParseBulkSources().Concat(paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!))).Distinct(StringComparer.OrdinalIgnoreCase); _bulkSources.Text = string.Join(Environment.NewLine, values); _bulkPlan = null; _bulkApply.IsEnabled = false;
    }

    private async Task PlanBulkAsync(Button button)
    {
        _bulkOperation?.Cancel(); _bulkOperation?.Dispose(); var operation = _bulkOperation = new CancellationTokenSource();
        try
        {
            button.IsEnabled = false; _bulkApply.IsEnabled = false; _bulkStatus.Text = "Scanning model geometry, DBC identities, and complete client dependencies…";
            var dbc = Path.Combine(_session.Settings.CoreDbcPath, "GameObjectDisplayInfo.dbc"); var schema = _session.Settings.SchemaDefinitionPath; var sources = ParseBulkSources();
            var library = Directory.Exists(_bulkLibrary.Text) ? Path.GetFullPath(_bulkLibrary.Text!) : null; var clientRoot = Directory.Exists(_bulkClientRoot.Text) ? Path.GetFullPath(_bulkClientRoot.Text!) : null;
            IReadOnlyList<uint>? occupied = null;
            if (_session.DatabaseTested && _session.DatabaseProfile is not null && _session.DatabaseCapabilities is not null)
            {
                var occupancy = await new ContentIdOccupancyService().InspectAsync(ContentIdDomain.GameObject, _session.DatabaseProfile, _session.DatabaseCapabilities, null, null, cancellationToken: operation.Token);
                if (!occupancy.Complete) throw new InvalidOperationException($"Live gameobject ID scan is incomplete: {string.Join(" ", occupancy.Warnings)}"); occupied = occupancy.OccupiedIds;
            }
            var capabilities = _session.DatabaseCapabilities; var displayStart = (uint)(_bulkDisplayStart.Value ?? 100000); var templateStart = (uint)(_bulkTemplateStart.Value ?? 100000);
            _bulkPlan = await Task.Run(() => GameObjectBulkGeneratorService.CreatePlan(dbc, schema, sources, displayStart, templateStart, library, clientRoot, capabilities, occupied, operation.Token), operation.Token);
            operation.Token.ThrowIfCancellationRequested(); _bulkPreview.Text = BulkPlanText(_bulkPlan); _bulkApply.IsEnabled = _bulkPlan.Ready;
            _bulkStatus.Text = _bulkPlan.Ready ? $"Ready · {_bulkPlan.Rows.Count:N0} templates · {_bulkPlan.AddedDisplays:N0} new displays · {_bulkPlan.Assets.Count:N0} dependency files." : $"Blocked · {_bulkPlan.Blockers.Count:N0} issue(s) must be resolved before any output is written.";
            if (library is not null) { _session.Settings.ProcessedAssetLibraryPath = library; _session.Settings.Save(); }
        }
        catch (OperationCanceledException) { _bulkStatus.Text = "Bulk planning cancelled."; }
        catch (Exception exception) { _bulkPlan = null; _bulkPreview.Text = exception.ToString(); _bulkStatus.Text = $"Bulk planning failed: {exception.Message}"; DesktopCrashLogger.Log("Bulk gameobject planning failed", exception); }
        finally { button.IsEnabled = true; }
    }

    private async Task PlanIndexedBulkAsync(Button button)
    {
        var index = _bulkIndex.Text?.Trim(); var virtualSources = ParseLines(_bulkVirtualSources.Text);
        if (string.IsNullOrWhiteSpace(index) || !Directory.Exists(index)) { _bulkStatus.Text = "Choose a complete client-index folder first."; return; }
        if (virtualSources.Count == 0) { _bulkStatus.Text = "Enter at least one virtual M2 or root-WMO client path."; return; }
        var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a new or empty indexed-source workspace", AllowMultiple = false });
        var workspace = folders.FirstOrDefault()?.TryGetLocalPath(); if (workspace is null) return;
        _bulkOperation?.Cancel(); _bulkOperation?.Dispose(); var operation = _bulkOperation = new CancellationTokenSource();
        try
        {
            button.IsEnabled = false; _bulkApply.IsEnabled = false; _bulkStatus.Text = "Resolving effective MPQ layers and extracting a verified dependency snapshot…";
            var dbc = Path.Combine(_session.Settings.CoreDbcPath, "GameObjectDisplayInfo.dbc"); var schema = _session.Settings.SchemaDefinitionPath;
            IReadOnlyList<uint>? occupied = null;
            if (_session.DatabaseTested && _session.DatabaseProfile is not null && _session.DatabaseCapabilities is not null)
            {
                var occupancy = await new ContentIdOccupancyService().InspectAsync(ContentIdDomain.GameObject, _session.DatabaseProfile, _session.DatabaseCapabilities, null, null, cancellationToken: operation.Token);
                if (!occupancy.Complete) throw new InvalidOperationException($"Live gameobject ID scan is incomplete: {string.Join(" ", occupancy.Warnings)}"); occupied = occupancy.OccupiedIds;
            }
            var choices = ParseArchiveChoices(_bulkArchiveChoices.Text); var displayStart = (uint)(_bulkDisplayStart.Value ?? 100000); var templateStart = (uint)(_bulkTemplateStart.Value ?? 100000);
            var result = await Task.Run(() => ClientIndexedAssetSnapshotService.CreateGameObjectPlan(index, workspace, virtualSources, dbc, schema,
                displayStart, templateStart, _session.DatabaseCapabilities, occupied, choices, operation.Token), operation.Token);
            operation.Token.ThrowIfCancellationRequested(); _bulkPlan = result.Plan; _bulkPreview.Text = $"INDEXED SOURCE SNAPSHOT\n{result.SnapshotPath}\nFingerprint: {result.Snapshot.IndexFingerprint}\nResolved files: {result.Snapshot.Files.Count(file => file.SourceRelativePath is not null):N0}\nExternal bindings: {result.Snapshot.Files.Count(file => file.State == ClientIndexedAssetSnapshotState.ExternalBinding):N0}\n\n{BulkPlanText(result.Plan)}";
            _bulkApply.IsEnabled = result.Plan.Ready; _bulkStatus.Text = $"Indexed plan ready · {result.Plan.Rows.Count:N0} templates · {result.Plan.Assets.Count:N0} exact dependency files · snapshot {result.SnapshotPath}.";
            _session.Settings.ClientIndexPath = Path.GetFullPath(index); _session.Settings.Save();
        }
        catch (OperationCanceledException) { _bulkStatus.Text = "Indexed GameObject planning cancelled."; }
        catch (Exception exception) { _bulkPlan = null; _bulkPreview.Text = exception.ToString(); _bulkStatus.Text = $"Indexed planning stopped safely: {exception.Message}"; DesktopCrashLogger.Log("Indexed gameobject planning failed", exception); }
        finally { button.IsEnabled = true; }
    }

    private async Task ApplyBulkAsync()
    {
        if (_bulkPlan?.Ready != true) { _bulkStatus.Text = "Build a blocker-free plan first."; return; }
        var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a new or empty output folder", AllowMultiple = false }); var output = folders.FirstOrDefault()?.TryGetLocalPath(); if (output is null) return;
        try
        {
            _bulkApply.IsEnabled = false; _bulkStatus.Text = "Verifying every source hash and building the DBC, SQL, manifest, and tiny MPQ…"; var plan = _bulkPlan;
            var result = await Task.Run(() => GameObjectBulkGeneratorService.Apply(plan, output)); _bulkStatus.Text = $"Bundle complete · {result.PatchPath} · {result.AddedDisplays:N0} display rows · {result.Templates:N0} templates · {result.PatchEntries:N0} MPQ entries.";
        }
        catch (Exception exception) { _bulkStatus.Text = $"Bulk output failed: {exception.Message}"; DesktopCrashLogger.Log("Bulk gameobject output failed", exception); }
        finally { _bulkApply.IsEnabled = _bulkPlan?.Ready == true; }
    }

    private IReadOnlyList<string> ParseBulkSources() => (_bulkSources.Text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static IReadOnlyList<string> ParseLines(string? text) => (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static IReadOnlyDictionary<string, string> ParseArchiveChoices(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseLines(text))
        {
            var separator = line.IndexOf('|'); if (separator <= 0 || separator == line.Length - 1) throw new InvalidDataException($"Invalid archive choice '{line}'. Use client path | Data\\archive.MPQ.");
            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return result;
    }
    private static string BulkPlanText(GameObjectBulkPlan plan)
    {
        var builder = new System.Text.StringBuilder(); builder.AppendLine($"Ready: {plan.Ready} · models {plan.Rows.Count:N0} · add displays {plan.AddedDisplays:N0} · reuse {plan.Rows.Count - plan.AddedDisplays:N0} · assets {plan.Assets.Count:N0}");
        foreach (var blocker in plan.Blockers) builder.AppendLine($"BLOCKER · {blocker}"); foreach (var finding in plan.Findings) builder.AppendLine($"FINDING · {finding}");
        foreach (var row in plan.Rows) builder.AppendLine($"{row.TemplateId} → display {row.DisplayId} [{(row.ReusesDisplay ? "reuse" : "add")}] · {row.ClientPath} · {row.DependencyPaths.Count:N0} files");
        builder.AppendLine(); builder.Append(plan.Sql); return builder.ToString();
    }

    private void AddLootRow()
    {
        var definition = SelectedType(); var suggested = definition.Id is 3 or 25 ? (uint)(_data[1].Value ?? 0) : 0; if (suggested == 0) suggested = (uint)(_entry.Value ?? 0);
        var row = new LootRowEditor(suggested, RefreshPreview, (field, currentId, selected) => ReferenceLookupRequested?.Invoke(this, new ReferencePickerRequest(ReferenceDomain.Item, field, currentId, selected))); row.RemoveRequested += (_, _) => { _lootRows.Children.Remove(row); RefreshPreview(); }; _lootRows.Children.Add(row); RefreshPreview();
    }

    private void RequestReference(ReferenceDomain domain, string field, TextBox destination)
    {
        ReferenceLookupRequested?.Invoke(this, new ReferencePickerRequest(domain, field, 0, id =>
        {
            var ids = ParseIds(destination.Text).Append(id).Distinct();
            destination.Text = string.Join(", ", ids);
        }));
    }

    private void HookEvents()
    {
        foreach (var number in AllNumbers()) number.ValueChanged += (_, _) => RefreshPreview();
        foreach (var text in new[] { _name, _iconName, _castCaption, _unknownText, _aiName, _scriptName, _spawnScript, _comment, _startsQuests, _endsQuests }) text.TextChanged += (_, _) => RefreshPreview();
        _type.SelectionChanged += (_, _) => { RefreshDataLabels(); RefreshPreview(); }; _includeSpawn.IsCheckedChanged += (_, _) => RefreshPreview();
    }

    private void RefreshDataLabels()
    {
        var type = SelectedType(); for (var index = 0; index < 24; index++) { var field = type.Field(index); _dataLabels[index].Text = field.Name.StartsWith("Data", StringComparison.Ordinal) ? $"Data{index} · raw / unused" : $"Data{index} · {field.Name}"; ToolTip.SetTip(_dataLabels[index], field.Meaning); }
    }

    private GameObjectTemplateDraft Draft() => new((uint)(_entry.Value ?? 0), SelectedType().Id, (uint)(_displayId.Value ?? 0), _name.Text ?? string.Empty, _iconName.Text ?? string.Empty, _castCaption.Text ?? string.Empty, _unknownText.Text ?? string.Empty, (float)(_size.Value ?? 1), _data.Select(value => (long)(value.Value ?? 0)).ToArray(), _aiName.Text ?? string.Empty, _scriptName.Text ?? string.Empty,
        _includeSpawn.IsChecked == true ? new((uint)(_guid.Value ?? 0), (ushort)(_map.Value ?? 0), (ushort)(_zone.Value ?? 0), (ushort)(_area.Value ?? 0), (byte)(_spawnMask.Value ?? 1), (uint)(_phaseMask.Value ?? 1), (float)(_x.Value ?? 0), (float)(_y.Value ?? 0), (float)(_z.Value ?? 0), (float)(_orientation.Value ?? 0), (float)(_rotation0.Value ?? 0), (float)(_rotation1.Value ?? 0), (float)(_rotation2.Value ?? 0), (float)(_rotation3.Value ?? 1), (int)(_respawn.Value ?? 0), (byte)(_animProgress.Value ?? 0), (byte)(_state.Value ?? 0), _spawnScript.Text ?? string.Empty, _comment.Text ?? string.Empty) : null,
        _lootRows.Children.OfType<LootRowEditor>().Select(row => row.Draft()).ToArray(), ParseIds(_startsQuests.Text), ParseIds(_endsQuests.Text));

    private WorldContentWritePlan Plan() => GameObjectTemplateAdapter.CreatePlan(Draft(), _session.DatabaseCapabilities ?? GameObjectTemplateAdapter.CreatePortableCapabilities());

    private void RefreshPreview()
    {
        _confirmation.IsVisible = false; _pendingPlan = null;
        try
        {
            var draft = Draft(); var type = SelectedType(); var used = type.Fields.Where(field => field.Index < draft.Data.Count && draft.Data[field.Index] != 0).Select(field => $"Data{field.Index} {field.Name}={draft.Data[field.Index]}").ToArray();
            _summary.Text = $"{draft.Name}\nEntry {draft.Entry} · {type.Display}\nDisplay {draft.DisplayId} · size {draft.Size:0.###}\n{(used.Length == 0 ? "No nonzero type-specific fields" : string.Join("\n", used))}\nSpawn: {(draft.Spawn is null ? "not staged" : $"GUID {draft.Spawn.Guid} · map {draft.Spawn.Map} · {draft.Spawn.X:0.###}, {draft.Spawn.Y:0.###}, {draft.Spawn.Z:0.###}")}\nLoot rows: {draft.Loot?.Count ?? 0:N0} · starts quests: {draft.StartsQuests?.Count ?? 0:N0} · ends quests: {draft.EndsQuests?.Count ?? 0:N0}\nAI {(string.IsNullOrWhiteSpace(draft.AiName) ? "core default" : draft.AiName)} · Script {(string.IsNullOrWhiteSpace(draft.ScriptName) ? "none" : draft.ScriptName)}";
            var plan = Plan(); _sql.Text = plan.PreviewSql() + (plan.OmittedFields.Count == 0 ? string.Empty : $"\n\n-- Not present in target schema:\n-- {string.Join("\n-- ", plan.OmittedFields)}");
        }
        catch (Exception exception) { _summary.Text = $"Draft is currently incomplete: {exception.Message}"; _sql.Text = $"-- Change plan is currently invalid: {exception.Message}"; }
    }

    private void PrepareCommit()
    {
        try
        {
            if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _status.Text = "Connect and verify Server & SQL before deploying. SQL export remains available offline."; return; }
            _pendingPlan = Plan(); if (_loadedEntry is { } loaded && (uint)(_entry.Value ?? 0) != loaded) throw new InvalidOperationException("The decoded editor will not silently change a primary gameobject ID. Clone it explicitly instead."); var editing = _loadedEntry is not null;
            var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => { _pendingPlan = null; _confirmation.IsVisible = false; }; var confirm = AccentButton(editing ? $"Update gameobject {_loadedEntry}" : $"Insert gameobject {(uint)(_entry.Value ?? 0)}"); confirm.Click += async (_, _) => await CommitAsync(confirm);
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = editing ? $"Update the decoded template and insert {_pendingPlan.Rows.Count - 1:N0} newly staged spawn/loot/quest row(s) in one transaction? Existing child identities are refused." : $"Insert this template and {_pendingPlan.Rows.Count - 1:N0} related row(s) in one transaction? No existing identity is replaced.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { _status.Text = $"Cannot deploy: {exception.Message}"; }
    }

    private async Task CommitAsync(Button button)
    {
        if (_pendingPlan is null || _session.DatabaseProfile is null) return;
        try { button.IsEnabled = false; var service = new WorldContentTemplateService(); if (_loadedEntry is null) await service.InsertAsync(_session.DatabaseProfile, _pendingPlan); else await service.UpdateFirstAndInsertChildrenAsync(_session.DatabaseProfile, _pendingPlan); _status.Text = _loadedEntry is null ? "Gameobject template and related rows inserted transactionally." : "Gameobject template updated and newly staged child rows inserted transactionally; custom columns were preserved."; _pendingPlan = null; _confirmation.IsVisible = false; }
        catch (Exception exception) { _status.Text = $"Gameobject deployment failed: {exception.Message}"; DesktopCrashLogger.Log("Gameobject deployment failed", exception); } finally { button.IsEnabled = true; }
    }

    private async Task ExportAsync()
    {
        try { var plan = Plan(); var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export gameobject SQL", SuggestedFileName = $"gameobject-{(uint)(_entry.Value ?? 0)}.sql", FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) await File.WriteAllTextAsync(path, plan.PreviewSql() + Environment.NewLine); }
        catch (Exception exception) { _status.Text = $"Export failed: {exception.Message}"; }
    }

    private async Task ExportDraftAsync()
    {
        try { var draft = Draft(); var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export portable gameobject draft", SuggestedFileName = $"gameobject-{draft.Entry}.crucible.json", FileTypeChoices = [new FilePickerFileType("Crucible JSON") { Patterns = ["*.crucible.json", "*.json"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine); }
        catch (Exception exception) { _status.Text = $"Draft export failed: {exception.Message}"; }
    }

    private async Task LoadModelAsync()
    {
        try
        {
            var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose an extracted WotLK gameobject M2 or root WMO", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("WotLK models") { Patterns = ["*.m2", "*.wmo"] }] }); var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return; _modelOperation?.Cancel(); _modelOperation?.Dispose(); _modelOperation = new CancellationTokenSource(); var token = _modelOperation.Token; _modelStatus.Text = "Loading model geometry…";
            if (Path.GetExtension(path).Equals(".wmo", StringComparison.OrdinalIgnoreCase))
            {
                var loaded = await Task.Run(() => { var geometry = WmoPreviewGeometryService.Load(path, cancellationToken: token); var textures = WmoPreviewGeometryService.LoadTextures(geometry, cancellationToken: token); return (geometry, textures); }, token); token.ThrowIfCancellationRequested();
                _model.ClearGeometry(); _model.IsVisible = false; _wmo.IsVisible = true; _wmo.SetGeometry(loaded.geometry); _wmo.SetDecodedTextures(loaded.textures.Textures); _attachmentPicker.ItemsSource = null; _showAttachments.IsChecked = false; _showAttachments.IsEnabled = false; _attachmentPicker.IsEnabled = false;
                _modelStatus.Text = $"{Path.GetFileName(loaded.geometry.RootPath)} · {loaded.geometry.Groups.Count:N0} groups · {loaded.geometry.TriangleIndices.Count / 3:N0} triangles · {loaded.textures.Textures.Count:N0}/{loaded.geometry.Materials.Count:N0} textures";
            }
            else
            {
                var geometry = await Task.Run(() => M2PreviewGeometryService.Load(path)); token.ThrowIfCancellationRequested(); _wmo.ClearGeometry(); _wmo.IsVisible = false; _model.IsVisible = true; _model.SetGeometry(geometry); _showAttachments.IsEnabled = true; RefreshAttachmentPoints(geometry); _modelStatus.Text = $"{Path.GetFileName(path)} · {geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} visible geosets · {geometry.TriangleIndices.Count / 3:N0} triangles · {geometry.Attachments.Count:N0} attachment points";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _modelStatus.Text = $"Model preview failed: {exception.Message}"; }
    }

    private void RefreshAttachmentPoints(M2PreviewGeometry geometry)
    {
        _attachmentPicker.ItemsSource = geometry.Attachments; _attachmentPicker.IsEnabled = geometry.Attachments.Count > 0;
        _attachmentPicker.SelectedItem = geometry.Attachments.FirstOrDefault(); ApplyAttachmentOverlay();
    }

    private void ApplyAttachmentOverlay() => _model.SetAttachmentOverlay(_showAttachments.IsChecked == true, (_attachmentPicker.SelectedItem as M2PreviewAttachment)?.Index);

    private void SessionChanged(object? sender, EventArgs e) { RefreshSchemaStatus(); RefreshPreview(); }
    private void RefreshSchemaStatus() { var capabilities = _session.DatabaseCapabilities; _status.Text = capabilities?.FindTable("gameobject_template") is { } table ? $"Live schema ready · {capabilities.Database}.gameobject_template · {table.Columns.Count:N0} columns" : "Offline current-core gameobject schema ready · connect Server & SQL for live deployment."; }
    public void Dispose() { _session.Changed -= SessionChanged; _modelOperation?.Cancel(); _modelOperation?.Dispose(); _bulkOperation?.Cancel(); _bulkOperation?.Dispose(); _model.Dispose(); _wmo.Dispose(); }
    private GameObjectTypeDefinition SelectedType() => _type.SelectedItem as GameObjectTypeDefinition ?? GameObjectTypeCatalog.Find(0);
    private void SetType(int id) => _type.SelectedItem = GameObjectTypeCatalog.All.FirstOrDefault(type => type.Id == id) ?? GameObjectTypeCatalog.All[0];
    private IEnumerable<NumericUpDown> AllNumbers() => new[] { _entry, _displayId, _size, _guid, _map, _zone, _area, _spawnMask, _phaseMask, _x, _y, _z, _orientation, _rotation0, _rotation1, _rotation2, _rotation3, _respawn, _animProgress, _state }.Concat(_data);
    private static IReadOnlyList<uint> ParseIds(string? text) { if (string.IsNullOrWhiteSpace(text)) return []; var result = new List<uint>(); foreach (var token in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { if (!uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0) throw new InvalidDataException($"Invalid quest ID '{token}'."); result.Add(id); } return result.Distinct().ToArray(); }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Gameobject workspace is not attached to the main window.");
    private static Grid Form(params (string Label, Control Input)[] rows) { var grid = new Grid { ColumnDefinitions = new("Auto,*"), RowSpacing = 7, ColumnSpacing = 10, Margin = new Thickness(12) }; for (var row = 0; row < rows.Length; row++) { grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); var label = new TextBlock { Text = rows[row].Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }; Grid.SetRow(label, row); grid.Children.Add(label); Grid.SetRow(rows[row].Input, row); Grid.SetColumn(rows[row].Input, 1); grid.Children.Add(rows[row].Input); } return grid; }
    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value = 0) => new() { Minimum = minimum, Maximum = maximum, Value = value, Increment = 1 };
    private static object? Value(IReadOnlyDictionary<string, object?> row, string name) => row.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static decimal Decimal(IReadOnlyDictionary<string, object?> row, string name, decimal fallback = 0) { try { return Convert.ToDecimal(Value(row, name) ?? fallback, CultureInfo.InvariantCulture); } catch { return fallback; } }
    private static int Int(IReadOnlyDictionary<string, object?> row, string name) { try { return Convert.ToInt32(Value(row, name) ?? 0, CultureInfo.InvariantCulture); } catch { return 0; } }
    private static string Text(IReadOnlyDictionary<string, object?> row, string name) => Convert.ToString(Value(row, name), CultureInfo.InvariantCulture) ?? string.Empty;
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; } private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }

    private sealed class LootRowEditor : UserControl
    {
        private readonly NumericUpDown _entry; private readonly NumericUpDown _item = Number(0, uint.MaxValue); private readonly NumericUpDown _reference = Number(0, int.MaxValue); private readonly NumericUpDown _chance = Number(0, 100, 100); private readonly CheckBox _quest = new() { Content = "Quest required" }; private readonly NumericUpDown _mode = Number(1, ushort.MaxValue, 1); private readonly NumericUpDown _group = Number(0, byte.MaxValue); private readonly NumericUpDown _minimum = Number(1, byte.MaxValue, 1); private readonly NumericUpDown _maximum = Number(1, byte.MaxValue, 1); private readonly TextBox _comment = new();
        public event EventHandler? RemoveRequested;
        public LootRowEditor(uint entry, Action changed, Action<string, uint, Action<uint>> lookup) { _entry = Number(1, uint.MaxValue, entry); foreach (var number in new[] { _entry, _item, _reference, _chance, _mode, _group, _minimum, _maximum }) number.ValueChanged += (_, _) => changed(); _quest.IsCheckedChanged += (_, _) => changed(); _comment.TextChanged += (_, _) => changed(); var findItem = new Button { Content = "Find…" }; findItem.Click += (_, _) => lookup("Loot item", (uint)(_item.Value ?? 0), selected => _item.Value = selected); var remove = new Button { Content = "Remove" }; remove.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty); Content = new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = new WrapPanel { Children = { Labeled("Loot entry", _entry), FieldWithButton("Item ID", _item, findItem), Labeled("Reference", _reference), Labeled("Chance %", _chance), Labeled("Loot mode", _mode), Labeled("Group", _group), Labeled("Minimum", _minimum), Labeled("Maximum", _maximum), Labeled("Comment", _comment), new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { _quest, remove } } } } }; }
        public GameObjectLootDraft Draft() => new((uint)(_entry.Value ?? 0), (uint)(_item.Value ?? 0), (int)(_reference.Value ?? 0), (float)(_chance.Value ?? 0), _quest.IsChecked == true, (ushort)(_mode.Value ?? 1), (byte)(_group.Value ?? 0), (byte)(_minimum.Value ?? 1), (byte)(_maximum.Value ?? 1), _comment.Text ?? string.Empty);
        private static Control Labeled(string label, Control control) => new StackPanel { Children = { new TextBlock { Text = label, Foreground = Brush.Parse("#9AA5B7") }, control } };
        private static Control FieldWithButton(string label, Control control, Button button) => new StackPanel { Children = { new TextBlock { Text = label, Foreground = Brush.Parse("#9AA5B7") }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { control, button } } } };
    }
}
