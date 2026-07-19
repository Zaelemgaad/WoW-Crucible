using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MySqlConnector;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ItemWorkbenchView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly ItemCreatorView _creator;
    private readonly TabControl _tabs;
    private readonly TextBox _host = new() { Text = "127.0.0.1", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _port = new() { Value = 3306, Minimum = 1, Maximum = 65535, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _user = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _password = new() { PasswordChar = '●', HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _database = new() { Text = "acore_world", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { Text = "Ready", Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly TextBox _search = new() { PlaceholderText = "Filter scan or pin exact IDs (17802, 17,802, #17 #17802)…" };
    private readonly ComboBox _classification = new()
    {
        ItemsSource = new[] { "No known acquisition path", "Known acquisition path", "All item_template rows" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox _reviewGroup = new()
    {
        ItemsSource = new[] { "All no-path review groups", "Other / manual review", "Deprecated / test / developer", "NPC / monster equipment" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox _inspectId = new() { PlaceholderText = "Inspect exact item ID…" };
    private readonly TextBox _acquisitionDbc = new() { PlaceholderText = "Optional server DBC folder for CharStartOutfit.dbc coverage…" };
    private readonly TextBox _favoriteMpq = new() { PlaceholderText = "Optional related MPQ path saved with favorites…" };
    private readonly ListBox _items = new();
    private readonly TextBlock _auditSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _inspection = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#AEB8C8")) };
    private ItemAcquisitionAudit? _audit;
    private bool _auditLoading;
    private string _auditIdentity = string.Empty;
    private readonly TextBox _cloneSource = new() { PlaceholderText = "Source item ID" };
    private readonly TextBox _cloneDestination = new() { PlaceholderText = "New item ID" };
    private readonly TextBox _cloneSuffix = new() { Text = " Variant" };
    private readonly TextBox _cloneSet = new() { PlaceholderText = "Keep existing set" };
    private readonly TextBlock _cloneResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _itemSetPath = new(); private readonly TextBox _schemaPath = new(); private readonly TextBox _spellPath = new();
    private readonly TextBox _setId = new() { PlaceholderText = "Set ID" }; private readonly TextBox _setOutput = new() { AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _newSetId = new() { PlaceholderText = "New set ID" }; private readonly TextBox _setMap = new() { AcceptsReturn = true, PlaceholderText = "oldItem:newItem, one pair per line or comma-separated" };
    private readonly TextBox _setSuffix = new() { Text = " Variant" }; private readonly TextBox _setCloneOutput = new() { PlaceholderText = "Output ItemSet.dbc" };
    private readonly TextBox _effects = new() { AcceptsReturn = true, PlaceholderText = "requiredItems:spellId, one bonus per line" }; private readonly TextBox _effectsOutput = new() { PlaceholderText = "Output ItemSet.dbc" };

    public event EventHandler? BackRequested;
    public event EventHandler? SqlStudioRequested;
    public event EventHandler? MpqWorkspaceRequested;
    public event EventHandler? ProjectWorkspaceRequested;
    public event EventHandler<SqlGuidedEditRequest>? FullSqlEditRequested;
    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public ItemWorkbenchView(DesktopWorkspaceSession session)
    {
        _session = session; _creator = new ItemCreatorView(session); _creator.ReferenceLookupRequested += (_, request) => ReferenceLookupRequested?.Invoke(this, request); _session.Changed += SessionChanged; LoadDefaults(); PopulateSessionConnection();
        _items.ItemTemplate = new FuncDataTemplate<ItemCatalogEntry>((item, _) =>
        {
            var panel = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(3, 3), ColumnSpacing = 8 };
            if (item is null) return panel;
            AddCell(panel, item.Entry.ToString("N0"), 0, FontWeight.SemiBold);
            var identity = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap } } };
            var explanation = item.HasKnownAcquisitionPath
                ? $"Known path · {string.Join("; ", item.AcquisitionSources.Take(2))}"
                : $"{ReviewGroupName(item.ReviewGroup)} · {item.NoPathReview.FirstOrDefault() ?? "No accepted acquisition evidence found."}";
            identity.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(item.HasKnownAcquisitionPath ? "#79B58A" : "#D4A45F")) });
            Grid.SetColumn(identity, 1); panel.Children.Add(identity);
            AddCell(panel, QualityName(item.Quality), 2); AddCell(panel, $"iLvl {item.ItemLevel}", 3); AddCell(panel, item.ItemSetId == 0 ? "No set" : $"Set {item.ItemSetId}", 4); return panel;
        });
        _items.SelectionChanged += (_, _) =>
        {
            if (_items.SelectedItem is not ItemCatalogEntry item) return;
            _cloneSource.Text = item.Entry.ToString(); _inspectId.Text = item.Entry.ToString();
            _cloneResult.Text = $"Selected {item.Entry:N0} — {item.Name}\nQuality: {QualityName(item.Quality)} · Item level: {item.ItemLevel} · Set: {(item.ItemSetId == 0 ? "none" : item.ItemSetId)}";
            ShowCatalogEvidence(item);
        };
        _search.TextChanged += (_, _) => ApplyAuditFilter();
        _search.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return; args.Handled = true;
            if (TryParseItemId(_search.Text, out var exactId)) { _inspectId.Text = exactId.ToString(); await InspectExactAsync(); }
            else if (_audit is null) await AuditAsync();
        };
        _classification.SelectionChanged += (_, _) =>
        {
            _reviewGroup.IsEnabled = _classification.SelectedIndex == 0;
            if (_classification.SelectedIndex != 0) _reviewGroup.SelectedIndex = 0;
            ApplyAuditFilter();
        };
        _reviewGroup.SelectionChanged += (_, _) => ApplyAuditFilter();

        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        var back = new Button { Content = "← Editor", HorizontalAlignment = HorizontalAlignment.Left }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var sqlStudio = AccentButton("Full SQL database / Favorites"); sqlStudio.Click += async (_, _) => await OpenSqlStudioAsync();
        var mpqMerge = new Button { Content = "MPQ patches / merge" }; mpqMerge.Click += (_, _) => MpqWorkspaceRequested?.Invoke(this, EventArgs.Empty);
        var titleActions = new WrapPanel { Children = { sqlStudio, mpqMerge } };
        root.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2B3445")), BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(12,8), Child = new WrapPanel { Children = { back, new TextBlock { Text = "ITEMS & SETS", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0) }, titleActions } } });
        var connection = ConnectionBar(); Grid.SetRow(connection, 1); root.Children.Add(connection);
        _tabs = new TabControl { Margin = new Thickness(12), Items = { new TabItem { Header = "Create / edit item", Content = _creator }, new TabItem { Header = "Unobtainable / cut items", Content = AcquisitionPage() }, new TabItem { Header = "Full item copy", Content = ClonePage() }, new TabItem { Header = "Item sets & effects", Content = ItemSetPage() } } };
        _tabs.SelectionChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex != 1 || _audit is not null || _auditLoading) return;
            if (CanQueryDatabase()) await AuditAsync(automatic: true);
            else _status.Text = "Connect the live world database above; this tab will then load the complete acquisition catalog automatically. Exact IDs can also be explained after connection.";
        };
        _host.TextChanged += (_, _) => InvalidateAuditIfTargetChanged();
        _user.TextChanged += (_, _) => InvalidateAuditIfTargetChanged();
        _database.TextChanged += (_, _) => InvalidateAuditIfTargetChanged();
        _port.ValueChanged += (_, _) => InvalidateAuditIfTargetChanged();
        _acquisitionDbc.TextChanged += (_, _) => InvalidateAuditIfTargetChanged();
        Grid.SetRow(_tabs, 2); root.Children.Add(_tabs);
        var statusBorder = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2B3445")), BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(14,7), Child = _status };
        Grid.SetRow(statusBorder, 3); root.Children.Add(statusBorder); Content = root;
    }

    public void OpenItemRow(IReadOnlyDictionary<string, object?> row) { _creator.LoadRow(row); _tabs.SelectedIndex = 0; _status.Text = "Loaded the selected SQL row into the decoded item editor. The SQL Studio row editor remains available for every custom/unknown column."; }

    private Control ConnectionBar()
    {
        var connect = AccentButton("Connect & share with SQL Studio"); connect.Click += async (_, _) => await ConnectDatabaseAsync(false);
        var bar = new Grid { ColumnDefinitions = new("*,*,*"), RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnSpacing = 14, RowSpacing = 7, Margin = new Thickness(12, 8) };
        var heading = new TextBlock { Text = "LIVE WORLD DATABASE", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#C58A2B")), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumnSpan(heading, 3); bar.Children.Add(heading);
        AddConnectionField(bar, "Host", _host, 1, 0); AddConnectionField(bar, "Port", _port, 1, 1); AddConnectionField(bar, "User", _user, 1, 2);
        AddConnectionField(bar, "Password", _password, 2, 0); AddConnectionField(bar, "Database", _database, 2, 1);
        var privacy = new TextBlock { Text = "Password remains in memory only.", Foreground = new SolidColorBrush(Color.Parse("#78859A")), FontSize = 10, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(privacy, 2); Grid.SetColumn(privacy, 2); bar.Children.Add(privacy);
        Grid.SetRow(connect, 3); Grid.SetColumnSpan(connect, 3); connect.HorizontalAlignment = HorizontalAlignment.Stretch; bar.Children.Add(connect);
        return new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2B3445")), BorderThickness = new Thickness(0,0,0,1), Child = bar };
    }

    private Control AcquisitionPage()
    {
        var audit = AccentButton("Scan acquisition paths"); audit.Click += async (_, _) => await AuditAsync();
        var findExact = new Button { Content = "Pin exact ID(s)" }; findExact.Click += async (_, _) => await InspectSearchExactAsync();
        var inspect = AccentButton("Explain exact ID"); inspect.Click += async (_, _) => await InspectExactAsync();
        var edit = new Button { Content = "Open selected in decoded editor" }; edit.Click += async (_, _) => await OpenSelectedItemAsync(false);
        var fullSql = new Button { Content = "Open complete SQL row" }; fullSql.Click += async (_, _) => await OpenSelectedInSqlAsync();
        var favorite = new Button { Content = "★ Favorite selected row" }; favorite.Click += async (_, _) => await OpenSelectedItemAsync(true);
        var browseDbc = new Button { Content = "DBC folder…" }; browseDbc.Click += async (_, _) => await PickFolderAsync(_acquisitionDbc, "Select the server DBC folder");
        var browseMpq = new Button { Content = "Related MPQ…" }; browseMpq.Click += async (_, _) => await PickFileAsync(_favoriteMpq, "Select an optional related MPQ", "*.mpq");
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), RowSpacing = 7, Margin = new Thickness(0,0,0,8) };
        header.Children.Add(audit);
        var searchRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7, Margin = new Thickness(10,0), Children = { _search, WithColumn(findExact, 1) } };
        Grid.SetColumn(searchRow, 1); header.Children.Add(searchRow); Grid.SetColumn(_auditSummary, 2); _auditSummary.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(_auditSummary);
        var classificationLabel = new TextBlock { Text = "Show", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(classificationLabel, 1); header.Children.Add(classificationLabel); Grid.SetRow(_classification, 1); Grid.SetColumn(_classification, 1); _classification.Margin = new Thickness(10,0); header.Children.Add(_classification);
        var groupLabel = new TextBlock { Text = "Review group", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(groupLabel, 2); header.Children.Add(groupLabel); Grid.SetRow(_reviewGroup, 2); Grid.SetColumn(_reviewGroup, 1); _reviewGroup.Margin = new Thickness(10,0); header.Children.Add(_reviewGroup);
        var rowActions = new WrapPanel { Children = { edit, fullSql, favorite } }; Grid.SetRow(rowActions, 1); Grid.SetColumnSpan(rowActions, 2);
        var exact = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6, Margin = new Thickness(0,0,0,8), Children = { _inspectId, WithColumn(inspect, 1), rowActions } };
        var paths = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6, Margin = new Thickness(0,0,0,8), Children = { _acquisitionDbc, WithColumn(browseDbc, 1), WithRow(_favoriteMpq, 1), WithRow(WithColumn(browseMpq, 1), 1) } };
        var note = new TextBlock { Text = "This tab scans automatically once a verified SQL profile is available. Every row shows why its path was accepted or rejected. “No known path” means no vendor, achievement, direct/reachable loot, usable quest reward/start item, character-start, profession/disenchant/fishing/spell-loot, or causally reachable Spell.dbc create-item source was found. The result deliberately includes NPC equipment, Monster entries, deprecated rows, tests, and developer items—not only polished cut player gear. Exact numeric search pins an existing row regardless of the current list filter. Custom scripts still require review.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#8995A9")), Margin = new Thickness(0,0,0,10) };
        return new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,*"), Margin = new Thickness(4), Children = { header, WithRow(exact, 1), WithRow(paths, 2), WithRow(note, 3), WithRow(_inspection, 4), WithRow(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _items }, 5) } };
    }

    private Control ClonePage()
    {
        var clone = AccentButton("Create full copy in database"); clone.Click += async (_, _) => await CloneAsync();
        var reserve = AccentButton("Reserve verified item ID"); reserve.Click += async (_, _) => await ReserveItemIdAsync();
        var projects = new Button { Content = "Projects & shared IDs" }; projects.Click += (_, _) => ProjectWorkspaceRequested?.Invoke(this, EventArgs.Empty);
        var form = Form(("Source item ID", _cloneSource), ("New item ID", _cloneDestination), ("Name suffix", _cloneSuffix), ("New item-set ID (optional)", _cloneSet));
        return new ScrollViewer { Content = new StackPanel { Spacing = 14, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Stretch, Children = { new TextBlock { Text = "Full item copying", FontSize = 22, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Copies every writable item_template column exposed by the current core—including unknown/custom columns—and matching locale rows. Existing destination IDs are refused rather than replaced. An active Crucible project can allocate the destination against both live item_template and Item.dbc before the copy.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#9AA5B7")) }, form, new WrapPanel { Children = { reserve, projects, clone } }, new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(6), BorderBrush = new SolidColorBrush(Color.Parse("#2B3548")), BorderThickness = new Thickness(1), Child = _cloneResult } } } };
    }

    private Control ItemSetPage()
    {
        var inspect = AccentButton("Inspect set with names"); inspect.Click += async (_, _) => await InspectSetAsync();
        var browseSet = new Button { Content = "Browse…" }; browseSet.Click += async (_, _) => await PickFileAsync(_itemSetPath, "ItemSet.dbc", "*.dbc");
        var browseSchema = new Button { Content = "Browse…" }; browseSchema.Click += async (_, _) => await PickFileAsync(_schemaPath, "WotLK schema", "*.xml");
        var browseSpell = new Button { Content = "Browse…" }; browseSpell.Click += async (_, _) => await PickFileAsync(_spellPath, "Spell.dbc", "*.dbc");
        var paths = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto,Auto") };
        AddPath(paths, 0, "ItemSet.dbc", _itemSetPath, browseSet); AddPath(paths, 1, "Schema XML", _schemaPath, browseSchema); AddPath(paths, 2, "Spell.dbc", _spellPath, browseSpell); AddPath(paths, 3, "Set ID", _setId, inspect);
        var clone = AccentButton("Clone complete set row"); clone.Click += async (_, _) => await CloneSetAsync();
        var browseClone = new Button { Content = "Output…" }; browseClone.Click += async (_, _) => await PickOutputAsync(_setCloneOutput, "ItemSet-clone.dbc");
        var cloneGrid = Form(("New set ID", _newSetId), ("Name suffix", _setSuffix), ("Item ID map", _setMap), ("Output", Row(_setCloneOutput, browseClone)));
        var applyEffects = AccentButton("Write set effects to output"); applyEffects.Click += async (_, _) => await ApplyEffectsAsync();
        var browseEffects = new Button { Content = "Output…" }; browseEffects.Click += async (_, _) => await PickOutputAsync(_effectsOutput, "ItemSet-effects.dbc");
        var effectsGrid = Form(("Bonuses", _effects), ("Output", Row(_effectsOutput, browseEffects)));
        var content = new StackPanel { Spacing = 13, Margin = new Thickness(14), Children = { paths, new Border { Padding = new Thickness(10), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = _setOutput } }, new TextBlock { Text = "Clone membership", FontSize = 17, FontWeight = FontWeight.SemiBold }, cloneGrid, clone, new TextBlock { Text = "Edit up to eight set bonuses", FontSize = 17, FontWeight = FontWeight.SemiBold }, effectsGrid, applyEffects } };
        return new ScrollViewer { Content = content };
    }

    private async Task AuditAsync(bool automatic = false)
    {
        if (_auditLoading) return;
        _auditLoading = true;
        SetBusy(automatic ? "Loading the complete acquisition catalog…" : "Scanning every known SQL and DBC acquisition source…");
        DesktopCrashLogger.Debug("ITEM", "acquisition-audit-start", ("host", _host.Text), ("database", _database.Text), ("dbc", _acquisitionDbc.Text), ("automatic", automatic));
        try
        {
            var profile = Profile();
            _audit = await new ItemCatalogService().AuditAsync(profile, EmptyNull(_acquisitionDbc.Text));
            _auditIdentity = AuditIdentity(profile);
            ApplyAuditFilter();
            var manual = _audit.NoKnownAcquisitionPath.Count(item => item.ReviewGroup == ItemAcquisitionReviewGroup.OtherManualReview);
            var legacy = _audit.NoKnownAcquisitionPath.Count(item => item.ReviewGroup == ItemAcquisitionReviewGroup.DeprecatedTestOrDeveloper);
            var npc = _audit.NoKnownAcquisitionPath.Count(item => item.ReviewGroup == ItemAcquisitionReviewGroup.NpcOrMonsterEquipment);
            _auditSummary.Text = $"{_audit.NoKnownAcquisitionPath.Count:N0} no path · {manual:N0} other/manual · {legacy:N0} deprecated/test/dev · {npc:N0} NPC/monster · {_audit.ObtainableItems:N0} known · {_audit.TotalItems:N0} total";
            _status.Text = $"Checked {_audit.CheckedSources.Count:N0} source families; {_audit.MissingSources.Count:N0} unavailable or unconfigured. Exact IDs are pinned even when another classification filter is selected.";
            DesktopCrashLogger.Debug("ITEM", "acquisition-audit-success", ("total_items", _audit.TotalItems), ("no_known_path", _audit.NoKnownAcquisitionPath.Count), ("checked_sources", _audit.CheckedSources.Count), ("missing_sources", _audit.MissingSources.Count));
        }
        catch (Exception exception) { await ErrorAsync("Acquisition audit failed", exception); }
        finally { _auditLoading = false; }
    }

    private async Task InspectExactAsync()
    {
        if (!TryParseItemId(_inspectId.Text, out var entry)) { _inspection.Text = "Enter a positive item ID. Plain, grouped, and #prefixed forms are accepted (17802, 17,802, or #17802)."; return; }
        SetBusy($"Locating exact item {entry:N0} in the complete acquisition catalog…");
        try
        {
            var profile = Profile();
            if (_audit is null || !_auditIdentity.Equals(AuditIdentity(profile), StringComparison.Ordinal)) await AuditAsync();
            if (_audit is null) return;
            var item = _audit.AllItems.FirstOrDefault(candidate => candidate.Entry == entry);
            if (item is null) { _items.ItemsSource = Array.Empty<ItemCatalogEntry>(); _inspection.Text = $"Item {entry:N0} does not exist in item_template."; _status.Text = "Exact item inspection complete."; return; }
            var evidence = item.HasKnownAcquisitionPath
                ? item.AcquisitionSources.Select(value => $"Accepted · {value}")
                : item.NoPathReview;
            _inspection.Text = $"{item.Entry:N0} — {item.Name}\nClassification: {(item.HasKnownAcquisitionPath ? "known acquisition path" : "NO KNOWN ACQUISITION PATH")}\nReview group: {ReviewGroupName(item.ReviewGroup)}\n" + string.Join("\n", evidence.Select(value => $"• {value}"));
            _items.ItemsSource = new[] { item }; _items.SelectedItem = item; _items.ScrollIntoView(item);
            _status.Text = $"Pinned item {entry:N0} across {_audit.CheckedSources.Count:N0} source families. It can now be opened, favorited, or edited.";
        }
        catch (Exception exception) { await ErrorAsync("Exact item inspection failed", exception); }
    }

    private async Task InspectSearchExactAsync()
    {
        var entries = ParseExactItemIds(_search.Text);
        if (entries.Count == 0)
        {
            _status.Text = "Enter one or more positive item IDs. Use spaces, semicolons, or # prefixes for a batch; a grouped single ID such as 17,802 remains valid.";
            return;
        }
        if (_audit is null) await AuditAsync();
        if (_audit is null) return;
        ShowPinnedExactItems(entries);
    }

    private void ShowCatalogEvidence(ItemCatalogEntry item)
    {
        var known = item.HasKnownAcquisitionPath;
        var evidence = known ? item.AcquisitionSources.Select(value => $"Accepted · {value}") : item.NoPathReview;
        _inspection.Text = $"{item.Entry:N0} — {item.Name}\nClassification: {(known ? "known acquisition path" : "NO KNOWN ACQUISITION PATH")}\nReview group: {ReviewGroupName(item.ReviewGroup)}\n" +
            string.Join("\n", evidence.Select(value => $"• {value}"));
    }

    private async Task OpenSelectedItemAsync(bool favorite)
    {
        if (_items.SelectedItem is not ItemCatalogEntry selected) { _status.Text = "Select an item row first."; return; }
        SetBusy($"Opening exact item_template row {selected.Entry:N0}…");
        try
        {
            var profile = Profile();
            var capabilities = _session.DatabaseCapabilities is { } active && _session.DatabaseProfile is { } connected &&
                connected.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) && connected.Port == profile.Port && connected.Database.Equals(profile.Database, StringComparison.OrdinalIgnoreCase)
                ? active : await new DatabaseCapabilityService().InspectAsync(profile);
            var table = capabilities.FindTable("item_template") ?? throw new NotSupportedException("The connected schema has no item_template table.");
            var entryColumn = table.Find("entry")?.Name ?? throw new NotSupportedException("item_template has no entry column.");
            var row = await new SqlWorkspaceService().ReadRowAsync(profile, table, new Dictionary<string, object?> { [entryColumn] = selected.Entry })
                ?? throw new InvalidOperationException($"Item {selected.Entry} disappeared before it could be opened.");
            if (favorite)
            {
                SqlFavoriteStore.Save(new(profile.Database, table.Name,
                    row.Key.ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase),
                    $"{selected.Name} · {row.Display}", "Favorited from the unobtainable/cut-item audit.", DateTimeOffset.UtcNow,
                    EmptyNull(_acquisitionDbc.Text), EmptyNull(_favoriteMpq.Text)));
                _status.Text = $"Favorited exact row {row.Display}. It is now available in SQL Studio → Favorites.";
                return;
            }
            _creator.LoadRow(row.Values); _tabs.SelectedIndex = 0; _status.Text = $"Loaded exact row {row.Display} into the decoded item editor. SQL Studio still exposes every raw/custom field.";
        }
        catch (Exception exception) { await ErrorAsync(favorite ? "Favorite failed" : "Decoded item open failed", exception); }
    }

    private async Task OpenSelectedInSqlAsync()
    {
        if (_items.SelectedItem is not ItemCatalogEntry selected) { _status.Text = "Select an item row first."; return; }
        SetBusy($"Opening complete item_template row {selected.Entry:N0}…");
        try
        {
            var profile = Profile();
            var capabilities = _session.DatabaseCapabilities is { } active && _session.DatabaseProfile is { } connected &&
                connected.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) && connected.Port == profile.Port && connected.Database.Equals(profile.Database, StringComparison.OrdinalIgnoreCase)
                ? active : await new DatabaseCapabilityService().InspectAsync(profile);
            if (_session.DatabaseProfile is null || !_session.DatabaseTested || !_session.DatabaseProfile.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) ||
                _session.DatabaseProfile.Port != profile.Port || !_session.DatabaseProfile.Database.Equals(profile.Database, StringComparison.OrdinalIgnoreCase))
                await _session.TestManualDatabaseAsync(profile);
            var table = capabilities.FindTable("item_template") ?? throw new NotSupportedException("The connected schema has no item_template table.");
            var entryColumn = table.Find("entry")?.Name ?? throw new NotSupportedException("item_template has no entry column.");
            var row = await new SqlWorkspaceService().ReadRowAsync(profile, table, new Dictionary<string, object?> { [entryColumn] = selected.Entry })
                ?? throw new InvalidOperationException($"Item {selected.Entry} disappeared before it could be opened.");
            FullSqlEditRequested?.Invoke(this, new(table.Name, row.Values));
        }
        catch (Exception exception) { await ErrorAsync("Complete SQL row open failed", exception); }
    }

    private void ApplyAuditFilter()
    {
        if (_audit is null) { _items.ItemsSource = Array.Empty<ItemCatalogEntry>(); _status.Text = CanQueryDatabase() ? "The acquisition catalog is loading, or press Scan acquisition paths to refresh it now." : "Connect the live world database above. This tab will then load automatically."; return; } var query = _search.Text?.Trim() ?? string.Empty;
        var exactIds = ParseExactItemIds(query);
        if (exactIds.Count > 1)
        {
            ShowPinnedExactItems(exactIds);
            return;
        }
        if (TryParseItemId(query, out var exactId))
        {
            var exact = _audit.AllItems.FirstOrDefault(item => item.Entry == exactId);
            if (exact is null)
            {
                _items.ItemsSource = Array.Empty<ItemCatalogEntry>();
                _status.Text = $"Exact item {exactId:N0} does not exist in item_template.";
                return;
            }
            if (exact.HasKnownAcquisitionPath)
            {
                _items.ItemsSource = new[] { exact }; _items.SelectedItem = exact; _items.ScrollIntoView(exact);
                _status.Text = $"Exact item {exactId:N0} has a KNOWN acquisition path: {string.Join("; ", exact.AcquisitionSources.Take(3))}. It is shown so its classification is never mistaken for a missing row.";
                return;
            }
            _items.ItemsSource = new[] { exact }; _items.SelectedItem = exact; _items.ScrollIntoView(exact);
            _status.Text = $"Exact item {exactId:N0} is classified NO KNOWN ACQUISITION PATH.";
            return;
        }
        var mode = _classification.SelectedIndex;
        IEnumerable<ItemCatalogEntry> rows = mode switch
        {
            1 => _audit.AllItems.Where(item => item.HasKnownAcquisitionPath),
            2 => _audit.AllItems,
            _ => _audit.NoKnownAcquisitionPath
        };
        if (mode == 0)
        {
            rows = _reviewGroup.SelectedIndex switch
            {
                1 => rows.Where(item => item.ReviewGroup == ItemAcquisitionReviewGroup.OtherManualReview),
                2 => rows.Where(item => item.ReviewGroup == ItemAcquisitionReviewGroup.DeprecatedTestOrDeveloper),
                3 => rows.Where(item => item.ReviewGroup == ItemAcquisitionReviewGroup.NpcOrMonsterEquipment),
                _ => rows
            };
        }
        if (query.Length > 0) rows = rows.Where(item => item.Entry.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || QualityName(item.Quality).Contains(query, StringComparison.OrdinalIgnoreCase) || item.ItemLevel.ToString().Contains(query) || item.ItemSetId.ToString().Contains(query));
        var result = rows.ToArray(); _items.ItemsSource = result;
        if (result.Length > 0) { _items.SelectedItem = result[0]; _items.ScrollIntoView(result[0]); }
        var label = mode switch { 1 => "known-path", 2 => "total", _ => "no-known-path" };
        var group = mode == 0 && _reviewGroup.SelectedIndex > 0 ? $" · {Convert.ToString(_reviewGroup.SelectedItem)}" : string.Empty;
        _status.Text = $"Showing {result.Length:N0} {label} item(s){group}. Numeric searches always show an existing exact row and state its classification.";
    }

    private void ShowPinnedExactItems(IReadOnlyList<uint> entries)
    {
        if (_audit is null) return;
        var requested = entries.Distinct().ToArray();
        var byId = _audit.AllItems.ToDictionary(item => item.Entry);
        var rows = requested.Where(byId.ContainsKey).Select(entry => byId[entry]).ToArray();
        var missing = requested.Where(entry => !byId.ContainsKey(entry)).ToArray();
        _items.ItemsSource = rows;
        if (rows.Length > 0)
        {
            _items.SelectedItem = rows[0];
            _items.ScrollIntoView(rows[0]);
            _inspectId.Text = rows[0].Entry.ToString();
        }
        var noPath = rows.Count(item => !item.HasKnownAcquisitionPath);
        var known = rows.Length - noPath;
        _status.Text = $"Pinned {rows.Length:N0} exact item row(s) above all catalog filters · {noPath:N0} no-known-path · {known:N0} known-path" +
            (missing.Length == 0 ? "." : $" · missing from item_template: {string.Join(", ", missing)}.");
        _inspection.Text = string.Join("\n\n", rows.Select(item =>
        {
            var evidence = item.HasKnownAcquisitionPath ? item.AcquisitionSources.Select(value => $"Accepted · {value}") : item.NoPathReview;
            return $"{item.Entry:N0} — {item.Name}\nClassification: {(item.HasKnownAcquisitionPath ? "known acquisition path" : "NO KNOWN ACQUISITION PATH")}\n• {string.Join("\n• ", evidence)}";
        }));
    }

    private static string ReviewGroupName(ItemAcquisitionReviewGroup group) => group switch
    {
        ItemAcquisitionReviewGroup.KnownAcquisition => "Known acquisition",
        ItemAcquisitionReviewGroup.DeprecatedTestOrDeveloper => "Deprecated / test / developer",
        ItemAcquisitionReviewGroup.NpcOrMonsterEquipment => "NPC / monster equipment",
        _ => "Other / manual review"
    };

    private static bool TryParseItemId(string? text, out uint entry)
    {
        var candidate = text?.Trim() ?? string.Empty;
        if (candidate.StartsWith('#')) candidate = candidate[1..].Trim();
        if (candidate.Length == 0 || candidate.Any(character => !char.IsDigit(character) && character is not ',' and not '_' and not ' ')) { entry = 0; return false; }
        candidate = candidate.Replace(",", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        return uint.TryParse(candidate, out entry) && entry > 0;
    }

    private static IReadOnlyList<uint> ParseExactItemIds(string? text)
    {
        var candidate = text?.Trim() ?? string.Empty;
        if (candidate.Length == 0 || candidate.Any(character => !char.IsDigit(character) && character is not ',' and not '_' and not ' ' and not '#' and not ';' and not '\r' and not '\n' and not '\t')) return [];
        var hardBatch = candidate.IndexOfAny([';', '\r', '\n']) >= 0 || candidate.Count(character => character == '#') > 1;
        var pieces = candidate.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(piece => piece.TrimStart('#').Replace("_", string.Empty, StringComparison.Ordinal)).Where(piece => piece.Length > 0).ToArray();
        if (pieces.Length == 0 || pieces.Any(piece => piece.Any(character => !char.IsDigit(character)))) return [];
        var looksGrouped = !hardBatch && pieces.Length > 1 && pieces[0].Length is >= 1 and <= 3 && pieces.Skip(1).All(piece => piece.Length == 3);
        if (looksGrouped && uint.TryParse(string.Concat(pieces), out var grouped) && grouped > 0) return [grouped];
        var result = new List<uint>();
        foreach (var piece in pieces)
        {
            if (!uint.TryParse(piece, out var entry) || entry == 0) return [];
            if (!result.Contains(entry)) result.Add(entry);
        }
        return result;
    }

    private async Task CloneAsync()
    {
        if (!uint.TryParse(_cloneSource.Text, out var source) || !uint.TryParse(_cloneDestination.Text, out var destination)) { _status.Text = "Enter valid source and new item IDs."; return; }
        uint? set = string.IsNullOrWhiteSpace(_cloneSet.Text) ? null : uint.TryParse(_cloneSet.Text, out var parsed) ? parsed : throw new FormatException("Item-set ID must be numeric.");
        SetBusy("Copying the complete item transactionally…"); DesktopCrashLogger.Debug("ITEM", "clone-start", ("source_id", source), ("destination_id", destination), ("item_set", set), ("database", _database.Text)); try { var result = await new ItemCatalogService().CloneAsync(Profile(), source, destination, _cloneSuffix.Text ?? string.Empty, set); _cloneResult.Text = $"Created {result.NewEntry:N0} — {result.NewName}\nCopied {result.CopiedColumns:N0} live-schema columns and {result.CopiedLocaleRows:N0} locale row(s).\nItem set: {(result.ItemSetId == 0 ? "none" : result.ItemSetId)}"; _status.Text = "Full item copy committed. Existing IDs were not replaced."; DesktopCrashLogger.Debug("ITEM", "clone-success", ("new_id", result.NewEntry), ("name", result.NewName), ("copied_columns", result.CopiedColumns), ("locale_rows", result.CopiedLocaleRows), ("item_set", result.ItemSetId)); }
        catch (Exception exception) { await ErrorAsync("Item copy failed", exception); }
    }

    private async Task ReserveItemIdAsync()
    {
        var project = _session.Settings.ActiveProjectPath;
        if (string.IsNullOrWhiteSpace(project)) { _status.Text = "Create or open a project first. Opening Projects & shared IDs…"; ProjectWorkspaceRequested?.Invoke(this, EventArgs.Empty); return; }
        try
        {
            if (!_session.DatabaseTested) await ConnectDatabaseAsync(false);
            if (!_session.DatabaseTested || _session.DatabaseProfile is null || _session.DatabaseCapabilities is null) return;
            SetBusy("Checking every live item_template and Item.dbc ID before reservation…");
            var dbc = EmptyNull(_acquisitionDbc.Text) ?? _session.Settings.CoreDbcPath; var schema = EmptyNull(_schemaPath.Text) ?? _session.Settings.SchemaDefinitionPath;
            var occupancy = await new ContentIdOccupancyService().InspectAsync(ContentIdDomain.Item, _session.DatabaseProfile, _session.DatabaseCapabilities, dbc, schema);
            _session.Settings.CoreDbcPath = dbc; _session.Settings.SchemaDefinitionPath = schema; _session.Settings.Save();
            var purpose = uint.TryParse(_cloneSource.Text, out var source) ? $"Full copy of item {source}" : "New item from Items & Sets";
            var result = CrucibleContentProjectService.ReserveVerifiedIds(project, occupancy, 1, null, purpose); var id = result.Reservation.Values.Single();
            _cloneDestination.Text = id.ToString(); _cloneResult.Text = $"Reserved item ID {id:N0} in {Path.GetFileName(project)}.\nThe ID was checked against live item_template, Item.dbc, and every earlier Item reservation. Review the fields, then create the copy.";
            _status.Text = $"Reserved collision-checked item ID {id:N0}; no SQL row has been written yet.";
        }
        catch (Exception exception) { await ErrorAsync("Item ID reservation failed", exception); }
    }

    private async Task InspectSetAsync()
    {
        if (!uint.TryParse(_setId.Text, out var id)) { _status.Text = "Enter a numeric set ID."; return; } SetBusy("Resolving item-set members and spell names…"); DesktopCrashLogger.Debug("ITEMSET", "inspect-start", ("set_id", id), ("dbc", _itemSetPath.Text));
        try
        {
            var itemSetPath = _itemSetPath.Text!; var schemaPath = _schemaPath.Text!; var spellPath = File.Exists(_spellPath.Text) ? _spellPath.Text : null;
            var set = await Task.Run(() => ItemSetDbcService.Inspect(itemSetPath, schemaPath, id, spellPath));
            IReadOnlyDictionary<uint, string> names = new Dictionary<uint, string>(); var namesUnavailable = false;
            if (!string.IsNullOrWhiteSpace(_user.Text) && !string.IsNullOrWhiteSpace(_password.Text))
            {
                try { names = await new ItemCatalogService().GetItemNamesAsync(Profile(), set.ItemIds); }
                catch (Exception exception) { DesktopCrashLogger.Log("Item-set member name lookup failed", exception); namesUnavailable = true; }
            }
            var members = string.Join("\n", set.ItemIds.Select(item => $"  {item:N0} — {names.GetValueOrDefault(item, "name unavailable")}"));
            _setOutput.Text = $"{set.Id} — {set.Name}\nMembers:\n{members}\n\nBonuses:\n" + string.Join("\n", set.Effects.Select(effect => $"  {effect.RequiredItems} pieces: {effect.SpellId:N0} — {effect.SpellName ?? "unknown spell"}"));
            _status.Text = $"Resolved {set.ItemIds.Count:N0} members and {set.Effects.Count:N0} set bonus(es).{(namesUnavailable ? " Database names were unavailable; IDs remain usable." : string.Empty)}";
            DesktopCrashLogger.Debug("ITEMSET", "inspect-success", ("set_id", set.Id), ("name", set.Name), ("members", set.ItemIds.Count), ("effects", set.Effects.Count), ("names_unavailable", namesUnavailable));
        }
        catch (Exception exception) { await ErrorAsync("Item-set inspection failed", exception); }
    }

    private async Task CloneSetAsync()
    {
        if (!uint.TryParse(_setId.Text, out var source) || !uint.TryParse(_newSetId.Text, out var target)) { _status.Text = "Enter numeric source and new set IDs."; return; }
        var itemSetPath = _itemSetPath.Text!; var schemaPath = _schemaPath.Text!; var output = _setCloneOutput.Text!; var suffix = _setSuffix.Text ?? string.Empty;
        DesktopCrashLogger.Debug("ITEMSET", "clone-start", ("source_set", source), ("target_set", target), ("output", output)); try { var map = ParsePairs(_setMap.Text); var result = await Task.Run(() => ItemSetDbcService.Clone(itemSetPath, schemaPath, output, source, target, map, suffix)); _status.Text = $"Cloned '{result.Name}' with {result.ItemIdMap.Count:N0} remapped member(s)."; DesktopCrashLogger.Debug("ITEMSET", "clone-success", ("target_set", target), ("name", result.Name), ("remapped_members", result.ItemIdMap.Count), ("output", output)); }
        catch (Exception exception) { await ErrorAsync("Item-set clone failed", exception); }
    }

    private async Task ApplyEffectsAsync()
    {
        if (!uint.TryParse(_setId.Text, out var set)) { _status.Text = "Enter a numeric set ID."; return; }
        var itemSetPath = _itemSetPath.Text!; var schemaPath = _schemaPath.Text!; var output = _effectsOutput.Text!;
        DesktopCrashLogger.Debug("ITEMSET", "effects-write-start", ("set_id", set), ("output", output)); try { var pairs = ParsePairs(_effects.Text).Select((pair, index) => new ItemSetEffect(index + 1, pair.Key, pair.Value, null)).ToArray(); await Task.Run(() => ItemSetDbcService.SetEffects(itemSetPath, schemaPath, output, set, pairs)); _status.Text = $"Wrote {pairs.Length:N0} set bonus slot(s) without modifying the source DBC."; DesktopCrashLogger.Debug("ITEMSET", "effects-write-success", ("set_id", set), ("slots", pairs.Length), ("output", output)); }
        catch (Exception exception) { await ErrorAsync("Item-set effect edit failed", exception); }
    }

    private DatabaseConnectionProfile Profile()
    {
        var password = _password.Text ?? string.Empty;
        var current = _session.DatabaseProfile;
        if (password.Length == 0 && current is not null && current.Host.Equals(_host.Text, StringComparison.OrdinalIgnoreCase) && current.Port == (uint)(_port.Value ?? 3306) && current.User.Equals(_user.Text, StringComparison.OrdinalIgnoreCase) && current.Database.Equals(_database.Text, StringComparison.OrdinalIgnoreCase)) password = current.Password;
        return new(_host.Text ?? "127.0.0.1", (uint)(_port.Value ?? 3306), _user.Text ?? string.Empty, password, _database.Text ?? string.Empty, MySqlSslMode.Preferred);
    }
    private bool CanQueryDatabase() => !string.IsNullOrWhiteSpace(_user.Text) && (!string.IsNullOrWhiteSpace(_password.Text) || _session.DatabaseProfile is not null);
    private string AuditIdentity(DatabaseConnectionProfile profile)
        => $"{profile.Host.ToUpperInvariant()}|{profile.Port}|{profile.User.ToUpperInvariant()}|{profile.Database.ToUpperInvariant()}|{NormalizeIdentityPath(_acquisitionDbc.Text)}";
    private static string NormalizeIdentityPath(string? path)
    {
        var value = path?.Trim() ?? string.Empty;
        if (value.Length == 0) return string.Empty;
        try { return Path.GetFullPath(value).ToUpperInvariant(); }
        catch { return value.ToUpperInvariant(); }
    }
    private void InvalidateAuditIfTargetChanged()
    {
        if (_audit is null || _auditLoading) return;
        if (_auditIdentity.Equals(AuditIdentity(Profile()), StringComparison.Ordinal)) return;
        _audit = null; _auditIdentity = string.Empty; _items.ItemsSource = Array.Empty<ItemCatalogEntry>(); _auditSummary.Text = "Target changed · refresh required";
        _status.Text = "The database or DBC target changed. Reopen this tab or press Scan acquisition paths to load fresh evidence.";
    }
    private async Task ConnectDatabaseAsync(bool openStudio)
    {
        try
        {
            var profile = Profile(); SetBusy($"Verifying {profile.User}@{profile.Host}:{profile.Port}/{profile.Database}…");
            await _session.TestManualDatabaseAsync(profile);
            _status.Text = $"Connected and shared with every Crucible SQL tool · {profile.Database} on {profile.Host}:{profile.Port}.";
            if (openStudio) SqlStudioRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) { await ErrorAsync("Database connection failed", exception); }
    }
    private async Task OpenSqlStudioAsync()
    {
        var profile = Profile(); var active = _session.DatabaseTested && _session.DatabaseProfile is { } connected &&
            connected.Host.Equals(profile.Host, StringComparison.OrdinalIgnoreCase) && connected.Port == profile.Port &&
            connected.User.Equals(profile.User, StringComparison.OrdinalIgnoreCase) && connected.Database.Equals(profile.Database, StringComparison.OrdinalIgnoreCase);
        if (active) SqlStudioRequested?.Invoke(this, EventArgs.Empty);
        else await ConnectDatabaseAsync(true);
    }
    private void LoadDefaults() { try { var path = CruciblePaths.SettingsFileForRead; if (!File.Exists(path)) return; using var json = JsonDocument.Parse(File.ReadAllText(path)); var root = json.RootElement; if (root.TryGetProperty("DatabaseHost", out var host)) _host.Text = host.GetString(); if (root.TryGetProperty("DatabasePort", out var port)) _port.Value = port.GetUInt32(); if (root.TryGetProperty("DatabaseUser", out var user)) _user.Text = user.GetString(); if (root.TryGetProperty("WorldDatabase", out var db)) _database.Text = db.GetString(); if (root.TryGetProperty("SchemaDefinitionPath", out var schema)) _schemaPath.Text = schema.GetString(); if (root.TryGetProperty("CoreDbcPath", out var dbc)) { var directory = dbc.GetString(); _acquisitionDbc.Text = directory; if (Directory.Exists(directory)) { _itemSetPath.Text = Path.Combine(directory, "ItemSet.dbc"); _spellPath.Text = Path.Combine(directory, "Spell.dbc"); } } } catch (Exception exception) { DesktopCrashLogger.Debug("SETTINGS", "item-workbench-defaults-load-failed", ("error", exception.Message)); } }
    private async Task PickFileAsync(TextBox target, string title, string pattern) { var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The item workspace is not attached to the main window."); var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }] }); var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) target.Text = path; }
    private async Task PickFolderAsync(TextBox target, string title) { var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The item workspace is not attached to the main window."); var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }); var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) target.Text = path; }
    private async Task PickOutputAsync(TextBox target, string name) { var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The item workspace is not attached to the main window."); var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name, FileTypeChoices = [new FilePickerFileType("DBC") { Patterns = ["*.dbc"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) target.Text = path; }
    private Task ErrorAsync(string title, Exception exception) { DesktopCrashLogger.Log(title, exception); _status.Text = $"{title}: {exception.Message}"; return Task.CompletedTask; }
    private void SetBusy(string text) => _status.Text = text;
    private static Dictionary<uint,uint> ParsePairs(string? text) => (text ?? string.Empty).Split([',','\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => value.Split(':', StringSplitOptions.TrimEntries)).ToDictionary(pair => uint.Parse(pair[0]), pair => uint.Parse(pair[1]));
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static Grid Form(params (string Label, Control Control)[] rows) { var grid = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", rows.Length))) }; for (var index=0; index<rows.Length; index++) { var label = new TextBlock { Text = rows[index].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,5,12,5) }; Grid.SetRow(label,index); grid.Children.Add(label); rows[index].Control.Margin = new Thickness(0,4); Grid.SetRow(rows[index].Control,index); Grid.SetColumn(rows[index].Control,1); grid.Children.Add(rows[index].Control); } return grid; }
    private static Grid Row(Control first, Control second) { var grid = new Grid { ColumnDefinitions = new("*,Auto") }; grid.Children.Add(first); Grid.SetColumn(second,1); second.Margin = new Thickness(6,0,0,0); grid.Children.Add(second); return grid; }
    private static void AddConnectionField(Grid grid, string label, Control input, int row, int column) { var field = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch }; field.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }); Grid.SetColumn(input, 1); field.Children.Add(input); Grid.SetRow(field, row); Grid.SetColumn(field, column); grid.Children.Add(field); }
    private static void AddPath(Grid grid, int row, string label, Control field, Control button) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text,row); grid.Children.Add(text); Grid.SetRow(field,row); Grid.SetColumn(field,1); field.Margin = new Thickness(6,3); grid.Children.Add(field); Grid.SetRow(button,row); Grid.SetColumn(button,2); grid.Children.Add(button); }
    private static T WithRow<T>(T control, int row) where T:Control { Grid.SetRow(control,row); return control; }
    private static T WithColumn<T>(T control, int column) where T:Control { Grid.SetColumn(control,column); return control; }
    private static void AddCell(Grid grid, string text, int column, FontWeight? weight = null) { var value = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = weight ?? FontWeight.Normal, Margin = new Thickness(4) }; Grid.SetColumn(value,column); grid.Children.Add(value); }
    private static string QualityName(int quality) => quality switch { 0=>"Poor",1=>"Common",2=>"Uncommon",3=>"Rare",4=>"Epic",5=>"Legendary",6=>"Artifact",7=>"Heirloom",_=>$"Quality {quality}" };
    private static string? EmptyNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void SessionChanged(object? sender, EventArgs e)
    {
        PopulateSessionConnection();
        InvalidateAuditIfTargetChanged();
        if (_tabs.SelectedIndex == 1 && _audit is null && !_auditLoading && CanQueryDatabase()) _ = AuditAsync(automatic: true);
    }
    private void PopulateSessionConnection()
    {
        var profile = _session.DatabaseProfile; if (profile is null) return;
        _host.Text = profile.Host; _port.Value = profile.Port; _user.Text = profile.User; _password.Text = profile.Password; _database.Text = profile.Database;
        _status.Text = _session.DatabaseTested ? $"Shared SQL session verified · {profile.Database} on {profile.Host}:{profile.Port}" : "Shared SQL profile loaded but not tested.";
    }
    public void Dispose()
    {
        _creator.Dispose();
        _session.Changed -= SessionChanged;
    }
}
