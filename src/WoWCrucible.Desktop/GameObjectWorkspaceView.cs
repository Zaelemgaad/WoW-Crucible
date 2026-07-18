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
    private readonly M2PreviewView _model = new(); private readonly TextBlock _modelStatus = Status("Load an extracted WotLK M2/SKIN when this display uses an M2. WMO geometry support is a separate renderer stage.");
    private readonly CheckBox _showAttachments = new() { Content = "Show attachment points" }; private readonly ComboBox _attachmentPicker = new() { PlaceholderText = "No attachment points loaded" };
    private readonly TextBlock _status = Status("Offline current-core gameobject schema ready."); private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly Button _commit = AccentButton("Insert into connected world database"); private WorldContentWritePlan? _pendingPlan; private uint? _loadedEntry;

    public event EventHandler? BackRequested;
    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public GameObjectWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged; _type.SelectedIndex = 3; HookEvents(); RefreshDataLabels();
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "GAMEOBJECTS", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, 1) } };
        var editor = new TabControl { Items = { new TabItem { Header = "Template", Content = new ScrollViewer { Content = TemplateForm() } }, new TabItem { Header = "Type-aware Data0–23", Content = DataPage() }, new TabItem { Header = "World spawn", Content = new ScrollViewer { Content = SpawnForm() } }, new TabItem { Header = "Loot & quests", Content = LootQuestPage() } } };
        var loadModel = new Button { Content = "Load extracted M2…" }; loadModel.Click += async (_, _) => await LoadModelAsync(); var clearModel = new Button { Content = "Clear" }; clearModel.Click += (_, _) => { _model.ClearGeometry(); _attachmentPicker.ItemsSource = null; _model.SetAttachmentOverlay(false); _modelStatus.Text = "Model preview cleared."; };
        _showAttachments.Click += (_, _) => ApplyAttachmentOverlay(); _attachmentPicker.SelectionChanged += (_, _) => ApplyAttachmentOverlay();
        var modelPage = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { new WrapPanel { Children = { loadModel, clearModel, _showAttachments, _attachmentPicker } }, WithRow(new Border { Background = Brush.Parse("#090D14"), Child = _model }, 1), WithRow(_modelStatus, 2) } };
        var preview = new TabControl { Items = { new TabItem { Header = "Decoded summary", Content = new ScrollViewer { Content = new Border { Padding = new Thickness(16), BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _summary } } }, new TabItem { Header = "3D model", Content = modelPage }, new TabItem { Header = "SQL change plan", Content = _sql } } };
        var workspace = new Grid { ColumnDefinitions = new("3*,Auto,2*"), Children = { editor, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(preview, 2) } };
        var export = new Button { Content = "Export SQL…" }; export.Click += async (_, _) => await ExportAsync(); var exportDraft = new Button { Content = "Export portable draft…" }; exportDraft.Click += async (_, _) => await ExportDraftAsync(); _commit.Click += (_, _) => PrepareCommit();
        Content = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(workspace, 1), WithRow(new WrapPanel { Children = { export, exportDraft, _commit, _status } }, 2), WithRow(_confirmation, 3) } };
        RefreshPreview(); RefreshSchemaStatus();
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
        try { var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose an extracted WotLK gameobject M2", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("WotLK M2") { Patterns = ["*.m2"] }] }); var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return; _modelStatus.Text = "Loading model…"; var geometry = await Task.Run(() => M2PreviewGeometryService.Load(path)); _model.SetGeometry(geometry); RefreshAttachmentPoints(geometry); _modelStatus.Text = $"{Path.GetFileName(path)} · {geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} visible geosets · {geometry.TriangleIndices.Count / 3:N0} triangles · {geometry.Attachments.Count:N0} attachment points"; }
        catch (Exception exception) { _modelStatus.Text = $"M2 preview failed: {exception.Message}"; }
    }

    private void RefreshAttachmentPoints(M2PreviewGeometry geometry)
    {
        _attachmentPicker.ItemsSource = geometry.Attachments; _attachmentPicker.IsEnabled = geometry.Attachments.Count > 0;
        _attachmentPicker.SelectedItem = geometry.Attachments.FirstOrDefault(); ApplyAttachmentOverlay();
    }

    private void ApplyAttachmentOverlay() => _model.SetAttachmentOverlay(_showAttachments.IsChecked == true, (_attachmentPicker.SelectedItem as M2PreviewAttachment)?.Index);

    private void SessionChanged(object? sender, EventArgs e) { RefreshSchemaStatus(); RefreshPreview(); }
    private void RefreshSchemaStatus() { var capabilities = _session.DatabaseCapabilities; _status.Text = capabilities?.FindTable("gameobject_template") is { } table ? $"Live schema ready · {capabilities.Database}.gameobject_template · {table.Columns.Count:N0} columns" : "Offline current-core gameobject schema ready · connect Server & SQL for live deployment."; }
    public void Dispose() => _session.Changed -= SessionChanged;
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
