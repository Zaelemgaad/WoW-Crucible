using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class QuestWorkspaceView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session; private readonly TabControl _fieldTabs = new();
    private readonly Dictionary<string, (DatabaseColumnCapability Column, TextBox Text, CheckBox Null)> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox _questType = new() { ItemsSource = QuestSemanticCatalog.Types }; private readonly WrapPanel _flagPanel = new();
    private readonly Dictionary<uint, CheckBox> _flagBoxes = [];
    private readonly TextBox _creatureStarters = Ids("Creature template IDs that start this quest"); private readonly TextBox _creatureEnders = Ids("Creature template IDs that end this quest"); private readonly TextBox _gameObjectStarters = Ids("Gameobject template IDs that start this quest"); private readonly TextBox _gameObjectEnders = Ids("Gameobject template IDs that end this quest");
    private readonly TextBlock _summary = Status("Quest draft summary"); private readonly TextBox _sql = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBlock _status = Status("Offline portable quest schema ready."); private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly Button _commit = AccentButton("Insert into connected world database"); private WorldContentWritePlan? _pendingPlan; private uint? _loadedId; private IReadOnlyDictionary<string, object?>? _loadedValues; private bool _syncingSemantics;
    public event EventHandler? BackRequested;
    public event EventHandler? ProjectWorkspaceRequested;
    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public QuestWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged;
        foreach (var flag in QuestSemanticCatalog.Flags) { var box = new CheckBox { Content = flag.Name }; ToolTip.SetTip(box, $"0x{flag.Value:X8} · {flag.Meaning}"); box.IsCheckedChanged += (_, _) => FlagsChanged(); _flagBoxes[flag.Value] = box; _flagPanel.Children.Add(box); }
        _questType.SelectionChanged += (_, _) => TypeChanged(); foreach (var links in LinkInputs()) links.TextChanged += (_, _) => RefreshPreview();
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty); var create = new Button { Content = "New blank quest" }; create.Click += (_, _) => ResetNew();
        var heading = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "QUESTS", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, 1), WithColumn(create, 2) } };
        var editor = new TabControl { Items = { new TabItem { Header = "Quest fields", Content = _fieldTabs }, new TabItem { Header = "Starters & enders", Content = LinksPage() } } };
        var preview = new TabControl { Items = { new TabItem { Header = "Decoded summary", Content = new ScrollViewer { Content = new Border { Padding = new Thickness(16), BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _summary } } }, new TabItem { Header = "SQL change plan", Content = _sql } } };
        var workspace = new Grid { ColumnDefinitions = new("3*,Auto,2*"), Children = { editor, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(preview, 2) } };
        var reserveId = AccentButton("Reserve project ID"); reserveId.Click += async (_, _) => await ReserveProjectIdAsync(reserveId); var export = new Button { Content = "Export SQL…" }; export.Click += async (_, _) => await ExportAsync(); var exportDraft = new Button { Content = "Export portable draft…" }; exportDraft.Click += async (_, _) => await ExportDraftAsync(); _commit.Click += (_, _) => PrepareCommit();
        Content = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(workspace, 1), WithRow(new WrapPanel { Children = { reserveId, export, exportDraft, _commit, _status } }, 2), WithRow(_confirmation, 3) } };
        RebuildFields(); RefreshSchemaStatus();
    }

    private async Task ReserveProjectIdAsync(Button button)
    {
        if (string.IsNullOrWhiteSpace(_session.Settings.ActiveProjectPath)) { _status.Text = "Opening Projects & shared IDs to create or choose a project…"; ProjectWorkspaceRequested?.Invoke(this, EventArgs.Empty); return; }
        try
        {
            button.IsEnabled = false; var current = Values(); var prior = ToUInt(Value(current, "ID")); var title = Convert.ToString(Value(current, "LogTitle"), CultureInfo.InvariantCulture) ?? "New quest";
            var purpose = _loadedId is null ? $"New quest: {title}" : $"Quest variant of {prior}: {title}"; var reserved = await ProjectIdReservationBridge.ReserveNextAsync(_session, ContentIdDomain.Quest, purpose);
            if (!_editors.TryGetValue("ID", out var idEditor)) throw new InvalidOperationException("The active quest schema has no editable ID field."); idEditor.Null.IsChecked = false; idEditor.Text.Text = reserved.SingleId.ToString(CultureInfo.InvariantCulture); _loadedId = null; _loadedValues = Values(); _commit.Content = "Insert into connected world database"; RefreshPreview();
            _status.Text = $"Reserved quest ID {reserved.SingleId:N0} in {reserved.ProjectName}. The current decoded fields are now a new INSERT draft; no SQL was written.";
        }
        catch (Exception exception) { _status.Text = $"Quest ID reservation failed: {exception.Message}"; DesktopCrashLogger.Log("Quest ID reservation failed", exception); }
        finally { button.IsEnabled = true; }
    }

    public void OpenQuestRow(IReadOnlyDictionary<string, object?> row) { _loadedValues = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase); _loadedId = ToUInt(Value(row, "ID")); _commit.Content = "Apply decoded fields to existing quest"; RebuildFields(); _status.Text = $"Loaded quest {_loadedId}. Existing custom columns are preserved and newly staged starter/ender links remain additive."; }

    private void ResetNew() { _loadedValues = null; _loadedId = null; _commit.Content = "Insert into connected world database"; foreach (var input in LinkInputs()) input.Text = string.Empty; RebuildFields(); }

    private void RebuildFields()
    {
        _fieldTabs.ItemsSource = null; _editors.Clear(); var table = Table(); var initial = new Dictionary<string, object?>(QuestTemplateAdapter.CreateDefaultValues(table), StringComparer.OrdinalIgnoreCase); if (_loadedValues is not null) foreach (var pair in _loadedValues) initial[pair.Key] = pair.Value; var tabs = new List<TabItem>();
        foreach (var group in table.Columns.GroupBy(column => QuestTemplateAdapter.Group(column.Name)).OrderBy(group => GroupOrder(group.Key)))
        {
            var panel = new StackPanel { Spacing = 7, Margin = new Thickness(12) };
            if (group.Key == "Identity & behavior")
            {
                panel.Children.Add(new TextBlock { Text = "Decoded quest type", FontWeight = FontWeight.SemiBold }); panel.Children.Add(_questType);
                panel.Children.Add(new TextBlock { Text = "Quest flags", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) }); panel.Children.Add(_flagPanel);
                panel.Children.Add(new TextBlock { Text = "Every value is still present below, including the raw type and Flags bitmask. Semantic controls stay synchronized with those raw fields.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") });
            }
            foreach (var column in group)
            {
                var value = initial.FirstOrDefault(pair => pair.Key.Equals(column.Name, StringComparison.OrdinalIgnoreCase)).Value; var textField = IsText(column);
                var text = new TextBox { Text = CellText(value), AcceptsReturn = textField, TextWrapping = textField ? TextWrapping.Wrap : TextWrapping.NoWrap, IsReadOnly = column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) || column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) };
                var nullValue = new CheckBox { Content = "NULL", IsChecked = value is null, IsEnabled = column.Nullable && !text.IsReadOnly }; nullValue.IsCheckedChanged += (_, _) => { text.IsEnabled = nullValue.IsChecked != true; RefreshPreview(); }; text.IsEnabled = value is not null; text.TextChanged += (_, _) => { RawSemanticChanged(column.Name); RefreshPreview(); };
                var description = QuestSemanticCatalog.DescribeField(column.Name); var label = new TextBlock { Text = $"{column.Name} · {column.ColumnType}{(column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) ? " · PRIMARY KEY" : string.Empty)}{(description.Length == 0 ? string.Empty : $"\n{description}")}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = description.Length == 0 ? null : Brush.Parse("#B7C2D5") };
                var row = new Grid { ColumnDefinitions = new("2*,3*,Auto,Auto"), ColumnSpacing = 9, Children = { label, WithColumn(text, 1), WithColumn(nullValue, 2) } };
                if (ReferenceForField(column.Name) is { } reference)
                {
                    var find = new Button { Content = "Find…", IsEnabled = !text.IsReadOnly }; find.Click += (_, _) =>
                    {
                        var current = uint.TryParse(text.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                        ReferenceLookupRequested?.Invoke(this, new(reference, column.Name, current, selected => text.Text = selected.ToString(CultureInfo.InvariantCulture)));
                    };
                    Grid.SetColumn(find, 3); row.Children.Add(find);
                }
                panel.Children.Add(row); _editors[column.Name] = (column, text, nullValue);
            }
            tabs.Add(new TabItem { Header = group.Key, Content = new ScrollViewer { Content = panel } });
        }
        _fieldTabs.ItemsSource = tabs; SyncSemanticControls(); RefreshPreview();
    }

    private Control LinksPage() => new ScrollViewer { Content = new StackPanel { Spacing = 9, Margin = new Thickness(12), Children = { new TextBlock { Text = "Quest-giver relationships", FontSize = 17, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Enter template IDs separated by commas, spaces, or new lines, or use the searchable pickers. These become creature_queststarter/ender and gameobject_queststarter/ender rows in the same transaction. Existing link identities are refused rather than replaced.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") }, LinkPicker("Creature starters", _creatureStarters, ReferenceDomain.Creature), LinkPicker("Creature enders", _creatureEnders, ReferenceDomain.Creature), LinkPicker("Gameobject starters", _gameObjectStarters, ReferenceDomain.GameObject), LinkPicker("Gameobject enders", _gameObjectEnders, ReferenceDomain.GameObject) } } };

    private Control LinkPicker(string label, TextBox input, ReferenceDomain domain)
    {
        var find = new Button { Content = "Find and add…" }; find.Click += (_, _) => ReferenceLookupRequested?.Invoke(this, new(domain, label, 0, selected =>
        {
            var ids = ParseIds(input.Text).Append(selected).Distinct(); input.Text = string.Join(", ", ids);
        }));
        return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7, Children = { input, WithColumn(find, 1) } } } };
    }

    private static ReferenceDomain? ReferenceForField(string name)
    {
        if (name.StartsWith("RewardItem", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RewardChoiceItemID", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RequiredItemId", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ItemDrop", StringComparison.OrdinalIgnoreCase) || name.Equals("StartItem", StringComparison.OrdinalIgnoreCase)) return ReferenceDomain.Item;
        if (name.Equals("RewardSpell", StringComparison.OrdinalIgnoreCase) || name.Equals("RewardDisplaySpell", StringComparison.OrdinalIgnoreCase)) return ReferenceDomain.Spell;
        if (name.Equals("RewardNextQuest", StringComparison.OrdinalIgnoreCase)) return ReferenceDomain.Quest;
        return null;
    }

    private void RawSemanticChanged(string name) { if (_syncingSemantics) return; if (name.Equals("QuestType", StringComparison.OrdinalIgnoreCase) || name.Equals("Flags", StringComparison.OrdinalIgnoreCase)) SyncSemanticControls(); }
    private void SyncSemanticControls()
    {
        _syncingSemantics = true;
        try
        {
            var type = RawInt("QuestType"); _questType.SelectedItem = QuestSemanticCatalog.Types.FirstOrDefault(item => item.Value == type) ?? QuestSemanticCatalog.Types[0]; var flags = unchecked((uint)RawLong("Flags")); foreach (var item in _flagBoxes) item.Value.IsChecked = (flags & item.Key) != 0;
        }
        finally { _syncingSemantics = false; }
    }
    private void TypeChanged() { if (_syncingSemantics || _questType.SelectedItem is not QuestTypeDefinition type || !_editors.TryGetValue("QuestType", out var editor)) return; _syncingSemantics = true; editor.Text.Text = type.Value.ToString(CultureInfo.InvariantCulture); _syncingSemantics = false; RefreshPreview(); }
    private void FlagsChanged() { if (_syncingSemantics || !_editors.TryGetValue("Flags", out var editor)) return; var value = _flagBoxes.Where(pair => pair.Value.IsChecked == true).Aggregate(0u, (current, pair) => current | pair.Key); _syncingSemantics = true; editor.Text.Text = value.ToString(CultureInfo.InvariantCulture); _syncingSemantics = false; RefreshPreview(); }

    private IReadOnlyDictionary<string, object?> Values()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); foreach (var editor in _editors.Values) values[editor.Column.Name] = editor.Null.IsChecked == true ? null : ParseCell(editor.Column, editor.Text.Text); return values;
    }
    private QuestEndpointLinks Links() => new(ParseIds(_creatureStarters.Text), ParseIds(_creatureEnders.Text), ParseIds(_gameObjectStarters.Text), ParseIds(_gameObjectEnders.Text));
    private WorldContentWritePlan Plan() { var capabilities = _session.DatabaseCapabilities ?? new DatabaseCapabilities("offline", "world", PortableCapabilities()); return QuestTemplateAdapter.CreatePlan(Table(), Values(), capabilities, Links()); }
    private IReadOnlyDictionary<string, DatabaseTableCapability> PortableCapabilities() { var quest = QuestTemplateAdapter.CreatePortableTable(); static DatabaseTableCapability Link(string name) => new(name, [new("id", "int", "int unsigned", false, "0", "PRI", "", 1), new("quest", "int", "int unsigned", false, "0", "PRI", "", 2)]); return new Dictionary<string, DatabaseTableCapability>(StringComparer.OrdinalIgnoreCase) { [quest.Name] = quest, ["creature_queststarter"] = Link("creature_queststarter"), ["creature_questender"] = Link("creature_questender"), ["gameobject_queststarter"] = Link("gameobject_queststarter"), ["gameobject_questender"] = Link("gameobject_questender") }; }
    private DatabaseTableCapability Table() => _session.DatabaseCapabilities?.FindTable("quest_template") ?? QuestTemplateAdapter.CreatePortableTable();

    private void RefreshPreview()
    {
        _confirmation.IsVisible = false; _pendingPlan = null;
        try { var values = Values(); var plan = Plan(); var id = ToUInt(Value(values, "ID")); var title = Convert.ToString(Value(values, "LogTitle"), CultureInfo.InvariantCulture); var flags = unchecked((uint)RawLong("Flags")); var selectedFlags = QuestSemanticCatalog.Flags.Where(flag => (flags & flag.Value) != 0).Select(flag => flag.Name).ToArray(); _summary.Text = $"{title}\nQuest {id} · {_questType.SelectedItem}\nLevel {Value(values, "QuestLevel")} · minimum {Value(values, "MinLevel")} · sort {Value(values, "QuestSortID")}\nFlags: {(selectedFlags.Length == 0 ? "none" : string.Join(", ", selectedFlags))}\nGuaranteed rewards: {CountNonzero(values, "RewardItem", 4):N0} · choice rewards: {CountNonzero(values, "RewardChoiceItemID", 6):N0}\nCreature objectives: {CountNonzero(values, "RequiredNpcOrGo", 4):N0} · item objectives: {CountNonzero(values, "RequiredItemId", 6):N0}\nStarter links: {plan.Rows.Count(row => row.Table.EndsWith("queststarter", StringComparison.OrdinalIgnoreCase)):N0} · ender links: {plan.Rows.Count(row => row.Table.EndsWith("questender", StringComparison.OrdinalIgnoreCase)):N0}\nAll {Table().Columns.Count:N0} live quest_template columns are represented in the grouped editor."; _sql.Text = plan.PreviewSql(); }
        catch (Exception exception) { _summary.Text = $"Quest draft is currently incomplete: {exception.Message}"; _sql.Text = $"-- Change plan is currently invalid: {exception.Message}"; }
    }

    private void PrepareCommit()
    {
        try { if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _status.Text = "Connect and verify Server & SQL before deploying. SQL export remains available offline."; return; } _pendingPlan = Plan(); var id = ToUInt(Value(Values(), "ID")); if (_loadedId is { } loaded && id != loaded) throw new InvalidOperationException("The decoded editor will not silently change a primary quest ID. Clone it explicitly instead."); var editing = _loadedId is not null; var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => { _pendingPlan = null; _confirmation.IsVisible = false; }; var confirm = AccentButton(editing ? $"Update quest {_loadedId}" : $"Insert quest {id}"); confirm.Click += async (_, _) => await CommitAsync(confirm); _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = editing ? $"Update all mapped quest fields and insert {_pendingPlan.Rows.Count - 1:N0} newly staged starter/ender link(s) in one transaction? Custom columns are preserved; existing links are never replaced." : $"Insert the quest and {_pendingPlan.Rows.Count - 1:N0} starter/ender link(s) in one transaction? Existing identities are refused.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true; }
        catch (Exception exception) { _status.Text = $"Cannot deploy: {exception.Message}"; }
    }

    private async Task CommitAsync(Button button)
    {
        if (_pendingPlan is null || _session.DatabaseProfile is null) return; try { button.IsEnabled = false; var service = new WorldContentTemplateService(); if (_loadedId is null) await service.InsertAsync(_session.DatabaseProfile, _pendingPlan); else await service.UpdateFirstAndInsertChildrenAsync(_session.DatabaseProfile, _pendingPlan); _status.Text = _loadedId is null ? "Quest and giver links inserted transactionally." : "Quest fields updated and newly staged giver links inserted transactionally; custom columns were preserved."; _pendingPlan = null; _confirmation.IsVisible = false; } catch (Exception exception) { _status.Text = $"Quest deployment failed: {exception.Message}"; DesktopCrashLogger.Log("Quest deployment failed", exception); } finally { button.IsEnabled = true; }
    }
    private async Task ExportAsync() { try { var plan = Plan(); var id = ToUInt(Value(Values(), "ID")); var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export quest SQL", SuggestedFileName = $"quest-{id}.sql", FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) await File.WriteAllTextAsync(path, plan.PreviewSql() + Environment.NewLine); } catch (Exception exception) { _status.Text = $"Export failed: {exception.Message}"; } }
    private async Task ExportDraftAsync() { try { var values = Values().ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : CellText(pair.Value), StringComparer.OrdinalIgnoreCase); var draft = new QuestPortableDraft(values, Links()); var id = ToUInt(Value(Values(), "ID")); var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export portable quest draft", SuggestedFileName = $"quest-{id}.crucible.json", FileTypeChoices = [new FilePickerFileType("Crucible JSON") { Patterns = ["*.crucible.json", "*.json"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine); } catch (Exception exception) { _status.Text = $"Draft export failed: {exception.Message}"; } }
    private void SessionChanged(object? sender, EventArgs e) { RebuildFields(); RefreshSchemaStatus(); } private void RefreshSchemaStatus() { var capabilities = _session.DatabaseCapabilities; _status.Text = capabilities?.FindTable("quest_template") is { } table ? $"Live schema ready · {capabilities.Database}.quest_template · {table.Columns.Count:N0} columns" : "Offline portable quest schema ready · connect Server & SQL for live deployment."; }
    public void Dispose() => _session.Changed -= SessionChanged;

    private int RawInt(string name) { try { return _editors.TryGetValue(name, out var editor) ? Convert.ToInt32(editor.Text.Text, CultureInfo.InvariantCulture) : 0; } catch { return 0; } } private long RawLong(string name) { try { return _editors.TryGetValue(name, out var editor) ? Convert.ToInt64(editor.Text.Text, CultureInfo.InvariantCulture) : 0; } catch { return 0; } }
    private static int CountNonzero(IReadOnlyDictionary<string, object?> values, string prefix, int count) => Enumerable.Range(1, count).Count(index => { try { return Convert.ToInt64(Value(values, prefix + index), CultureInfo.InvariantCulture) != 0; } catch { return false; } });
    private static IReadOnlyList<uint> ParseIds(string? text) { if (string.IsNullOrWhiteSpace(text)) return []; var result = new List<uint>(); foreach (var token in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { if (!uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0) throw new InvalidDataException($"Invalid template ID '{token}'."); result.Add(id); } return result.Distinct().ToArray(); }
    private static object? ParseCell(DatabaseColumnCapability column, string? text) { text ??= string.Empty; if (column.DataType.Contains("binary", StringComparison.OrdinalIgnoreCase) || column.DataType.Contains("blob", StringComparison.OrdinalIgnoreCase)) return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(text[2..]) : System.Text.Encoding.UTF8.GetBytes(text); return text; }
    private static string CellText(object? value) => value switch { null => string.Empty, byte[] bytes => "0x" + Convert.ToHexString(bytes), DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), IFormattable format => format.ToString(null, CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };
    private static bool IsText(DatabaseColumnCapability column) => column.DataType.Contains("text", StringComparison.OrdinalIgnoreCase) || column.DataType.Contains("char", StringComparison.OrdinalIgnoreCase);
    private static int GroupOrder(string name) => name switch { "Identity & behavior" => 0, "Quest text" => 1, "Objectives" => 2, "Rewards" => 3, "Requirements" => 4, _ => 5 };
    private static TextBox Ids(string placeholder) => new() { PlaceholderText = placeholder, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private IEnumerable<TextBox> LinkInputs() => [_creatureStarters, _creatureEnders, _gameObjectStarters, _gameObjectEnders];
    private static object? Value(IReadOnlyDictionary<string, object?> row, string name) => row.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static uint ToUInt(object? value) { try { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); } catch { return 0; } }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Quest workspace is not attached to the main window.");
    private static Control Labeled(string text, Control control) => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = text, FontWeight = FontWeight.SemiBold }, control } };
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") }; private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; } private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
