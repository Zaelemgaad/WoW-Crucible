using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class CreatureWorkspaceView : UserControl, IDisposable
{
    private sealed record Choice(int Value, string Name) { public override string ToString() => Name; }
    private readonly DesktopWorkspaceSession _session;
    private readonly NumericUpDown _entry = Number(1, uint.MaxValue, 900000);
    private readonly TextBox _name = new() { Text = "New Crucible Creature" };
    private readonly TextBox _subname = new();
    private readonly NumericUpDown[] _displayIds = Enumerable.Range(0, 4).Select(_ => Number(0, uint.MaxValue)).ToArray();
    private readonly NumericUpDown _minLevel = Number(1, 255, 80);
    private readonly NumericUpDown _maxLevel = Number(1, 255, 80);
    private readonly ComboBox _faction = Choices((35, "Friendly to everyone [35]"), (14, "Hostile monster [14]"), (16, "Hostile creature [16]"), (7, "Neutral creature [7]"), (84, "Alliance [84]"), (83, "Horde [83]"));
    private readonly ComboBox _rank = Choices((0, "Normal"), (1, "Elite"), (2, "Rare elite"), (3, "World boss"), (4, "Rare"));
    private readonly ComboBox _type = Choices((7, "Humanoid"), (1, "Beast"), (2, "Dragonkin"), (3, "Demon"), (4, "Elemental"), (5, "Giant"), (6, "Undead"), (8, "Critter"), (9, "Mechanical"), (10, "Not specified"), (11, "Totem"), (12, "Non-combat pet"), (13, "Gas cloud"));
    private readonly ComboBox _unitClass = Choices((1, "Warrior"), (2, "Paladin"), (4, "Rogue"), (8, "Mage"));
    private readonly NumericUpDown _family = Number(-1, 100, 0);
    private readonly NumericUpDown _scale = Number(0.01m, 100, 1);
    private readonly NumericUpDown _walk = Number(0.01m, 100, 1);
    private readonly NumericUpDown _run = Number(0.01m, 100, 1.14286m);
    private readonly NumericUpDown _health = Number(0.01m, 100000, 1);
    private readonly NumericUpDown _mana = Number(0, 100000, 1);
    private readonly NumericUpDown _armor = Number(0, 100000, 1);
    private readonly NumericUpDown _damage = Number(0, 100000, 1);
    private readonly NumericUpDown _baseAttack = Number(0, uint.MaxValue, 2000);
    private readonly NumericUpDown _rangeAttack = Number(0, uint.MaxValue, 2000);
    private readonly NumericUpDown _unitFlags = Number(0, uint.MaxValue);
    private readonly NumericUpDown _unitFlags2 = Number(0, uint.MaxValue);
    private readonly NumericUpDown _dynamicFlags = Number(0, uint.MaxValue);
    private readonly NumericUpDown _typeFlags = Number(0, uint.MaxValue);
    private readonly NumericUpDown _loot = Number(0, uint.MaxValue);
    private readonly NumericUpDown _pickpocket = Number(0, uint.MaxValue);
    private readonly NumericUpDown _skinLoot = Number(0, uint.MaxValue);
    private readonly NumericUpDown _minGold = Number(0, uint.MaxValue);
    private readonly NumericUpDown _maxGold = Number(0, uint.MaxValue);
    private readonly TextBox _aiName = new();
    private readonly TextBox _scriptName = new();
    private readonly StackPanel _vendorRows = new() { Spacing = 7 };
    private readonly StackPanel _lootRows = new() { Spacing = 7 };
    private readonly CheckBox _regen = new() { Content = "Regenerates health", IsChecked = true };
    private readonly (uint Flag, CheckBox Box)[] _npcFlags =
    [
        (1, new CheckBox { Content = "Gossip" }), (2, new CheckBox { Content = "Quest giver" }), (16, new CheckBox { Content = "Trainer" }),
        (128, new CheckBox { Content = "Vendor" }), (4096, new CheckBox { Content = "Repair" }), (8192, new CheckBox { Content = "Flight master" }),
        (65536, new CheckBox { Content = "Innkeeper" }), (131072, new CheckBox { Content = "Banker" }), (2097152, new CheckBox { Content = "Auctioneer" }),
        (4194304, new CheckBox { Content = "Stable master" }), (8388608, new CheckBox { Content = "Guild bank" }), (16777216, new CheckBox { Content = "Spell click" })
    ];
    private readonly TextBlock _visualPreview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _sqlPreview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBlock _status = Status("Offline current-core schema ready.");
    private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly M2PreviewView _model = new();
    private readonly TextBlock _modelStatus = Status("Load an extracted M2/SKIN to preview the chosen creature appearance geometry.");
    private readonly CheckBox _showAttachments = new() { Content = "Show attachment points" };
    private readonly ComboBox _attachmentPicker = new() { PlaceholderText = "No attachment points loaded" };
    private WorldContentWritePlan? _pendingPlan;
    private readonly Button _commit = AccentButton("Insert into connected world database");
    private uint? _loadedEntry;

    public event EventHandler? BackRequested;
    public event EventHandler? ProjectWorkspaceRequested;
    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public CreatureWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged;
        HookPreviewEvents();
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "CREATURES & NPCs", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, 1) } };
        var editor = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Identity & appearance", Content = new ScrollViewer { Content = IdentityForm() } },
                new TabItem { Header = "Interaction", Content = new ScrollViewer { Content = InteractionForm() } },
                new TabItem { Header = "Combat & movement", Content = new ScrollViewer { Content = CombatForm() } },
                new TabItem { Header = "Loot IDs & scripting", Content = new ScrollViewer { Content = LootForm() } },
                new TabItem { Header = "Vendor inventory", Content = VendorPage() },
                new TabItem { Header = "Loot entries", Content = LootRowsPage() }
            }
        };
        var loadModel = new Button { Content = "Load extracted M2…" }; loadModel.Click += async (_, _) => await LoadModelAsync();
        var clearModel = new Button { Content = "Clear" }; clearModel.Click += (_, _) => { _model.ClearGeometry(); _attachmentPicker.ItemsSource = null; _model.SetAttachmentOverlay(false); _modelStatus.Text = "Model preview cleared."; };
        _showAttachments.Click += (_, _) => ApplyAttachmentOverlay(); _attachmentPicker.SelectionChanged += (_, _) => ApplyAttachmentOverlay();
        var modelPage = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { new WrapPanel { Children = { loadModel, clearModel, _showAttachments, _attachmentPicker } }, WithRow(new Border { Background = Brush.Parse("#090D14"), Child = _model }, 1), WithRow(_modelStatus, 2) } };
        var preview = new TabControl
        {
            Items =
            {
                new TabItem { Header = "In-game summary", Content = new ScrollViewer { Content = new Border { Padding = new Thickness(16), BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _visualPreview } } },
                new TabItem { Header = "3D model", Content = modelPage },
                new TabItem { Header = "SQL change plan", Content = _sqlPreview }
            }
        };
        var workspace = new Grid { ColumnDefinitions = new("3*,Auto,2*"), Children = { editor, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(preview, 2) } };
        var export = new Button { Content = "Export SQL…" }; export.Click += async (_, _) => await ExportAsync();
        var reserveId = AccentButton("Reserve project ID"); reserveId.Click += async (_, _) => await ReserveProjectIdAsync(reserveId);
        _commit.Click += (_, _) => PrepareInsert();
        var actions = new WrapPanel { Children = { reserveId, export, _commit, _status } };
        Content = new Grid
        {
            RowDefinitions = new("Auto,*,Auto,Auto"),
            Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(workspace, 1), WithRow(actions, 2), WithRow(_confirmation, 3) }
        };
        RefreshPreview(); RefreshSchemaStatus();
    }

    private async Task ReserveProjectIdAsync(Button button)
    {
        if (string.IsNullOrWhiteSpace(_session.Settings.ActiveProjectPath)) { _status.Text = "Opening Projects & shared IDs to create or choose a project…"; ProjectWorkspaceRequested?.Invoke(this, EventArgs.Empty); return; }
        try
        {
            button.IsEnabled = false; var prior = (uint)(_entry.Value ?? 0); var purpose = _loadedEntry is null ? $"New creature: {_name.Text}" : $"Creature variant of {prior}: {_name.Text}";
            var reserved = await ProjectIdReservationBridge.ReserveNextAsync(_session, ContentIdDomain.CreatureTemplate, purpose); _entry.Value = reserved.SingleId; _loadedEntry = null; _commit.Content = "Insert into connected world database"; RefreshPreview();
            _status.Text = $"Reserved creature ID {reserved.SingleId:N0} in {reserved.ProjectName}. The current decoded fields are now a new INSERT draft; no SQL was written.";
        }
        catch (Exception exception) { _status.Text = $"Creature ID reservation failed: {exception.Message}"; DesktopCrashLogger.Log("Creature ID reservation failed", exception); }
        finally { button.IsEnabled = true; }
    }

    public void OpenCreatureRow(IReadOnlyDictionary<string, object?> row)
    {
        _entry.Value = Decimal(row, "entry", 1); _name.Text = Text(row, "name"); _subname.Text = Text(row, "subname");
        _minLevel.Value = Decimal(row, "minlevel", 1); _maxLevel.Value = Decimal(row, "maxlevel", 1); Set(_faction, Int(row, "faction")); Set(_rank, Int(row, "rank")); Set(_type, Int(row, "type")); Set(_unitClass, Int(row, "unit_class"));
        _family.Value = Decimal(row, "family"); _scale.Value = Decimal(row, "scale", 1); _walk.Value = Decimal(row, "speed_walk", 1); _run.Value = Decimal(row, "speed_run", 1.14286m);
        _health.Value = Decimal(row, "HealthModifier", 1); _mana.Value = Decimal(row, "ManaModifier", 1); _armor.Value = Decimal(row, "ArmorModifier", 1); _damage.Value = Decimal(row, "DamageModifier", 1);
        _baseAttack.Value = Decimal(row, "BaseAttackTime", 2000); _rangeAttack.Value = Decimal(row, "RangeAttackTime", 2000); _unitFlags.Value = Decimal(row, "unit_flags"); _unitFlags2.Value = Decimal(row, "unit_flags2"); _dynamicFlags.Value = Decimal(row, "dynamicflags"); _typeFlags.Value = Decimal(row, "type_flags");
        _loot.Value = Decimal(row, "lootid"); _pickpocket.Value = Decimal(row, "pickpocketloot"); _skinLoot.Value = Decimal(row, "skinloot"); _minGold.Value = Decimal(row, "mingold"); _maxGold.Value = Decimal(row, "maxgold"); _aiName.Text = Text(row, "AIName"); _scriptName.Text = Text(row, "ScriptName"); _regen.IsChecked = Int(row, "RegenHealth") != 0;
        var flags = (uint)Decimal(row, "npcflag"); foreach (var flag in _npcFlags) flag.Box.IsChecked = (flags & flag.Flag) != 0;
        for (var index = 0; index < 4; index++) _displayIds[index].Value = Decimal(row, $"modelid{index + 1}");
        _loadedEntry = (uint)(_entry.Value ?? 0); _commit.Content = "Apply decoded fields to existing creature"; RefreshPreview(); _status.Text = $"Loaded creature {_loadedEntry} into the decoded editor. Normalized model/vendor/loot child rows can be reviewed in SQL Studio.";
    }

    private Control IdentityForm() => Form(
        ("Entry ID", _entry), ("Name", _name), ("Subname / title", _subname),
        ("Display ID 1", _displayIds[0]), ("Display ID 2", _displayIds[1]), ("Display ID 3", _displayIds[2]), ("Display ID 4", _displayIds[3]),
        ("Minimum level", _minLevel), ("Maximum level", _maxLevel), ("Faction template", _faction), ("Creature type", _type), ("Rank", _rank), ("Unit class", _unitClass), ("Creature family", _family), ("Display scale", _scale));

    private Control InteractionForm()
    {
        var flags = new WrapPanel(); foreach (var flag in _npcFlags) flags.Children.Add(flag.Box);
        return new StackPanel
        {
            Margin = new Thickness(12), Spacing = 10, Children =
            {
                new TextBlock { Text = "NPC services", FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Service flags make the client offer an interaction; vendor inventory, trainer data, quests, gossip, and other linked records must also exist before that service is complete.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") },
                flags
            }
        };
    }

    private Control CombatForm() => Form(("Walk speed", _walk), ("Run speed", _run), ("Health modifier", _health), ("Mana modifier", _mana), ("Armor modifier", _armor), ("Damage modifier", _damage), ("Base attack time (ms)", _baseAttack), ("Ranged attack time (ms)", _rangeAttack), ("Unit flags", _unitFlags), ("Unit flags 2", _unitFlags2), ("Dynamic flags", _dynamicFlags), ("Type flags", _typeFlags), ("Health behavior", _regen));
    private Control LootForm() => Form(("Creature loot ID", _loot), ("Pickpocket loot ID", _pickpocket), ("Skinning loot ID", _skinLoot), ("Minimum copper", _minGold), ("Maximum copper", _maxGold), ("AI name", _aiName), ("Script name", _scriptName));

    private Control VendorPage()
    {
        var add = AccentButton("Add vendor item"); add.Click += (_, _) => AddVendorRow();
        return new Grid
        {
            RowDefinitions = new("Auto,*"), Margin = new Thickness(10),
            Children =
            {
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        add,
                        new TextBlock { Text = "Rows are inserted transactionally with the creature. Existing vendor identities are refused rather than replaced.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") }
                    }
                },
                WithRow(new ScrollViewer { Content = _vendorRows }, 1)
            }
        };
    }

    private Control LootRowsPage()
    {
        var add = AccentButton("Add loot row"); add.Click += (_, _) => AddLootRow();
        return new Grid
        {
            RowDefinitions = new("Auto,*"), Margin = new Thickness(10),
            Children =
            {
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        add,
                        new TextBlock { Text = "Loot entry normally matches the creature template's loot ID. Reference rows may use item 0 with a nonzero reference.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") }
                    }
                },
                WithRow(new ScrollViewer { Content = _lootRows }, 1)
            }
        };
    }

    private void AddVendorRow()
    {
        var row = new VendorRowEditor(RefreshPreview, request => ReferenceLookupRequested?.Invoke(this, request)); row.RemoveRequested += (_, _) => { _vendorRows.Children.Remove(row); RefreshPreview(); };
        _vendorRows.Children.Add(row); _npcFlags.Single(flag => flag.Flag == 128).Box.IsChecked = true; RefreshPreview();
    }

    private void AddLootRow()
    {
        var entry = (uint)(_loot.Value ?? 0); if (entry == 0) { entry = (uint)(_entry.Value ?? 0); _loot.Value = entry; }
        var row = new LootRowEditor(entry, RefreshPreview, request => ReferenceLookupRequested?.Invoke(this, request)); row.RemoveRequested += (_, _) => { _lootRows.Children.Remove(row); RefreshPreview(); };
        _lootRows.Children.Add(row); RefreshPreview();
    }

    private void HookPreviewEvents()
    {
        foreach (var number in Numbers()) number.ValueChanged += (_, _) => RefreshPreview();
        foreach (var text in new[] { _name, _subname, _aiName, _scriptName }) text.TextChanged += (_, _) => RefreshPreview();
        foreach (var combo in new[] { _faction, _rank, _type, _unitClass }) combo.SelectionChanged += (_, _) => RefreshPreview();
        foreach (var flag in _npcFlags) flag.Box.IsCheckedChanged += (_, _) => RefreshPreview();
        _regen.IsCheckedChanged += (_, _) => RefreshPreview();
    }

    private CreatureTemplateDraft Draft() => new(
        (uint)(_entry.Value ?? 0), _name.Text ?? string.Empty, _subname.Text ?? string.Empty, _displayIds.Select(value => (uint)(value.Value ?? 0)).ToArray(),
        (byte)(_minLevel.Value ?? 1), (byte)(_maxLevel.Value ?? 1), (ushort)Selected(_faction), _npcFlags.Where(flag => flag.Box.IsChecked == true).Aggregate(0u, (value, flag) => value | flag.Flag),
        (byte)Selected(_rank), (byte)Selected(_type), (sbyte)(_family.Value ?? 0), (byte)Selected(_unitClass), (float)(_scale.Value ?? 1), (float)(_walk.Value ?? 1), (float)(_run.Value ?? 1),
        (float)(_health.Value ?? 1), (float)(_mana.Value ?? 1), (float)(_armor.Value ?? 1), (float)(_damage.Value ?? 1), (uint)(_baseAttack.Value ?? 0), (uint)(_rangeAttack.Value ?? 0),
        (uint)(_unitFlags.Value ?? 0), (uint)(_unitFlags2.Value ?? 0), (uint)(_dynamicFlags.Value ?? 0), (uint)(_typeFlags.Value ?? 0), (uint)(_loot.Value ?? 0), (uint)(_pickpocket.Value ?? 0), (uint)(_skinLoot.Value ?? 0),
        (uint)(_minGold.Value ?? 0), (uint)(_maxGold.Value ?? 0), _aiName.Text ?? string.Empty, _scriptName.Text ?? string.Empty, _regen.IsChecked == true,
        _vendorRows.Children.OfType<VendorRowEditor>().Select(row => row.Draft()).ToArray(), _lootRows.Children.OfType<LootRowEditor>().Select(row => row.Draft()).ToArray());

    private WorldContentWritePlan Plan() => CreatureTemplateAdapter.CreatePlan(Draft(), _session.DatabaseCapabilities ?? CreatureTemplateAdapter.CreatePortableCapabilities());

    private void RefreshPreview()
    {
        _confirmation.IsVisible = false; _pendingPlan = null;
        var draft = Draft(); var services = _npcFlags.Where(flag => flag.Box.IsChecked == true).Select(flag => Convert.ToString(flag.Box.Content)).ToArray();
        _visualPreview.Text = $"{draft.Name}\n{(string.IsNullOrWhiteSpace(draft.Subname) ? string.Empty : $"<{draft.Subname}>\n")}" +
            $"Level {draft.MinimumLevel}{(draft.MaximumLevel == draft.MinimumLevel ? string.Empty : $"–{draft.MaximumLevel}")} {SelectedName(_type)} · {SelectedName(_rank)}\n" +
            $"Faction {SelectedName(_faction)} · Unit class {SelectedName(_unitClass)}\nDisplay IDs: {string.Join(", ", draft.DisplayIds.Where(id => id != 0).DefaultIfEmpty(0u))}\n" +
            $"Services: {(services.Length == 0 ? "none" : string.Join(", ", services))}\nHealth ×{draft.HealthModifier:0.##} · Mana ×{draft.ManaModifier:0.##} · Armor ×{draft.ArmorModifier:0.##} · Damage ×{draft.DamageModifier:0.##}\n" +
            $"Loot {draft.LootId} · {draft.LootItems?.Count ?? 0:N0} loot row(s) · {draft.VendorItems?.Count ?? 0:N0} vendor item(s)\nAI {(string.IsNullOrWhiteSpace(draft.AiName) ? "core default" : draft.AiName)} · Script {(string.IsNullOrWhiteSpace(draft.ScriptName) ? "none" : draft.ScriptName)}";
        try { var plan = Plan(); _sqlPreview.Text = plan.PreviewSql() + (plan.OmittedFields.Count == 0 ? string.Empty : $"\n\n-- Not present / not supplied in target schema:\n-- {string.Join("\n-- ", plan.OmittedFields)}"); }
        catch (Exception exception) { _sqlPreview.Text = $"-- Change plan is currently invalid: {exception.Message}"; }
    }

    private async Task ExportAsync()
    {
        try
        {
            var plan = Plan(); var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export creature SQL", SuggestedFileName = $"creature-{(uint)(_entry.Value ?? 0)}.sql", FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }] });
            var path = file?.TryGetLocalPath(); if (path is not null) await File.WriteAllTextAsync(path, plan.PreviewSql() + Environment.NewLine);
        }
        catch (Exception exception) { _status.Text = $"Export failed: {exception.Message}"; }
    }

    private void PrepareInsert()
    {
        try
        {
            if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _status.Text = "Connect and verify Server & SQL before inserting. SQL export remains available offline."; return; }
            _pendingPlan = Plan(); if (_loadedEntry is { } loaded && (uint)(_entry.Value ?? 0) != loaded) throw new InvalidOperationException("The decoded editor will not silently change a primary creature ID. Clone or migrate it explicitly instead.");
            var editing = _loadedEntry is not null;
            var cancel = new Button { Content = "Cancel" }; var confirm = AccentButton($"Confirm creature {(uint)(_entry.Value ?? 0)} insert");
            cancel.Click += (_, _) => { _pendingPlan = null; _confirmation.IsVisible = false; };
            confirm.Content = editing ? $"Confirm creature {_loadedEntry} update" : $"Confirm creature {(uint)(_entry.Value ?? 0)} insert"; confirm.Click += async (_, _) => await InsertAsync(confirm);
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = editing ? $"Update Crucible's decoded creature_template fields for '{_name.Text}' and insert {_pendingPlan.Rows.Count - 1:N0} newly staged child row(s) in the same transaction? Existing child identities are refused, never replaced." : $"Insert '{_name.Text}' and {_pendingPlan.Rows.Count - 1:N0} related row(s) transactionally? Existing identities are refused, never replaced.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, WithColumn(cancel, 1), WithColumn(confirm, 2) } };
            _confirmation.IsVisible = true;
        }
        catch (Exception exception) { _status.Text = $"Cannot insert: {exception.Message}"; }
    }

    private async Task InsertAsync(Button button)
    {
        if (_pendingPlan is null || _session.DatabaseProfile is null) return;
        try { button.IsEnabled = false; var service = new WorldContentTemplateService(); if (_loadedEntry is not null) await service.UpdateFirstAndInsertChildrenAsync(_session.DatabaseProfile, _pendingPlan); else await service.InsertAsync(_session.DatabaseProfile, _pendingPlan); _status.Text = _loadedEntry is null ? "Creature template and related rows inserted transactionally. Reload creature_template or restart worldserver." : "Decoded creature fields updated and newly staged model/vendor/loot rows inserted transactionally; custom columns were preserved."; _pendingPlan = null; _confirmation.IsVisible = false; }
        catch (Exception exception) { _status.Text = $"Creature insert failed: {exception.Message}"; DesktopCrashLogger.Log("Creature insert failed", exception); }
        finally { button.IsEnabled = true; }
    }

    private async Task LoadModelAsync()
    {
        try
        {
            var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose an extracted WotLK creature M2", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("WotLK M2") { Patterns = ["*.m2"] }] });
            var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return; _modelStatus.Text = "Loading model…";
            var geometry = await Task.Run(() => M2PreviewGeometryService.Load(path)); _model.SetGeometry(geometry); RefreshAttachmentPoints(geometry); _modelStatus.Text = $"{Path.GetFileName(path)} · {geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} base geosets · {geometry.TriangleIndices.Count / 3:N0} triangles · {geometry.Attachments.Count:N0} attachment points";
        }
        catch (Exception exception) { _modelStatus.Text = $"Model load failed: {exception.Message}"; }
    }

    private void RefreshAttachmentPoints(M2PreviewGeometry geometry)
    {
        _attachmentPicker.ItemsSource = geometry.Attachments; _attachmentPicker.IsEnabled = geometry.Attachments.Count > 0;
        _attachmentPicker.SelectedItem = geometry.Attachments.FirstOrDefault(attachment => attachment.Id == 11) ?? geometry.Attachments.FirstOrDefault(); ApplyAttachmentOverlay();
    }

    private void ApplyAttachmentOverlay() => _model.SetAttachmentOverlay(_showAttachments.IsChecked == true, (_attachmentPicker.SelectedItem as M2PreviewAttachment)?.Index);

    private void SessionChanged(object? sender, EventArgs e) { RefreshSchemaStatus(); RefreshPreview(); }
    private void RefreshSchemaStatus() { var capabilities = _session.DatabaseCapabilities; _status.Text = capabilities?.FindTable("creature_template") is { } table ? $"Live creature schema ready · {capabilities.Database}.creature_template · {table.Columns.Count:N0} columns · models {(capabilities.FindTable("creature_template_model") is null ? "embedded/legacy" : "normalized/current")}" : "Offline current-core schema ready · connect Server & SQL for live deployment."; }
    public void Dispose() => _session.Changed -= SessionChanged;
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Creature workspace is not attached to the main window.");
    private IEnumerable<NumericUpDown> Numbers() => new[] { _entry, _minLevel, _maxLevel, _family, _scale, _walk, _run, _health, _mana, _armor, _damage, _baseAttack, _rangeAttack, _unitFlags, _unitFlags2, _dynamicFlags, _typeFlags, _loot, _pickpocket, _skinLoot, _minGold, _maxGold }.Concat(_displayIds);
    private static Grid Form(params (string Label, Control Input)[] rows) { var grid = new Grid { ColumnDefinitions = new("Auto,*"), RowSpacing = 7, ColumnSpacing = 10, Margin = new Thickness(12) }; for (var row = 0; row < rows.Length; row++) { grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); var label = new TextBlock { Text = rows[row].Label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(label, row); grid.Children.Add(label); Grid.SetRow(rows[row].Input, row); Grid.SetColumn(rows[row].Input, 1); grid.Children.Add(rows[row].Input); } return grid; }
    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value = 0) => new() { Minimum = minimum, Maximum = maximum, Value = value, Increment = 1 };
    private static ComboBox Choices(params (int Value, string Name)[] values) => new() { ItemsSource = values.Select(value => new Choice(value.Value, value.Name)).ToArray(), SelectedIndex = 0 };
    private static int Selected(ComboBox combo) => combo.SelectedItem is Choice choice ? choice.Value : 0;
    private static string SelectedName(ComboBox combo) => combo.SelectedItem is Choice choice ? choice.Name : string.Empty;
    private static void Set(ComboBox combo, int value) { if (combo.ItemsSource is IEnumerable<Choice> values) combo.SelectedItem = values.FirstOrDefault(item => item.Value == value) ?? values.FirstOrDefault(); }
    private static object? Value(IReadOnlyDictionary<string, object?> row, string name) => row.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static decimal Decimal(IReadOnlyDictionary<string, object?> row, string name, decimal fallback = 0) { try { return Convert.ToDecimal(Value(row, name) ?? fallback, System.Globalization.CultureInfo.InvariantCulture); } catch { return fallback; } }
    private static int Int(IReadOnlyDictionary<string, object?> row, string name) { try { return Convert.ToInt32(Value(row, name) ?? 0, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; } }
    private static string Text(IReadOnlyDictionary<string, object?> row, string name) => Convert.ToString(Value(row, name), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }

    private sealed class VendorRowEditor : UserControl
    {
        private readonly NumericUpDown _slot = Number(-1, int.MaxValue, -1);
        private readonly NumericUpDown _item = Number(1, int.MaxValue, 1);
        private readonly NumericUpDown _maximumCount = Number(0, uint.MaxValue);
        private readonly NumericUpDown _restockSeconds = Number(0, uint.MaxValue);
        private readonly NumericUpDown _extendedCost = Number(0, uint.MaxValue);

        public event EventHandler? RemoveRequested;

        public VendorRowEditor(Action changed, Action<ReferencePickerRequest> lookup)
        {
            foreach (var number in new[] { _slot, _item, _maximumCount, _restockSeconds, _extendedCost })
                number.ValueChanged += (_, _) => changed();

            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
            var findItem = new Button { Content = "Find…" };
            findItem.Click += (_, _) => lookup(new(ReferenceDomain.Item, "Vendor item", (uint)(_item.Value ?? 0), selected => _item.Value = selected));
            Content = new Border
            {
                BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Padding = new Thickness(8),
                Child = new WrapPanel
                {
                    Children =
                    {
                        Labeled("Slot (-1 = automatic)", _slot), Labeled("Item ID", FieldWithButton(_item, findItem)), Labeled("Maximum stock (0 = unlimited)", _maximumCount),
                        Labeled("Restock seconds", _restockSeconds), Labeled("Extended cost ID", _extendedCost),
                        new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { remove } }
                    }
                }
            };
        }

        public VendorItemDraft Draft() => new(
            (int)(_slot.Value ?? -1), (int)(_item.Value ?? 0), (uint)(_maximumCount.Value ?? 0),
            (uint)(_restockSeconds.Value ?? 0), (uint)(_extendedCost.Value ?? 0));
    }

    private sealed class LootRowEditor : UserControl
    {
        private readonly NumericUpDown _entry;
        private readonly NumericUpDown _item = Number(0, int.MaxValue);
        private readonly NumericUpDown _reference = Number(0, int.MaxValue);
        private readonly NumericUpDown _chance = Number(0, 100, 100);
        private readonly CheckBox _questRequired = new() { Content = "Quest required" };
        private readonly NumericUpDown _lootMode = Number(0, ushort.MaxValue, 1);
        private readonly NumericUpDown _groupId = Number(0, byte.MaxValue);
        private readonly NumericUpDown _minimumCount = Number(1, byte.MaxValue, 1);
        private readonly NumericUpDown _maximumCount = Number(1, byte.MaxValue, 1);
        private readonly TextBox _comment = new();

        public event EventHandler? RemoveRequested;

        public LootRowEditor(uint entry, Action changed, Action<ReferencePickerRequest> lookup)
        {
            _entry = Number(1, uint.MaxValue, entry == 0 ? 1 : entry);
            foreach (var number in new[] { _entry, _item, _reference, _chance, _lootMode, _groupId, _minimumCount, _maximumCount })
                number.ValueChanged += (_, _) => changed();
            _questRequired.IsCheckedChanged += (_, _) => changed();
            _comment.TextChanged += (_, _) => changed();

            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
            var findItem = new Button { Content = "Find…" };
            findItem.Click += (_, _) => lookup(new(ReferenceDomain.Item, "Creature loot item", (uint)(_item.Value ?? 0), selected => _item.Value = selected));
            Content = new Border
            {
                BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Padding = new Thickness(8),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new WrapPanel
                        {
                            Children =
                            {
                                Labeled("Loot entry", _entry), Labeled("Item ID", FieldWithButton(_item, findItem)), Labeled("Reference ID", _reference), Labeled("Chance %", _chance),
                                Labeled("Loot mode", _lootMode), Labeled("Group", _groupId), Labeled("Minimum count", _minimumCount), Labeled("Maximum count", _maximumCount)
                            }
                        },
                        new Grid
                        {
                            ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8,
                            Children = { _questRequired, WithColumn(_comment, 1), WithColumn(remove, 2) }
                        }
                    }
                }
            };
        }

        public CreatureLootDraft Draft() => new(
            (uint)(_entry.Value ?? 0), (int)(_item.Value ?? 0), (int)(_reference.Value ?? 0), (float)(_chance.Value ?? 0),
            _questRequired.IsChecked == true, (ushort)(_lootMode.Value ?? 0), (byte)(_groupId.Value ?? 0),
            (byte)(_minimumCount.Value ?? 1), (byte)(_maximumCount.Value ?? 1), _comment.Text ?? string.Empty);
    }

    private static Control Labeled(string label, Control control) => new StackPanel
    {
        Margin = new Thickness(0, 0, 10, 6),
        Children = { new TextBlock { Text = label, Foreground = Brush.Parse("#9AA5B7") }, control }
    };
    private static Grid FieldWithButton(Control field, Control button) => new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7, Children = { field, WithColumn(button, 1) } };
}
