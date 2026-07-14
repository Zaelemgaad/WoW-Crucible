using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class ItemCreatorForm : Form
{
    private readonly DatabaseConnectionProfile? _profile;
    private readonly DatabaseTableCapability _table;
    private readonly NumericUpDown _entry = Number(1, uint.MaxValue);
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, PlaceholderText = "New custom item" };
    private readonly ComboBox _class = Choice((0, "Consumable"), (1, "Container"), (2, "Weapon"), (3, "Gem"), (4, "Armor"), (5, "Reagent"), (6, "Projectile"), (7, "Trade Goods"), (9, "Recipe"), (11, "Quiver"), (12, "Quest"), (13, "Key"), (15, "Miscellaneous"), (16, "Glyph"));
    private readonly ComboBox _subclass = Choice((0, "Generic"));
    private readonly NumericUpDown _display = Number(0, uint.MaxValue);
    private readonly ComboBox _quality = Choice((0, "Poor"), (1, "Common"), (2, "Uncommon"), (3, "Rare"), (4, "Epic"), (5, "Legendary"), (6, "Artifact"), (7, "Heirloom"));
    private readonly ComboBox _inventory = Choice((0, "Non-equippable"), (1, "Head"), (2, "Neck"), (3, "Shoulder"), (4, "Shirt"), (5, "Chest"), (6, "Waist"), (7, "Legs"), (8, "Feet"), (9, "Wrist"), (10, "Hands"), (11, "Finger"), (12, "Trinket"), (13, "One-hand weapon"), (14, "Shield"), (15, "Ranged"), (16, "Back"), (17, "Two-hand weapon"), (18, "Bag"), (19, "Tabard"), (20, "Robe"), (21, "Main-hand weapon"), (22, "Off-hand weapon"), (23, "Held in off-hand"), (24, "Ammo"), (25, "Thrown"), (26, "Ranged right"), (27, "Quiver"), (28, "Relic"));
    private readonly NumericUpDown _itemLevel = Number(1, 1000);
    private readonly NumericUpDown _requiredLevel = Number(0, 255);
    private readonly NumericUpDown _buy = Number(0, uint.MaxValue);
    private readonly NumericUpDown _sell = Number(0, uint.MaxValue);
    private readonly ComboBox _bonding = Choice((0, "No binding"), (1, "Bind on pickup"), (2, "Bind on equip"), (3, "Bind on use"), (4, "Quest item"));
    private readonly NumericUpDown _flags = Number(0, uint.MaxValue);
    private readonly NumericUpDown _armor = DecimalNumber(0, 100000);
    private readonly NumericUpDown _damageMin = DecimalNumber(0, 100000);
    private readonly NumericUpDown _damageMax = DecimalNumber(0, 100000);
    private readonly NumericUpDown _delay = Number(0, 10000);
    private readonly NumericUpDown _durability = Number(0, 100000);
    private readonly ComboBox[] _statTypes = Enumerable.Range(0, 10).Select(_ => StatChoice()).ToArray();
    private readonly NumericUpDown[] _statValues = Enumerable.Range(0, 10).Select(_ => Number(-100000, 100000)).ToArray();
    private readonly NumericUpDown[] _spellIds = Enumerable.Range(0, 5).Select(_ => Number(0, int.MaxValue)).ToArray();
    private readonly ComboBox[] _spellTriggers = Enumerable.Range(0, 5).Select(_ => SpellTriggerChoice()).ToArray();
    private readonly NumericUpDown[] _spellCharges = Enumerable.Range(0, 5).Select(_ => Number(short.MinValue, short.MaxValue)).ToArray();
    private readonly NumericUpDown[] _spellPpm = Enumerable.Range(0, 5).Select(_ => DecimalNumber(0, 1000)).ToArray();
    private readonly NumericUpDown[] _spellCooldowns = Enumerable.Range(0, 5).Select(_ => Number(-1, int.MaxValue, -1)).ToArray();
    private readonly NumericUpDown[] _spellCategories = Enumerable.Range(0, 5).Select(_ => Number(0, ushort.MaxValue)).ToArray();
    private readonly NumericUpDown[] _spellCategoryCooldowns = Enumerable.Range(0, 5).Select(_ => Number(-1, int.MaxValue, -1)).ToArray();
    private readonly TextBox _description = new() { Dock = DockStyle.Fill, Multiline = true, Height = 55 };
    private readonly TextBox _preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9), WordWrap = false };
    private readonly ItemTooltipControl _tooltip = new() { Dock = DockStyle.Fill };
    private readonly ToolTip _help = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 100 };

    public ItemCreatorForm(DatabaseConnectionProfile? profile, DatabaseCapabilities capabilities)
    {
        _profile = profile; _table = capabilities.FindTable("item_template") ?? throw new NotSupportedException("The connected database has no item_template table.");
        Text = profile is null ? "Guided Item Creator — Offline Authoring" : $"Guided Item Creator — {capabilities.Database}"; Width = 1320; Height = 820; StartPosition = FormStartPosition.CenterParent; MinimumSize = new(1050, 700);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Basics", ("Entry ID", _entry), ("Name", _name), ("Item class", _class), ("Subclass", _subclass), ("Display ID", _display), ("Quality", _quality), ("Inventory slot", _inventory), ("Item level", _itemLevel), ("Required level", _requiredLevel), ("Binding", _bonding), ("Description", _description)));
        tabs.TabPages.Add(Page("Combat and value", ("Buy price (copper)", _buy), ("Sell price (copper)", _sell), ("Armor", _armor), ("Minimum damage", _damageMin), ("Maximum damage", _damageMax), ("Weapon delay (ms)", _delay), ("Durability", _durability), ("Raw flags", _flags)));
        tabs.TabPages.Add(StatsPage());
        tabs.TabPages.Add(SpellEffectsPage());
        var previewPage = new TabPage("SQL preview"); previewPage.Controls.Add(_preview); tabs.TabPages.Add(previewPage);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(8) };
        var preview = new Button { Text = "Refresh SQL Preview", AutoSize = true }; var export = new Button { Text = "Export SQL…", AutoSize = true }; var insert = new Button { Text = profile is null ? "Connect a Server to Insert" : "Insert into Database…", AutoSize = true, Enabled = profile is not null };
        buttons.Controls.Add(preview); buttons.Controls.Add(export); buttons.Controls.Add(insert); buttons.Controls.Add(new Label { Text = profile is null ? "Offline common-schema preview — connect a target before deployment" : $"Live schema: {_table.Columns.Count} columns detected", AutoSize = true, Margin = new(16, 8, 0, 0), ForeColor = Color.DimGray });
        preview.Click += (_, _) => { if (TryPlan(out var plan)) { _preview.Text = Preview(plan!); tabs.SelectedTab = previewPage; } };
        export.Click += (_, _) => Export(); insert.Click += async (_, _) => await InsertAsync(insert);
        var tooltipPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 29, 38), RowCount = 2, Padding = new(12) };
        tooltipPanel.RowStyles.Add(new(SizeType.AutoSize)); tooltipPanel.RowStyles.Add(new(SizeType.Percent, 100));
        tooltipPanel.Controls.Add(new Label { Text = "LIVE IN-GAME TOOLTIP PREVIEW", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.FromArgb(190, 190, 205), Margin = new(8, 4, 0, 8) }, 0, 0); tooltipPanel.Controls.Add(_tooltip, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(tabs); split.Panel2.Controls.Add(tooltipPanel); Controls.Add(split); Controls.Add(buttons);
        Shown += (_, _) => { split.Panel1MinSize = 570; split.Panel2MinSize = 360; split.SplitterDistance = Math.Clamp(split.Width - 440, split.Panel1MinSize, split.Width - split.Panel2MinSize); };
        _class.SelectedValueChanged += (_, _) => { UpdateSubclassChoices(); UpdateTooltip(); };
        RegisterPreviewEvents(tabs); UpdateSubclassChoices(); UpdateTooltip();
        FormClosed += (_, _) => _help.Dispose();
    }

    private ItemWritePlan Plan() => ItemTemplateAdapter.CreatePlan(Draft(), _table);

    private ItemDraft Draft() => new(
        (uint)_entry.Value, _name.Text, Selected(_class), Selected(_subclass), (uint)_display.Value, Selected(_quality), Selected(_inventory),
        (uint)_itemLevel.Value, (uint)_requiredLevel.Value, (uint)_buy.Value, (uint)_sell.Value, (uint)Selected(_bonding), (uint)_flags.Value,
        (float)_armor.Value, (float)_damageMin.Value, (float)_damageMax.Value, (uint)_delay.Value, (uint)_durability.Value, _description.Text,
        _statTypes.Select((type, index) => new ItemStatDraft(Selected(type), (int)_statValues[index].Value)).ToArray(),
        _spellIds.Select((id, index) => new ItemSpellDraft((int)id.Value, Selected(_spellTriggers[index]), (int)_spellCharges[index].Value, (float)_spellPpm[index].Value, (int)_spellCooldowns[index].Value, (int)_spellCategories[index].Value, (int)_spellCategoryCooldowns[index].Value)).ToArray());

    private bool TryPlan(out ItemWritePlan? plan) { try { plan = Plan(); return true; } catch (Exception ex) { plan = null; MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; } }
    private string Preview(ItemWritePlan plan) => plan.PreviewSql() + (plan.OmittedFields.Count == 0 ? "" : $"\r\n\r\n-- Fields omitted because this server schema does not expose them: {string.Join(", ", plan.OmittedFields)}");
    private void Export() { if (!TryPlan(out var plan)) return; using var dialog = new SaveFileDialog { Filter = "SQL files (*.sql)|*.sql", FileName = $"item-{_entry.Value}.sql" }; if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, Preview(plan!) + Environment.NewLine); }
    private async Task InsertAsync(Button button)
    {
        if (_profile is null) return;
        if (!TryPlan(out var plan)) return;
        _preview.Text = Preview(plan!);
        if (MessageBox.Show(this, $"Insert item {_entry.Value} '{_name.Text}' into {_profile.Database}.item_template?\n\nExisting IDs are never replaced.", "Confirm database write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { button.Enabled = false; await new ItemTemplateService().InsertAsync(_profile, plan!); MessageBox.Show(this, "Item inserted successfully. Restart or reload the world server as required by your core.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { button.Enabled = true; }
    }

    private void UpdateTooltip() => _tooltip.ShowItem(Draft(), SelectedName(_class), SelectedName(_subclass), SelectedName(_inventory));

    private void RegisterPreviewEvents(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is NumericUpDown number) number.ValueChanged += (_, _) => UpdateTooltip();
            else if (control is ComboBox combo) combo.SelectedValueChanged += (_, _) => UpdateTooltip();
            else if (control is TextBox text && !ReferenceEquals(text, _preview)) text.TextChanged += (_, _) => UpdateTooltip();
            if (control.HasChildren) RegisterPreviewEvents(control);
        }
    }

    private void UpdateSubclassChoices()
    {
        var choices = Selected(_class) switch
        {
            0 => Values((0, "Consumable"), (1, "Potion"), (2, "Elixir"), (3, "Flask"), (4, "Scroll"), (5, "Food & Drink"), (6, "Item Enhancement"), (7, "Bandage")),
            1 => Values((0, "Bag"), (1, "Soul Bag"), (2, "Herb Bag"), (3, "Enchanting Bag"), (4, "Engineering Bag"), (5, "Gem Bag"), (6, "Mining Bag"), (7, "Leatherworking Bag"), (8, "Inscription Bag")),
            2 => Values((0, "One-Handed Axe"), (1, "Two-Handed Axe"), (2, "Bow"), (3, "Gun"), (4, "One-Handed Mace"), (5, "Two-Handed Mace"), (6, "Polearm"), (7, "One-Handed Sword"), (8, "Two-Handed Sword"), (10, "Staff"), (13, "Fist Weapon"), (15, "Dagger"), (16, "Thrown"), (18, "Crossbow"), (19, "Wand"), (20, "Fishing Pole")),
            3 => Values((0, "Red Gem"), (1, "Blue Gem"), (2, "Yellow Gem"), (3, "Purple Gem"), (4, "Green Gem"), (5, "Orange Gem"), (6, "Meta Gem"), (7, "Simple Gem"), (8, "Prismatic Gem")),
            4 => Values((0, "Miscellaneous Armor"), (1, "Cloth"), (2, "Leather"), (3, "Mail"), (4, "Plate"), (6, "Shield"), (7, "Libram"), (8, "Idol"), (9, "Totem"), (10, "Sigil")),
            6 => Values((2, "Arrow"), (3, "Bullet")),
            7 => Values((0, "Trade Goods"), (1, "Parts"), (2, "Explosives"), (3, "Devices"), (4, "Jewelcrafting"), (5, "Cloth"), (6, "Leather"), (7, "Metal & Stone"), (8, "Meat"), (9, "Herb"), (10, "Elemental"), (12, "Enchanting"), (13, "Materials"), (14, "Armor Enchantment"), (15, "Weapon Enchantment")),
            9 => Values((0, "Book"), (1, "Leatherworking Recipe"), (2, "Tailoring Pattern"), (3, "Engineering Schematic"), (4, "Blacksmithing Plans"), (5, "Cooking Recipe"), (6, "Alchemy Recipe"), (7, "First Aid Manual"), (8, "Enchanting Formula"), (9, "Fishing Manual"), (10, "Jewelcrafting Design"), (11, "Inscription Technique")),
            11 => Values((2, "Quiver"), (3, "Ammo Pouch")),
            16 => Values((1, "Warrior Glyph"), (2, "Paladin Glyph"), (3, "Hunter Glyph"), (4, "Rogue Glyph"), (5, "Priest Glyph"), (6, "Death Knight Glyph"), (7, "Shaman Glyph"), (8, "Mage Glyph"), (9, "Warlock Glyph"), (11, "Druid Glyph")),
            _ => Values((0, "Generic"))
        };
        _subclass.DataSource = choices;
    }

    private static TabPage Page(string title, params (string Label, Control Control)[] fields)
    {
        var page = new TabPage(title); var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(14), ColumnCount = 2 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 190)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var row = 0; row < fields.Length; row++) { table.Controls.Add(new Label { Text = fields[row].Label + ":", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); table.Controls.Add(fields[row].Control, 1, row); }
        page.Controls.Add(table); return page;
    }

    private TabPage StatsPage()
    {
        var page = new TabPage("Stats"); var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(14), ColumnCount = 3 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 85)); table.ColumnStyles.Add(new(SizeType.Percent, 65)); table.ColumnStyles.Add(new(SizeType.Percent, 35));
        table.Controls.Add(new Label { Text = "Slot", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) }, 0, 0); table.Controls.Add(new Label { Text = "Stat", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) }, 1, 0); table.Controls.Add(new Label { Text = "Value", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) }, 2, 0);
        for (var index = 0; index < 10; index++) { table.Controls.Add(new Label { Text = $"Stat {index + 1}", AutoSize = true, Anchor = AnchorStyles.Left }, 0, index + 1); table.Controls.Add(_statTypes[index], 1, index + 1); table.Controls.Add(_statValues[index], 2, index + 1); }
        page.Controls.Add(table); return page;
    }

    private TabPage SpellEffectsPage()
    {
        var page = new TabPage("Spell Effects"); var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(10) };
        stack.Controls.Add(new Label { Text = "Each spell ID must exist in the target client's/server's spell data. Trigger controls whether the tooltip presents it as Use, Equip, or Chance on hit. Cooldown -1 uses the spell default.", AutoSize = true, MaximumSize = new(690, 0), ForeColor = Color.DimGray, Margin = new(3, 3, 3, 10) });
        for (var index = 0; index < 5; index++)
        {
            var group = new GroupBox { Text = $"Spell effect {index + 1}", Width = 700, Height = 174, Padding = new(10), Margin = new(3, 3, 3, 8) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4 };
            grid.ColumnStyles.Add(new(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new(SizeType.Percent, 50)); grid.ColumnStyles.Add(new(SizeType.Absolute, 155)); grid.ColumnStyles.Add(new(SizeType.Percent, 50));
            AddPair(grid, "Spell ID", _spellIds[index], 0, 0); AddPair(grid, "Trigger", _spellTriggers[index], 2, 0);
            AddPair(grid, "Charges", _spellCharges[index], 0, 1); AddPair(grid, "Proc per minute", _spellPpm[index], 2, 1);
            AddPair(grid, "Cooldown (ms)", _spellCooldowns[index], 0, 2); AddPair(grid, "Category ID", _spellCategories[index], 2, 2);
            AddPair(grid, "Category cooldown", _spellCategoryCooldowns[index], 0, 3); group.Controls.Add(grid); stack.Controls.Add(group);
            _help.SetToolTip(_spellCharges[index], "0 = unlimited. Negative charges delete the item when depleted; positive charges leave the empty item.");
            _help.SetToolTip(_spellPpm[index], "Procs per minute; used for Chance on hit effects.");
            _help.SetToolTip(_spellCooldowns[index], "Cooldown in milliseconds. -1 uses the spell's default cooldown; this is not a proc internal cooldown.");
            _help.SetToolTip(_spellCategories[index], "SpellCategory.dbc ID. Custom categories should normally be above 1260.");
            _help.SetToolTip(_spellCategoryCooldowns[index], "Shared category cooldown in milliseconds. -1 uses the default.");
        }
        page.Controls.Add(stack); return page;
    }

    private static void AddPair(TableLayoutPanel table, string label, Control control, int column, int row) { table.Controls.Add(new Label { Text = label + ":", AutoSize = true, Anchor = AnchorStyles.Left }, column, row); table.Controls.Add(control, column + 1, row); }
    private static NumericUpDown Number(decimal min, decimal max, decimal value = 0) => new() { Minimum = min, Maximum = max, Value = value, Dock = DockStyle.Fill, ThousandsSeparator = true };
    private static NumericUpDown DecimalNumber(decimal min, decimal max) => new() { Minimum = min, Maximum = max, DecimalPlaces = 2, Dock = DockStyle.Fill, ThousandsSeparator = true };
    private static ComboBox Choice(params (int Value, string Name)[] values) { var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", ValueMember = "Value" }; combo.DataSource = values.Select(value => new NamedValue(value.Value, value.Name)).ToList(); return combo; }
    private static ComboBox StatChoice() => Choice((0, "Mana (unused when value is 0)"), (1, "Health"), (3, "Agility"), (4, "Strength"), (5, "Intellect"), (6, "Spirit"), (7, "Stamina"), (12, "Defense rating"), (13, "Dodge rating"), (14, "Parry rating"), (15, "Block rating"), (16, "Melee hit rating"), (17, "Ranged hit rating"), (18, "Spell hit rating"), (19, "Melee critical rating"), (20, "Ranged critical rating"), (21, "Spell critical rating"), (22, "Melee hit avoidance"), (23, "Ranged hit avoidance"), (24, "Spell hit avoidance"), (25, "Melee critical avoidance"), (26, "Ranged critical avoidance"), (27, "Spell critical avoidance"), (28, "Melee haste rating"), (29, "Ranged haste rating"), (30, "Spell haste rating"), (31, "Hit rating"), (32, "Critical strike rating"), (33, "Hit avoidance rating"), (34, "Critical avoidance rating"), (35, "Resilience rating"), (36, "Haste rating"), (37, "Expertise rating"), (38, "Attack power"), (39, "Ranged attack power"), (40, "Feral attack power (legacy)"), (41, "Spell healing (legacy)"), (42, "Spell damage (legacy)"), (43, "Mana regeneration"), (44, "Armor penetration rating"), (45, "Spell power"), (46, "Health regeneration"), (47, "Spell penetration"), (48, "Block value"));
    private static ComboBox SpellTriggerChoice() => Choice((0, "Use"), (1, "Equip"), (2, "Chance on hit"), (4, "Soulstone"), (5, "Use (no delay)"), (6, "Learn spell"));
    private static int Selected(ComboBox combo) => combo.SelectedValue is int value ? value : 0;
    private static string SelectedName(ComboBox combo) => combo.SelectedItem is NamedValue value ? value.Name : string.Empty;
    private static List<NamedValue> Values(params (int Value, string Name)[] values) => values.Select(value => new NamedValue(value.Value, value.Name)).ToList();
    private sealed record NamedValue(int Value, string Name);
}
