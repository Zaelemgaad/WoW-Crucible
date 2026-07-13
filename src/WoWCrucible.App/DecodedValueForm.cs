using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class DecodedValueForm : Form
{
    private readonly SemanticField _field;
    private readonly CheckedListBox? _flags;
    private readonly ComboBox? _values;
    private readonly Label _raw = new() { AutoSize = true };
    public uint Value { get; private set; }

    public DecodedValueForm(SemanticField field, uint value)
    {
        _field = field; Value = value; Text = $"Decoded value — {field.Label}"; Width = 620; Height = 560;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.SizableToolWindow;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new(8), FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        panel.Controls.Add(ok); panel.Controls.Add(cancel); panel.Controls.Add(_raw);
        if (field.Kind == SemanticKind.Flags)
        {
            _flags = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            foreach (var option in field.Options) _flags.Items.Add(option, option.Value != 0 && (value & option.Value) == option.Value);
            var knownMask = field.Options.Aggregate(0u, (mask, option) => mask | option.Value);
            var unknown = value & ~knownMask;
            for (var bit = 0; bit < 32; bit++)
            {
                var bitValue = 1u << bit;
                if ((unknown & bitValue) != 0) _flags.Items.Add(new SemanticOption(bitValue, $"Unknown bit {bit} (0x{bitValue:X8})"), true);
            }
            _flags.DisplayMember = nameof(SemanticOption.Name); _flags.ItemCheck += (_, _) => BeginInvoke(UpdateRawPreview);
            Controls.Add(_flags);
        }
        else
        {
            _values = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new(12) };
            foreach (var option in field.Options) _values.Items.Add(option);
            var selected = field.Options.FirstOrDefault(option => option.Value == value) ?? new SemanticOption(value, $"Unknown [{value}]");
            if (!_values.Items.Contains(selected)) _values.Items.Add(selected);
            _values.DisplayMember = nameof(SemanticOption.Name); _values.SelectedItem = selected;
            Controls.Add(_values);
        }
        Controls.Add(panel); AcceptButton = ok; CancelButton = cancel; UpdateRawPreview();
        FormClosing += (_, _) => { if (DialogResult == DialogResult.OK) Value = CalculateValue(); };
    }

    private uint CalculateValue()
    {
        if (_values?.SelectedItem is SemanticOption option) return option.Value;
        uint value = 0;
        if (_flags is not null) foreach (SemanticOption checkedOption in _flags.CheckedItems) value |= checkedOption.Value;
        return value;
    }

    private void UpdateRawPreview() => _raw.Text = $"Raw: {CalculateValue()} / 0x{CalculateValue():X8}    ";
}
