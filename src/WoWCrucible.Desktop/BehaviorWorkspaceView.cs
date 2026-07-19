using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class BehaviorWorkspaceView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly ComboBox _domain = new() { ItemsSource = BehaviorDomainCatalog.All };
    private readonly TabControl _fieldTabs = new();
    private readonly Dictionary<string, FieldEditor> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _summary = Status("Behavior draft summary");
    private readonly TextBox _sql = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBlock _status = Status("Offline behavior schemas ready.");
    private readonly Button _commit = AccentButton("Insert into connected world database");
    private readonly Button _petCurve = new() { Content = "Bulk level curve…", IsVisible = false };
    private readonly Button _petGraph = new() { Content = "Talent & ability graph…", IsVisible = false };
    private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private IReadOnlyDictionary<string, object?>? _initialValues;
    private IReadOnlyDictionary<string, object?>? _loadedKey;
    private WorldContentWritePlan? _pendingPlan;
    private bool _syncing;
    public event EventHandler? BackRequested;
    public event EventHandler? PetCurveRequested;
    public event EventHandler? PetAbilityGraphRequested;
    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public BehaviorWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged; _domain.SelectedItem = BehaviorDomainCatalog.All[0]; _domain.SelectionChanged += (_, _) => { if (_syncing) return; ResetNew(); };
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty); var create = new Button { Content = "New row" }; create.Click += (_, _) => ResetNew(); _petCurve.Click += (_, _) => PetCurveRequested?.Invoke(this, EventArgs.Empty); _petGraph.Click += (_, _) => PetAbilityGraphRequested?.Invoke(this, EventArgs.Empty);
        var heading = new WrapPanel { Margin = new Thickness(12, 8), Children = { back, new TextBlock { Text = "WORLD DATA EDITORS", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _domain, _petCurve, _petGraph, create } };
        var preview = new TabControl { Items = { new TabItem { Header = "Decoded summary", Content = new ScrollViewer { Content = new Border { Padding = new Thickness(16), BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _summary } } }, new TabItem { Header = "SQL change plan", Content = _sql } } };
        var workspace = new Grid { ColumnDefinitions = new("3*,Auto,2*"), Children = { _fieldTabs, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(preview, 2) } };
        var import = new Button { Content = "Open portable draft…" }; import.Click += async (_, _) => await ImportDraftAsync(); var exportDraft = new Button { Content = "Export portable draft…" }; exportDraft.Click += async (_, _) => await ExportDraftAsync(); var exportSql = new Button { Content = "Export SQL…" }; exportSql.Click += async (_, _) => await ExportSqlAsync(); _commit.Click += (_, _) => PrepareCommit();
        Content = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(workspace, 1), WithRow(new WrapPanel { Children = { import, exportDraft, exportSql, _commit, _status } }, 2), WithRow(_confirmation, 3) } };
        RebuildFields(); RefreshSchemaStatus();
    }

    public void OpenRow(string tableName, IReadOnlyDictionary<string, object?> row)
    {
        var domain = BehaviorDomainCatalog.Find(tableName); _syncing = true; _domain.SelectedItem = domain; _syncing = false; _initialValues = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase); var table = Table(); _loadedKey = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).ToDictionary(column => column.Name, column => Value(row, column.Name), StringComparer.OrdinalIgnoreCase); _commit.Content = "Apply complete row to connected database"; RebuildFields(); _status.Text = $"Loaded {table.Name} row · primary identity is locked · every live column remains editable.";
    }

    public void SelectDomain(string idOrTable)
    {
        var domain = BehaviorDomainCatalog.Find(idOrTable); _syncing = true; _domain.SelectedItem = domain; _syncing = false; ResetNew();
    }

    private void ResetNew() { _initialValues = null; _loadedKey = null; _commit.Content = "Insert into connected world database"; RebuildFields(); RefreshSchemaStatus(); }

    private void RebuildFields()
    {
        _fieldTabs.ItemsSource = null; _editors.Clear(); var table = Table(); var petDomain = table.Name.Equals("pet_levelstats", StringComparison.OrdinalIgnoreCase) || table.Name.Equals("spell_pet_auras", StringComparison.OrdinalIgnoreCase) || table.Name.StartsWith("pet_", StringComparison.OrdinalIgnoreCase); _petCurve.IsVisible = table.Name.Equals("pet_levelstats", StringComparison.OrdinalIgnoreCase); _petGraph.IsVisible = petDomain; var initial = new Dictionary<string, object?>(BehaviorAuthoringAdapter.Defaults(table), StringComparer.OrdinalIgnoreCase); if (_initialValues is not null) foreach (var pair in _initialValues) initial[pair.Key] = pair.Value; var tabs = new List<TabItem>();
        foreach (var group in table.Columns.GroupBy(column => BehaviorAuthoringAdapter.Group(table.Name, column.Name)))
        {
            var panel = new StackPanel { Spacing = 7, Margin = new Thickness(12) };
            foreach (var column in group)
            {
                var value = Value(initial, column.Name); var isText = IsText(column); var text = new TextBox { Text = CellText(value), AcceptsReturn = isText, TextWrapping = isText ? TextWrapping.Wrap : TextWrapping.NoWrap, IsReadOnly = column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) || column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) };
                var nullBox = new CheckBox { Content = "NULL", IsChecked = value is null, IsEnabled = column.Nullable && !text.IsReadOnly }; text.IsEnabled = value is not null; nullBox.IsCheckedChanged += (_, _) => { text.IsEnabled = nullBox.IsChecked != true; RefreshPreview(); };
                var choices = BehaviorSemanticCatalog.Choices(table.Name, column.Name); ComboBox? decoded = null; if (choices.Count > 0) { decoded = new ComboBox { ItemsSource = choices, SelectedItem = Choice(choices, value) }; var captured = decoded; captured.SelectionChanged += (_, _) => { if (_syncing || captured.SelectedItem is not SemanticValue choice) return; _syncing = true; text.Text = choice.Value.ToString(CultureInfo.InvariantCulture); _syncing = false; RefreshPreview(); }; }
                var description = BehaviorSemanticCatalog.Describe(table.Name, column.Name); var label = new TextBlock { Text = $"{column.Name} · {column.ColumnType}{(column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) ? " · PRIMARY KEY" : string.Empty)}{(description.Length == 0 ? string.Empty : $"\n{description}")}", TextWrapping = TextWrapping.Wrap, Foreground = description.Length == 0 ? null : Brush.Parse("#B7C2D5") };
                var decodedControl = (Control?)decoded ?? new TextBlock { Text = "raw", Foreground = Brush.Parse("#667085"), VerticalAlignment = VerticalAlignment.Center };
                var decodedAndLookup = new WrapPanel { Children = { decodedControl } }; var reference = ReferenceFor(table.Name, column.Name);
                if (reference is not null)
                {
                    var find = new Button { Content = "Find name…" }; find.Click += (_, _) => ReferenceLookupRequested?.Invoke(this, new(reference.Value, $"{table.Name}.{column.Name}", ParseId(text.Text), selected => text.Text = selected.ToString(CultureInfo.InvariantCulture))); decodedAndLookup.Children.Add(find);
                }
                panel.Children.Add(new Grid { ColumnDefinitions = new("2*,2*,3*,Auto"), ColumnSpacing = 9, Children = { label, WithColumn(decodedAndLookup, 1), WithColumn(text, 2), WithColumn(nullBox, 3) } }); var editor = new FieldEditor(column, text, nullBox, decoded, choices); _editors[column.Name] = editor;
                text.TextChanged += (_, _) => { if (!_syncing && editor.Decoded is not null) { _syncing = true; editor.Decoded.SelectedItem = Choice(editor.Choices, text.Text); _syncing = false; } RefreshPreview(); };
            }
            tabs.Add(new TabItem { Header = group.Key, Content = new ScrollViewer { Content = panel } });
        }
        _fieldTabs.ItemsSource = tabs; RefreshPreview();
    }

    private void RefreshPreview()
    {
        _confirmation.IsVisible = false; _pendingPlan = null;
        try
        {
            var domain = Domain(); var table = Table(); var values = Values(); var plan = BehaviorAuthoringAdapter.CreatePlan(domain, table, values); var key = plan.Rows[0].Key; var decoded = _editors.Values.Where(editor => editor.Decoded?.SelectedItem is SemanticValue).Select(editor => { var choice = (SemanticValue)editor.Decoded!.SelectedItem!; return $"{editor.Column.Name}: {choice.Name} [{choice.Value}]{(string.IsNullOrWhiteSpace(choice.Meaning) ? string.Empty : $" · {choice.Meaning}")}"; }).ToArray();
            _summary.Text = $"{domain.Display}\n{domain.Description}\nTable: {table.Name} · {table.Columns.Count:N0} complete column(s)\nIdentity: {string.Join(", ", key.Select(pair => $"{pair.Key}={CellText(pair.Value)}"))}\n{(decoded.Length == 0 ? "No enum-backed fields in this row; all raw fields remain available." : string.Join("\n", decoded))}\n\nA new identity inserts; a row opened from SQL Studio updates exactly that primary identity. No other row is replaced."; _sql.Text = plan.PreviewSql();
        }
        catch (Exception exception) { _summary.Text = $"Draft is currently incomplete: {exception.Message}"; _sql.Text = $"-- Change plan is currently invalid: {exception.Message}"; }
    }

    private void PrepareCommit()
    {
        try
        {
            var domain = Domain(); if (!_session.DatabaseTested || _session.DatabaseProfile is null || _session.DatabaseCapabilities?.FindTable(domain.TableName) is null) { _status.Text = $"Connect a world database containing {domain.TableName} before deployment. Draft and SQL export remain available offline."; return; }
            _pendingPlan = BehaviorAuthoringAdapter.CreatePlan(domain, Table(), Values()); if (_loadedKey is not null && !SameKey(_pendingPlan.Rows[0].Key, _loadedKey)) throw new InvalidOperationException("The guided editor will not silently change a primary identity. Create a new row or clone explicitly."); var editing = _loadedKey is not null; var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => { _pendingPlan = null; _confirmation.IsVisible = false; }; var confirm = AccentButton(editing ? "Update exact row" : "Insert new row"); confirm.Click += async (_, _) => await CommitAsync(confirm);
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = editing ? $"Update every mapped {domain.TableName} field on exactly this primary identity?" : $"Insert this new {domain.TableName} identity? Existing identities are refused.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { _status.Text = $"Cannot deploy: {exception.Message}"; }
    }

    private async Task CommitAsync(Button button)
    {
        if (_pendingPlan is null || _session.DatabaseProfile is null) return;
        try { button.IsEnabled = false; var service = new WorldContentTemplateService(); if (_loadedKey is null) await service.InsertAsync(_session.DatabaseProfile, _pendingPlan); else await service.UpdateFirstAndInsertChildrenAsync(_session.DatabaseProfile, _pendingPlan); _status.Text = _loadedKey is null ? "Row inserted transactionally." : "Exact row updated transactionally; custom live columns were preserved in the editor."; _confirmation.IsVisible = false; _pendingPlan = null; }
        catch (Exception exception) { _status.Text = $"Deployment failed: {exception.Message}"; DesktopCrashLogger.Log("Behavior deployment failed", exception); }
        finally { button.IsEnabled = true; }
    }

    private async Task ExportSqlAsync()
    {
        try { var domain = Domain(); var plan = BehaviorAuthoringAdapter.CreatePlan(domain, Table(), Values()); var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = $"Export {domain.Display} SQL", SuggestedFileName = $"{domain.Id}.sql", FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) await File.WriteAllTextAsync(path, plan.PreviewSql() + Environment.NewLine); }
        catch (Exception exception) { _status.Text = $"SQL export failed: {exception.Message}"; }
    }

    private async Task ExportDraftAsync()
    {
        try { var domain = Domain(); var draft = new BehaviorPortableDraft(domain.Id, Values().ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : CellText(pair.Value), StringComparer.OrdinalIgnoreCase)); var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = $"Export {domain.Display} draft", SuggestedFileName = $"{domain.Id}.crucible.json", FileTypeChoices = [new FilePickerFileType("Crucible JSON") { Patterns = ["*.crucible.json", "*.json"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine); }
        catch (Exception exception) { _status.Text = $"Draft export failed: {exception.Message}"; }
    }

    private async Task ImportDraftAsync()
    {
        try
        {
            var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open behavior draft", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Crucible JSON") { Patterns = ["*.crucible.json", "*.json"] }] }); var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return; var draft = JsonSerializer.Deserialize<BehaviorPortableDraft>(await File.ReadAllTextAsync(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Draft decoded to null."); var domain = BehaviorDomainCatalog.Find(draft.Domain); _syncing = true; _domain.SelectedItem = domain; _syncing = false; _initialValues = draft.Values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase); _loadedKey = null; _commit.Content = "Insert into connected world database"; RebuildFields(); RefreshSchemaStatus();
        }
        catch (Exception exception) { _status.Text = $"Draft import failed: {exception.Message}"; }
    }

    private IReadOnlyDictionary<string, object?> Values() { var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); foreach (var editor in _editors.Values) result[editor.Column.Name] = editor.Null.IsChecked == true ? null : ParseCell(editor.Column, editor.Text.Text); return result; }
    private void SessionChanged(object? sender, EventArgs e) { RebuildFields(); RefreshSchemaStatus(); }
    private void RefreshSchemaStatus() { var domain = Domain(); var live = _session.DatabaseCapabilities?.FindTable(domain.TableName); _status.Text = live is null ? $"Offline {domain.TableName} schema ready · connect Server & SQL for live deployment." : $"Live schema ready · {_session.DatabaseCapabilities!.Database}.{live.Name} · {live.Columns.Count:N0} columns"; }
    public void Dispose() => _session.Changed -= SessionChanged;

    private BehaviorDomainDefinition Domain() => _domain.SelectedItem as BehaviorDomainDefinition ?? BehaviorDomainCatalog.All[0];
    private DatabaseTableCapability Table() => BehaviorAuthoringAdapter.Table(Domain(), _session.DatabaseCapabilities);
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Behavior workspace is not attached to the main window.");
    private static SemanticValue? Choice(IReadOnlyList<SemanticValue> choices, object? value) { try { var number = Convert.ToInt64(value, CultureInfo.InvariantCulture); return choices.FirstOrDefault(choice => choice.Value == number); } catch { return null; } }
    private static bool SameKey(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right) => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && CellText(pair.Value).Equals(CellText(value), StringComparison.Ordinal));
    private static object? ParseCell(DatabaseColumnCapability column, string? text) { text ??= string.Empty; if (column.DataType.Contains("binary", StringComparison.OrdinalIgnoreCase) || column.DataType.Contains("blob", StringComparison.OrdinalIgnoreCase)) return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(text[2..]) : System.Text.Encoding.UTF8.GetBytes(text); return text; }
    private static uint ParseId(string? text) => uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static ReferenceDomain? ReferenceFor(string table, string column)
    {
        if (table.Equals("pet_levelstats", StringComparison.OrdinalIgnoreCase) && column.Equals("creature_entry", StringComparison.OrdinalIgnoreCase)) return ReferenceDomain.Creature;
        if ((table.Equals("pet_name_generation", StringComparison.OrdinalIgnoreCase) || table.Equals("pet_name_generation_locale", StringComparison.OrdinalIgnoreCase)) && column.Equals("entry", StringComparison.OrdinalIgnoreCase)) return ReferenceDomain.Creature;
        if (table.Equals("spell_pet_auras", StringComparison.OrdinalIgnoreCase))
        {
            if (column.Equals("spell", StringComparison.OrdinalIgnoreCase) || column.Equals("aura", StringComparison.OrdinalIgnoreCase)) return ReferenceDomain.Spell;
            if (column.Equals("pet", StringComparison.OrdinalIgnoreCase)) return ReferenceDomain.Creature;
        }
        if (table.Equals("trainer_spell", StringComparison.OrdinalIgnoreCase) && (column.Equals("SpellId", StringComparison.OrdinalIgnoreCase) || column.StartsWith("ReqAbility", StringComparison.OrdinalIgnoreCase))) return ReferenceDomain.Spell;
        if (table.Equals("npc_trainer", StringComparison.OrdinalIgnoreCase) && (column.Equals("SpellID", StringComparison.OrdinalIgnoreCase) || column.Equals("ReqSpell", StringComparison.OrdinalIgnoreCase))) return ReferenceDomain.Spell;
        if (table.Equals("creature_default_trainer", StringComparison.OrdinalIgnoreCase) && column.Equals("CreatureId", StringComparison.OrdinalIgnoreCase)) return ReferenceDomain.Creature;
        return null;
    }
    private static string CellText(object? value) => value switch { null => string.Empty, byte[] bytes => "0x" + Convert.ToHexString(bytes), DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), IFormattable format => format.ToString(null, CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };
    private static bool IsText(DatabaseColumnCapability column) => column.DataType.Contains("text", StringComparison.OrdinalIgnoreCase) || column.DataType.Contains("char", StringComparison.OrdinalIgnoreCase);
    private static object? Value(IReadOnlyDictionary<string, object?> row, string name) => row.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private sealed record FieldEditor(DatabaseColumnCapability Column, TextBox Text, CheckBox Null, ComboBox? Decoded, IReadOnlyList<SemanticValue> Choices);
}
