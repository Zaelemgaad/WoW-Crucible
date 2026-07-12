using System.Globalization;
using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class SpellEditorForm : Form
{
    private readonly WdbcFile _file;
    private readonly int _row;
    private readonly IReadOnlyList<DbcColumn> _columns;
    private readonly Action<int, DbcColumn, object?> _applyValue;
    private readonly List<FieldBinding> _bindings = [];
    private readonly Label _heading = new();

    public SpellEditorForm(WdbcFile file, int row, IReadOnlyList<DbcColumn> columns, Action<int, DbcColumn, object?> applyValue)
    {
        _file = file;
        _row = row;
        _columns = columns;
        _applyValue = applyValue;
        Text = $"Spell Workspace — row {row:N0}";
        Width = 1050;
        Height = 820;
        MinimumSize = new(780, 600);
        StartPosition = FormStartPosition.CenterParent;

        var header = new Panel { Dock = DockStyle.Top, Height = 66, Padding = new(12, 8, 12, 8) };
        _heading.AutoSize = true;
        _heading.Font = new Font(Font, FontStyle.Bold);
        _heading.Location = new(12, 10);
        header.Controls.Add(_heading);
        var note = new Label { AutoSize = true, Location = new(12, 36), Text = "Changes apply to the open Spell.dbc row and participate in main-window undo/redo." };
        header.Controls.Add(note);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new(12, 6) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildCostsTab());
        for (var effect = 0; effect < 3; effect++) tabs.TabPages.Add(BuildEffectTab(effect));
        tabs.TabPages.Add(BuildTextTab());
        tabs.TabPages.Add(BuildVisualsTab());

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new(10), FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Text = "Apply changes", AutoSize = true };
        apply.Click += (_, _) => ApplyChanges();
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        footer.Controls.Add(close);
        footer.Controls.Add(apply);

        Controls.Add(tabs);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = apply;
        RefreshHeading();
    }

    private TabPage BuildGeneralTab()
    {
        var page = Page("General", out var table);
        Add(table, "Spell ID", 0, readOnly: true);
        Add(table, "Name (enUS)", 136);
        Add(table, "Category", 1); Add(table, "Dispel type", 2); Add(table, "Mechanic", 3);
        Add(table, "Spell level", 39); Add(table, "Base level", 38); Add(table, "Maximum level", 37);
        Add(table, "Casting time index", 28); Add(table, "Duration index", 40); Add(table, "Range index", 46);
        Add(table, "Recovery time (ms)", 29); Add(table, "Category recovery (ms)", 30);
        Add(table, "School mask", 225); Add(table, "Power type", 41); Add(table, "Maximum targets", 212);
        Add(table, "Proc flags", 34); Add(table, "Proc chance", 35); Add(table, "Proc charges", 36);
        Add(table, "Attributes", 4); Add(table, "AttributesEx", 5); Add(table, "AttributesEx2", 6); Add(table, "AttributesEx3", 7);
        return page;
    }

    private TabPage BuildCostsTab()
    {
        var page = Page("Costs & Requirements", out var table);
        Add(table, "Mana cost", 42); Add(table, "Mana cost per level", 43); Add(table, "Mana per second", 44); Add(table, "Mana cost percent", 204);
        Add(table, "Rune cost ID", 226); Add(table, "Required spell focus", 18); Add(table, "Required areas ID", 224);
        Add(table, "Equipped item class", 68); Add(table, "Equipped item subclass mask", 69); Add(table, "Equipped inventory mask", 70);
        for (var i = 0; i < 8; i++) { Add(table, $"Reagent {i + 1} ID", 52 + i); Add(table, $"Reagent {i + 1} count", 60 + i); }
        return page;
    }

    private TabPage BuildEffectTab(int effect)
    {
        var page = Page($"Effect {effect + 1}", out var table);
        Add(table, "Effect type", 71 + effect); Add(table, "Aura type", 95 + effect);
        Add(table, "Base points", 80 + effect); Add(table, "Die sides", 74 + effect); Add(table, "Points per level", 77 + effect); Add(table, "Points per combo", 119 + effect);
        Add(table, "Implicit target A", 86 + effect); Add(table, "Implicit target B", 89 + effect); Add(table, "Radius index", 92 + effect);
        Add(table, "Aura period", 98 + effect); Add(table, "Multiple value", 101 + effect); Add(table, "Chain targets", 104 + effect);
        Add(table, "Item type", 107 + effect); Add(table, "Misc value A", 110 + effect); Add(table, "Misc value B", 113 + effect); Add(table, "Trigger spell", 116 + effect);
        Add(table, "Mechanic", 83 + effect); Add(table, "Class mask A", 122 + effect); Add(table, "Class mask B", 125 + effect); Add(table, "Class mask C", 128 + effect);
        return page;
    }

    private TabPage BuildTextTab()
    {
        var page = Page("Text", out var table);
        Add(table, "Name (enUS)", 136); Add(table, "Subtext (enUS)", 153);
        Add(table, "Description (enUS)", 170, multiline: true); Add(table, "Aura description (enUS)", 187, multiline: true);
        Add(table, "Description variables ID", 232);
        return page;
    }

    private TabPage BuildVisualsTab()
    {
        var page = Page("Visuals & Links", out var table);
        Add(table, "Primary spell visual ID", 131); Add(table, "Secondary spell visual ID", 132);
        Add(table, "Spell icon ID", 133); Add(table, "Active icon ID", 134); Add(table, "Spell priority", 135);
        Add(table, "Missile ID", 227); Add(table, "Missile speed", 47); Add(table, "Difficulty ID", 233);
        Add(table, "Modal next spell", 48); Add(table, "Spell class set", 208);
        return page;
    }

    private static TabPage Page(string title, out TableLayoutPanel table)
    {
        var page = new TabPage(title) { Padding = new(8) };
        table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new(8) };
        table.ColumnStyles.Add(new(SizeType.Absolute, 240));
        table.ColumnStyles.Add(new(SizeType.Percent, 100));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(table);
        page.Controls.Add(scroll);
        return page;
    }

    private void Add(TableLayoutPanel table, string label, int columnIndex, bool readOnly = false, bool multiline = false)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new(SizeType.AutoSize));
        var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(3, 9, 8, 3) };
        var input = new TextBox { Dock = DockStyle.Top, ReadOnly = readOnly, Margin = new(3, 5, 3, 3) };
        if (multiline) { input.Multiline = true; input.Height = 90; input.ScrollBars = ScrollBars.Vertical; }
        var column = _columns[columnIndex];
        input.Text = Convert.ToString(_file.GetDisplayValue(_row, column), CultureInfo.InvariantCulture) ?? string.Empty;
        input.Tag = column;
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(input, 1, row);
        if (!readOnly) _bindings.Add(new(column, input, input.Text));
    }

    private void ApplyChanges()
    {
        var changed = _bindings.Where(binding => binding.Input.Text != binding.OriginalText).ToArray();
        if (changed.Length == 0) { MessageBox.Show(this, "No fields have changed.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        try
        {
            foreach (var binding in changed) Validate(binding.Column, binding.Input.Text);
            foreach (var binding in changed)
            {
                _applyValue(_row, binding.Column, binding.Input.Text);
                binding.OriginalText = binding.Input.Text;
            }
            RefreshHeading();
            MessageBox.Show(this, $"Applied {changed.Length:N0} field change(s).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid spell value", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void Validate(DbcColumn column, string value)
    {
        switch (column.Type)
        {
            case DbcValueType.Int32: int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); break;
            case DbcValueType.UInt32 or DbcValueType.Raw32:
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) uint.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                else uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                break;
            case DbcValueType.Byte: byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture); break;
            case DbcValueType.Float32: float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture); break;
        }
    }

    private void RefreshHeading() => _heading.Text = $"Spell {_file.GetDisplayValue(_row, _columns[0])}: {_file.GetDisplayValue(_row, _columns[136])}";

    private sealed class FieldBinding(DbcColumn column, TextBox input, string originalText)
    {
        public DbcColumn Column { get; } = column;
        public TextBox Input { get; } = input;
        public string OriginalText { get; set; } = originalText;
    }
}
