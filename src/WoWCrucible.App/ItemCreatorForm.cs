using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class ItemCreatorForm : Form
{
    private readonly DatabaseConnectionProfile _profile;
    private readonly DatabaseTableCapability _table;
    private readonly NumericUpDown _entry = Number(1, uint.MaxValue);
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _class = Choice((0, "Consumable"), (2, "Weapon"), (4, "Armor"), (15, "Miscellaneous"));
    private readonly NumericUpDown _subclass = Number(0, 30);
    private readonly NumericUpDown _display = Number(0, uint.MaxValue);
    private readonly ComboBox _quality = Choice((0, "Poor"), (1, "Common"), (2, "Uncommon"), (3, "Rare"), (4, "Epic"), (5, "Legendary"), (7, "Heirloom"));
    private readonly ComboBox _inventory = Choice((0, "Non-equippable"), (1, "Head"), (3, "Shoulder"), (5, "Chest"), (6, "Waist"), (7, "Legs"), (8, "Feet"), (9, "Wrist"), (10, "Hands"), (13, "One-hand weapon"), (14, "Shield"), (17, "Two-hand weapon"), (21, "Main-hand weapon"), (22, "Off-hand weapon"));
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
    private readonly ComboBox _stat1 = StatChoice();
    private readonly NumericUpDown _statValue1 = Number(-100000, 100000);
    private readonly ComboBox _stat2 = StatChoice();
    private readonly NumericUpDown _statValue2 = Number(-100000, 100000);
    private readonly TextBox _description = new() { Dock = DockStyle.Fill, Multiline = true, Height = 55 };
    private readonly TextBox _preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9), WordWrap = false };

    public ItemCreatorForm(DatabaseConnectionProfile profile, DatabaseCapabilities capabilities)
    {
        _profile = profile; _table = capabilities.FindTable("item_template") ?? throw new NotSupportedException("The connected database has no item_template table.");
        Text = $"Guided Item Creator — {capabilities.Database}"; Width = 1050; Height = 800; StartPosition = FormStartPosition.CenterParent;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Basics", ("Entry ID", _entry), ("Name", _name), ("Item class", _class), ("Subclass ID", _subclass), ("Display ID", _display), ("Quality", _quality), ("Inventory slot", _inventory), ("Item level", _itemLevel), ("Required level", _requiredLevel), ("Binding", _bonding), ("Description", _description)));
        tabs.TabPages.Add(Page("Combat and value", ("Buy price (copper)", _buy), ("Sell price (copper)", _sell), ("Armor", _armor), ("Minimum damage", _damageMin), ("Maximum damage", _damageMax), ("Weapon delay (ms)", _delay), ("Durability", _durability), ("Raw flags", _flags)));
        tabs.TabPages.Add(Page("Stats", ("Stat 1", _stat1), ("Stat 1 value", _statValue1), ("Stat 2", _stat2), ("Stat 2 value", _statValue2)));
        var previewPage = new TabPage("SQL preview"); previewPage.Controls.Add(_preview); tabs.TabPages.Add(previewPage);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new(8) };
        var preview = new Button { Text = "Refresh SQL Preview", AutoSize = true }; var export = new Button { Text = "Export SQL…", AutoSize = true }; var insert = new Button { Text = "Insert into Database…", AutoSize = true };
        buttons.Controls.Add(preview); buttons.Controls.Add(export); buttons.Controls.Add(insert); buttons.Controls.Add(new Label { Text = $"Schema: {_table.Columns.Count} columns detected", AutoSize = true, Margin = new(16, 8, 0, 0), ForeColor = Color.DimGray });
        preview.Click += (_, _) => { if (TryPlan(out var plan)) { _preview.Text = Preview(plan!); tabs.SelectedTab = previewPage; } };
        export.Click += (_, _) => Export(); insert.Click += async (_, _) => await InsertAsync(insert);
        Controls.Add(tabs); Controls.Add(buttons);
    }

    private ItemWritePlan Plan() => ItemTemplateAdapter.CreatePlan(new(
        (uint)_entry.Value, _name.Text, Selected(_class), (int)_subclass.Value, (uint)_display.Value, Selected(_quality), Selected(_inventory),
        (uint)_itemLevel.Value, (uint)_requiredLevel.Value, (uint)_buy.Value, (uint)_sell.Value, (uint)Selected(_bonding), (uint)_flags.Value,
        (float)_armor.Value, (float)_damageMin.Value, (float)_damageMax.Value, (uint)_delay.Value, (uint)_durability.Value, _description.Text,
        Selected(_stat1), (int)_statValue1.Value, Selected(_stat2), (int)_statValue2.Value), _table);

    private bool TryPlan(out ItemWritePlan? plan) { try { plan = Plan(); return true; } catch (Exception ex) { plan = null; MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; } }
    private string Preview(ItemWritePlan plan) => plan.PreviewSql() + (plan.OmittedFields.Count == 0 ? "" : $"\r\n\r\n-- Fields omitted because this server schema does not expose them: {string.Join(", ", plan.OmittedFields)}");
    private void Export() { if (!TryPlan(out var plan)) return; using var dialog = new SaveFileDialog { Filter = "SQL files (*.sql)|*.sql", FileName = $"item-{_entry.Value}.sql" }; if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, Preview(plan!) + Environment.NewLine); }
    private async Task InsertAsync(Button button)
    {
        if (!TryPlan(out var plan)) return;
        _preview.Text = Preview(plan!);
        if (MessageBox.Show(this, $"Insert item {_entry.Value} '{_name.Text}' into {_profile.Database}.item_template?\n\nExisting IDs are never replaced.", "Confirm database write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { button.Enabled = false; await new ItemTemplateService().InsertAsync(_profile, plan!); MessageBox.Show(this, "Item inserted successfully. Restart or reload the world server as required by your core.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { button.Enabled = true; }
    }

    private static TabPage Page(string title, params (string Label, Control Control)[] fields)
    {
        var page = new TabPage(title); var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(14), ColumnCount = 2 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 190)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var row = 0; row < fields.Length; row++) { table.Controls.Add(new Label { Text = fields[row].Label + ":", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); table.Controls.Add(fields[row].Control, 1, row); }
        page.Controls.Add(table); return page;
    }
    private static NumericUpDown Number(decimal min, decimal max) => new() { Minimum = min, Maximum = max, Dock = DockStyle.Fill, ThousandsSeparator = true };
    private static NumericUpDown DecimalNumber(decimal min, decimal max) => new() { Minimum = min, Maximum = max, DecimalPlaces = 2, Dock = DockStyle.Fill, ThousandsSeparator = true };
    private static ComboBox Choice(params (int Value, string Name)[] values) { var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", ValueMember = "Value" }; combo.DataSource = values.Select(value => new NamedValue(value.Value, value.Name)).ToList(); return combo; }
    private static ComboBox StatChoice() => Choice((0, "None"), (3, "Agility"), (4, "Strength"), (5, "Intellect"), (6, "Spirit"), (7, "Stamina"), (12, "Defense rating"), (31, "Hit rating"), (32, "Critical strike rating"), (35, "Resilience rating"), (36, "Haste rating"), (37, "Expertise rating"), (38, "Attack power"), (45, "Spell power"));
    private static int Selected(ComboBox combo) => combo.SelectedValue is int value ? value : 0;
    private sealed record NamedValue(int Value, string Name);
}
