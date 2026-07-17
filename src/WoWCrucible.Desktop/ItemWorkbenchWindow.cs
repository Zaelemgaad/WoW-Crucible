using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MySqlConnector;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ItemWorkbenchWindow : Window
{
    private readonly TextBox _host = new() { Text = "127.0.0.1", Width = 125 };
    private readonly NumericUpDown _port = new() { Value = 3306, Minimum = 1, Maximum = 65535, Width = 82 };
    private readonly TextBox _user = new() { Width = 105 };
    private readonly TextBox _password = new() { PasswordChar = '●', Width = 110 };
    private readonly TextBox _database = new() { Text = "acore_world", Width = 130 };
    private readonly TextBlock _status = new() { Text = "Ready", Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly TextBox _search = new() { PlaceholderText = "Filter by ID, name, quality, level, or set ID…" };
    private readonly ListBox _items = new();
    private readonly TextBlock _auditSummary = new() { TextWrapping = TextWrapping.Wrap };
    private ItemAcquisitionAudit? _audit;
    private readonly TextBox _cloneSource = new() { PlaceholderText = "Source item ID" };
    private readonly TextBox _cloneDestination = new() { PlaceholderText = "New item ID" };
    private readonly TextBox _cloneSuffix = new() { Text = " Variant" };
    private readonly TextBox _cloneSet = new() { PlaceholderText = "Keep existing set" };
    private readonly TextBlock _cloneResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _itemSetPath = new(); private readonly TextBox _schemaPath = new(); private readonly TextBox _spellPath = new();
    private readonly TextBox _setId = new() { PlaceholderText = "Set ID" }; private readonly TextBox _setOutput = new() { AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _newSetId = new() { PlaceholderText = "New set ID" }; private readonly TextBox _setMap = new() { AcceptsReturn = true, Height = 78, PlaceholderText = "oldItem:newItem, one pair per line or comma-separated" };
    private readonly TextBox _setSuffix = new() { Text = " Variant" }; private readonly TextBox _setCloneOutput = new() { PlaceholderText = "Output ItemSet.dbc" };
    private readonly TextBox _effects = new() { AcceptsReturn = true, Height = 92, PlaceholderText = "requiredItems:spellId, one bonus per line" }; private readonly TextBox _effectsOutput = new() { PlaceholderText = "Output ItemSet.dbc" };

    public ItemWorkbenchWindow()
    {
        Title = "WoW Crucible — Items & Sets"; Width = 1240; Height = 820; MinWidth = 980; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        LoadDefaults();
        _items.ItemTemplate = new FuncDataTemplate<ItemCatalogEntry>((item, _) =>
        {
            var panel = new Grid { ColumnDefinitions = new("105,*,80,80,90"), Margin = new Thickness(3, 2) };
            AddCell(panel, item.Entry.ToString("N0"), 0, FontWeight.SemiBold); AddCell(panel, item.Name, 1); AddCell(panel, QualityName(item.Quality), 2); AddCell(panel, $"iLvl {item.ItemLevel}", 3); AddCell(panel, item.ItemSetId == 0 ? "No set" : $"Set {item.ItemSetId}", 4); return panel;
        });
        _items.SelectionChanged += (_, _) => { if (_items.SelectedItem is ItemCatalogEntry item) { _cloneSource.Text = item.Entry.ToString(); _cloneResult.Text = $"Selected {item.Entry:N0} — {item.Name}\nQuality: {QualityName(item.Quality)} · Item level: {item.ItemLevel} · Set: {(item.ItemSetId == 0 ? "none" : item.ItemSetId)}"; } };
        _search.TextChanged += (_, _) => ApplyAuditFilter();

        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        root.Children.Add(ConnectionBar());
        var tabs = new TabControl { Margin = new Thickness(12), Items = { new TabItem { Header = "Unobtainable / cut items", Content = AcquisitionPage() }, new TabItem { Header = "Full item copy", Content = ClonePage() }, new TabItem { Header = "Item sets & effects", Content = ItemSetPage() } } };
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var statusBorder = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2B3445")), BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(14,7), Child = _status };
        Grid.SetRow(statusBorder, 2); root.Children.Add(statusBorder); Content = root;
    }

    private Control ConnectionBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new TextBlock { Text = "LIVE WORLD DATABASE", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#C58A2B")), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) });
        foreach (var pair in new[] { ("Host", (Control)_host), ("Port", _port), ("User", _user), ("Password", _password), ("Database", _database) }) { bar.Children.Add(new TextBlock { Text = pair.Item1, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }); bar.Children.Add(pair.Item2); }
        bar.Children.Add(new TextBlock { Text = "Password remains in memory only.", Foreground = new SolidColorBrush(Color.Parse("#78859A")), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,0,0) }); return new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2B3445")), BorderThickness = new Thickness(0,0,0,1), Child = bar };
    }

    private Control AcquisitionPage()
    {
        var audit = AccentButton("Scan acquisition paths"); audit.Click += async (_, _) => await AuditAsync();
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(0,0,0,10) }; header.Children.Add(audit); Grid.SetColumn(_search, 1); _search.Margin = new Thickness(10,0); header.Children.Add(_search); Grid.SetColumn(_auditSummary, 2); _auditSummary.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(_auditSummary);
        var note = new TextBlock { Text = "“No known path” means no vendor, loot, quest reward/choice, starting-item, prospecting, milling, disenchanting, fishing, or spell-loot source was found in the live schema. Script-created items still require review.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#8995A9")), Margin = new Thickness(0,0,0,10) };
        return new Grid { RowDefinitions = new("Auto,Auto,*"), Margin = new Thickness(4), Children = { header, WithRow(note, 1), WithRow(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _items }, 2) } };
    }

    private Control ClonePage()
    {
        var clone = AccentButton("Create full copy in database"); clone.Click += async (_, _) => await CloneAsync();
        var form = Form(("Source item ID", _cloneSource), ("New item ID", _cloneDestination), ("Name suffix", _cloneSuffix), ("New item-set ID (optional)", _cloneSet));
        return new StackPanel { Spacing = 14, Margin = new Thickness(20), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Children = { new TextBlock { Text = "Full item copying", FontSize = 22, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Copies every writable item_template column exposed by the current core—including unknown/custom columns—and matching locale rows. Existing destination IDs are refused rather than replaced.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#9AA5B7")) }, form, clone, new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(6), BorderBrush = new SolidColorBrush(Color.Parse("#2B3548")), BorderThickness = new Thickness(1), Child = _cloneResult } } };
    }

    private Control ItemSetPage()
    {
        var inspect = AccentButton("Inspect set with names"); inspect.Click += async (_, _) => await InspectSetAsync();
        var browseSet = new Button { Content = "Browse…" }; browseSet.Click += async (_, _) => await PickFileAsync(_itemSetPath, "ItemSet.dbc", "*.dbc");
        var browseSchema = new Button { Content = "Browse…" }; browseSchema.Click += async (_, _) => await PickFileAsync(_schemaPath, "WotLK schema", "*.xml");
        var browseSpell = new Button { Content = "Browse…" }; browseSpell.Click += async (_, _) => await PickFileAsync(_spellPath, "Spell.dbc", "*.dbc");
        var paths = new Grid { ColumnDefinitions = new("150,*,Auto"), RowDefinitions = new("Auto,Auto,Auto,Auto") };
        AddPath(paths, 0, "ItemSet.dbc", _itemSetPath, browseSet); AddPath(paths, 1, "Schema XML", _schemaPath, browseSchema); AddPath(paths, 2, "Spell.dbc", _spellPath, browseSpell); AddPath(paths, 3, "Set ID", _setId, inspect);
        var clone = AccentButton("Clone complete set row"); clone.Click += async (_, _) => await CloneSetAsync();
        var browseClone = new Button { Content = "Output…" }; browseClone.Click += async (_, _) => await PickOutputAsync(_setCloneOutput, "ItemSet-clone.dbc");
        var cloneGrid = Form(("New set ID", _newSetId), ("Name suffix", _setSuffix), ("Item ID map", _setMap), ("Output", Row(_setCloneOutput, browseClone)));
        var applyEffects = AccentButton("Write set effects to output"); applyEffects.Click += async (_, _) => await ApplyEffectsAsync();
        var browseEffects = new Button { Content = "Output…" }; browseEffects.Click += async (_, _) => await PickOutputAsync(_effectsOutput, "ItemSet-effects.dbc");
        var effectsGrid = Form(("Bonuses", _effects), ("Output", Row(_effectsOutput, browseEffects)));
        var content = new StackPanel { Spacing = 13, Margin = new Thickness(14), Children = { paths, new Border { Height = 180, Padding = new Thickness(10), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = _setOutput } }, new TextBlock { Text = "Clone membership", FontSize = 17, FontWeight = FontWeight.SemiBold }, cloneGrid, clone, new TextBlock { Text = "Edit up to eight set bonuses", FontSize = 17, FontWeight = FontWeight.SemiBold }, effectsGrid, applyEffects } };
        return new ScrollViewer { Content = content };
    }

    private async Task AuditAsync()
    {
        SetBusy("Scanning every known acquisition source…"); try { _audit = await new ItemCatalogService().AuditAsync(Profile()); ApplyAuditFilter(); _auditSummary.Text = $"{_audit.NoKnownAcquisitionPath.Count:N0} / {_audit.TotalItems:N0} no known path"; _status.Text = $"Checked {_audit.CheckedSources.Count:N0} source families; {_audit.MissingSources.Count:N0} unavailable in this schema."; }
        catch (Exception exception) { await ErrorAsync("Acquisition audit failed", exception); }
    }

    private void ApplyAuditFilter()
    {
        if (_audit is null) return; var query = _search.Text?.Trim() ?? string.Empty; IEnumerable<ItemCatalogEntry> rows = _audit.NoKnownAcquisitionPath;
        if (query.Length > 0) rows = rows.Where(item => item.Entry.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || QualityName(item.Quality).Contains(query, StringComparison.OrdinalIgnoreCase) || item.ItemLevel.ToString().Contains(query) || item.ItemSetId.ToString().Contains(query));
        var result = rows.Take(100_000).ToArray(); _items.ItemsSource = result; _status.Text = $"Showing {result.Length:N0} no-known-path item(s).";
    }

    private async Task CloneAsync()
    {
        if (!uint.TryParse(_cloneSource.Text, out var source) || !uint.TryParse(_cloneDestination.Text, out var destination)) { _status.Text = "Enter valid source and new item IDs."; return; }
        uint? set = string.IsNullOrWhiteSpace(_cloneSet.Text) ? null : uint.TryParse(_cloneSet.Text, out var parsed) ? parsed : throw new FormatException("Item-set ID must be numeric.");
        SetBusy("Copying the complete item transactionally…"); try { var result = await new ItemCatalogService().CloneAsync(Profile(), source, destination, _cloneSuffix.Text ?? string.Empty, set); _cloneResult.Text = $"Created {result.NewEntry:N0} — {result.NewName}\nCopied {result.CopiedColumns:N0} live-schema columns and {result.CopiedLocaleRows:N0} locale row(s).\nItem set: {(result.ItemSetId == 0 ? "none" : result.ItemSetId)}"; _status.Text = "Full item copy committed. Existing IDs were not replaced."; }
        catch (Exception exception) { await ErrorAsync("Item copy failed", exception); }
    }

    private async Task InspectSetAsync()
    {
        if (!uint.TryParse(_setId.Text, out var id)) { _status.Text = "Enter a numeric set ID."; return; } SetBusy("Resolving item-set members and spell names…");
        try
        {
            var set = await Task.Run(() => ItemSetDbcService.Inspect(_itemSetPath.Text!, _schemaPath.Text!, id, File.Exists(_spellPath.Text) ? _spellPath.Text : null));
            IReadOnlyDictionary<uint, string> names = new Dictionary<uint, string>(); var namesUnavailable = false;
            if (!string.IsNullOrWhiteSpace(_user.Text) && !string.IsNullOrWhiteSpace(_password.Text))
            {
                try { names = await new ItemCatalogService().GetItemNamesAsync(Profile(), set.ItemIds); }
                catch (Exception exception) { DesktopCrashLogger.Log("Item-set member name lookup failed", exception); namesUnavailable = true; }
            }
            var members = string.Join("\n", set.ItemIds.Select(item => $"  {item:N0} — {names.GetValueOrDefault(item, "name unavailable")}"));
            _setOutput.Text = $"{set.Id} — {set.Name}\nMembers:\n{members}\n\nBonuses:\n" + string.Join("\n", set.Effects.Select(effect => $"  {effect.RequiredItems} pieces: {effect.SpellId:N0} — {effect.SpellName ?? "unknown spell"}"));
            _status.Text = $"Resolved {set.ItemIds.Count:N0} members and {set.Effects.Count:N0} set bonus(es).{(namesUnavailable ? " Database names were unavailable; IDs remain usable." : string.Empty)}";
        }
        catch (Exception exception) { await ErrorAsync("Item-set inspection failed", exception); }
    }

    private async Task CloneSetAsync()
    {
        if (!uint.TryParse(_setId.Text, out var source) || !uint.TryParse(_newSetId.Text, out var target)) { _status.Text = "Enter numeric source and new set IDs."; return; }
        try { var map = ParsePairs(_setMap.Text); var result = await Task.Run(() => ItemSetDbcService.Clone(_itemSetPath.Text!, _schemaPath.Text!, _setCloneOutput.Text!, source, target, map, _setSuffix.Text ?? string.Empty)); _status.Text = $"Cloned '{result.Name}' with {result.ItemIdMap.Count:N0} remapped member(s)."; }
        catch (Exception exception) { await ErrorAsync("Item-set clone failed", exception); }
    }

    private async Task ApplyEffectsAsync()
    {
        if (!uint.TryParse(_setId.Text, out var set)) { _status.Text = "Enter a numeric set ID."; return; }
        try { var pairs = ParsePairs(_effects.Text).Select((pair, index) => new ItemSetEffect(index + 1, pair.Key, pair.Value, null)).ToArray(); await Task.Run(() => ItemSetDbcService.SetEffects(_itemSetPath.Text!, _schemaPath.Text!, _effectsOutput.Text!, set, pairs)); _status.Text = $"Wrote {pairs.Length:N0} set bonus slot(s) without modifying the source DBC."; }
        catch (Exception exception) { await ErrorAsync("Item-set effect edit failed", exception); }
    }

    private DatabaseConnectionProfile Profile() => new(_host.Text ?? "127.0.0.1", (uint)(_port.Value ?? 3306), _user.Text ?? string.Empty, _password.Text ?? string.Empty, _database.Text ?? string.Empty, MySqlSslMode.Preferred);
    private void LoadDefaults() { try { var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWCrucible", "settings.json"); if (!File.Exists(path)) return; using var json = JsonDocument.Parse(File.ReadAllText(path)); var root = json.RootElement; if (root.TryGetProperty("DatabaseHost", out var host)) _host.Text = host.GetString(); if (root.TryGetProperty("DatabasePort", out var port)) _port.Value = port.GetUInt32(); if (root.TryGetProperty("DatabaseUser", out var user)) _user.Text = user.GetString(); if (root.TryGetProperty("WorldDatabase", out var db)) _database.Text = db.GetString(); if (root.TryGetProperty("SchemaDefinitionPath", out var schema)) _schemaPath.Text = schema.GetString(); if (root.TryGetProperty("CoreDbcPath", out var dbc)) { var directory = dbc.GetString(); if (Directory.Exists(directory)) { _itemSetPath.Text = Path.Combine(directory, "ItemSet.dbc"); _spellPath.Text = Path.Combine(directory, "Spell.dbc"); } } } catch { } }
    private async Task PickFileAsync(TextBox target, string title, string pattern) { var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }] }); var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) target.Text = path; }
    private async Task PickOutputAsync(TextBox target, string name) { var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name, FileTypeChoices = [new FilePickerFileType("DBC") { Patterns = ["*.dbc"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) target.Text = path; }
    private async Task ErrorAsync(string title, Exception exception) { DesktopCrashLogger.Log(title, exception); _status.Text = exception.Message; var window = new Window { Title = title, Width = 560, Height = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => window.Close(); window.Content = new StackPanel { Margin = new Thickness(22), Spacing = 14, Children = { new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap }, close } }; await window.ShowDialog(this); }
    private void SetBusy(string text) => _status.Text = text;
    private static Dictionary<uint,uint> ParsePairs(string? text) => (text ?? string.Empty).Split([',','\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => value.Split(':', StringSplitOptions.TrimEntries)).ToDictionary(pair => uint.Parse(pair[0]), pair => uint.Parse(pair[1]));
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static Grid Form(params (string Label, Control Control)[] rows) { var grid = new Grid { ColumnDefinitions = new("190,*"), RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", rows.Length))) }; for (var index=0; index<rows.Length; index++) { var label = new TextBlock { Text = rows[index].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,5,12,5) }; Grid.SetRow(label,index); grid.Children.Add(label); rows[index].Control.Margin = new Thickness(0,4); Grid.SetRow(rows[index].Control,index); Grid.SetColumn(rows[index].Control,1); grid.Children.Add(rows[index].Control); } return grid; }
    private static Grid Row(Control first, Control second) { var grid = new Grid { ColumnDefinitions = new("*,Auto") }; grid.Children.Add(first); Grid.SetColumn(second,1); second.Margin = new Thickness(6,0,0,0); grid.Children.Add(second); return grid; }
    private static void AddPath(Grid grid, int row, string label, Control field, Control button) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text,row); grid.Children.Add(text); Grid.SetRow(field,row); Grid.SetColumn(field,1); field.Margin = new Thickness(6,3); grid.Children.Add(field); Grid.SetRow(button,row); Grid.SetColumn(button,2); grid.Children.Add(button); }
    private static T WithRow<T>(T control, int row) where T:Control { Grid.SetRow(control,row); return control; }
    private static void AddCell(Grid grid, string text, int column, FontWeight weight = default) { var value = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = weight, Margin = new Thickness(4) }; Grid.SetColumn(value,column); grid.Children.Add(value); }
    private static string QualityName(int quality) => quality switch { 0=>"Poor",1=>"Common",2=>"Uncommon",3=>"Rare",4=>"Epic",5=>"Legendary",6=>"Artifact",7=>"Heirloom",_=>$"Quality {quality}" };
}
