using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed record SpellFieldChange(DbcColumn Column, string Value);

internal sealed class SpellWorkspaceView : UserControl
{
    private sealed record FieldDefinition(string Label, int Column, bool Multiline = false);
    private sealed class FieldBinding(DbcColumn column, TextBox input, string originalText)
    {
        public DbcColumn Column { get; } = column;
        public TextBox Input { get; } = input;
        public string OriginalText { get; set; } = originalText;
    }

    private readonly WdbcFile _file;
    private readonly int _row;
    private readonly IReadOnlyList<DbcColumn> _columns;
    private readonly Action<IReadOnlyList<SpellFieldChange>> _apply;
    private readonly List<FieldBinding> _bindings = [];
    private readonly TextBlock _heading = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };

    public event EventHandler? BackRequested;

    public SpellWorkspaceView(WdbcFile file, int row, IReadOnlyList<DbcColumn> columns, Action<IReadOnlyList<SpellFieldChange>> apply)
    {
        _file = file;
        _row = row;
        _columns = columns;
        _apply = apply;

        var back = new Button { Content = "← DBC table" };
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var applyButton = AccentButton("Apply changes to staged Spell.dbc");
        applyButton.Click += (_, _) => ApplyChanges();
        var header = new Grid
        {
            ColumnDefinitions = new("Auto,*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(12, 8),
            Children =
            {
                back,
                WithColumn(new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        _heading,
                        new TextBlock
                        {
                            Text = "Guided WotLK 3.3.5a spell editing · all changes remain in the staged DBC and use its normal undo/save workflow.",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brush.Parse("#8E99AD")
                        }
                    }
                }, 1),
                WithColumn(applyButton, 2)
            }
        };

        var tabs = new TabControl
        {
            Margin = new Thickness(10),
            Items =
            {
                Tab("General", GeneralFields()),
                Tab("Costs & requirements", CostFields()),
                Tab("Effect 1", EffectFields(0)),
                Tab("Effect 2", EffectFields(1)),
                Tab("Effect 3", EffectFields(2)),
                Tab("Text", TextFields()),
                Tab("Visuals & links", VisualFields())
            }
        };
        var footer = new Border
        {
            BorderBrush = Brush.Parse("#2B3445"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 7),
            Child = _status
        };
        Content = new Grid
        {
            RowDefinitions = new("Auto,*,Auto"),
            Children = { header, WithRow(tabs, 1), WithRow(footer, 2) }
        };
        RefreshHeading();
        _status.Text = $"Editing row {_row + 1:N0}. Fields show their exact stored values; decoded meanings appear beside supported enums and flags.";
    }

    private static FieldDefinition[] GeneralFields() =>
    [
        new("Spell ID", 0), new("Name (enUS)", 136), new("Category", 1), new("Dispel type", 2), new("Mechanic", 3),
        new("Spell level", 39), new("Base level", 38), new("Maximum level", 37), new("Casting time index", 28),
        new("Duration index", 40), new("Range index", 46), new("Recovery time (ms)", 29), new("Category recovery (ms)", 30),
        new("School mask", 225), new("Power type", 41), new("Maximum targets", 212), new("Proc flags", 34),
        new("Proc chance", 35), new("Proc charges", 36), new("Attributes", 4), new("AttributesEx", 5),
        new("AttributesEx2", 6), new("AttributesEx3", 7), new("AttributesEx4", 8), new("AttributesEx5", 9),
        new("AttributesEx6", 10), new("AttributesEx7", 11)
    ];

    private static FieldDefinition[] CostFields()
    {
        var fields = new List<FieldDefinition>
        {
            new("Mana cost", 42), new("Mana cost per level", 43), new("Mana per second", 44), new("Mana cost percent", 204),
            new("Rune cost ID", 226), new("Required spell focus", 18), new("Required areas ID", 224),
            new("Equipped item class", 68), new("Equipped item subclass mask", 69), new("Equipped inventory mask", 70)
        };
        for (var index = 0; index < 8; index++)
        {
            fields.Add(new FieldDefinition($"Reagent {index + 1} ID", 52 + index));
            fields.Add(new FieldDefinition($"Reagent {index + 1} count", 60 + index));
        }
        return fields.ToArray();
    }

    private static FieldDefinition[] EffectFields(int effect) =>
    [
        new("Effect type", 71 + effect), new("Aura type", 95 + effect), new("Base points", 80 + effect),
        new("Die sides", 74 + effect), new("Points per level", 77 + effect), new("Points per combo point", 119 + effect),
        new("Implicit target A", 86 + effect), new("Implicit target B", 89 + effect), new("Radius index", 92 + effect),
        new("Aura period", 98 + effect), new("Multiple value", 101 + effect), new("Chain targets", 104 + effect),
        new("Item type", 107 + effect), new("Misc value A", 110 + effect), new("Misc value B", 113 + effect),
        new("Trigger spell", 116 + effect), new("Mechanic", 83 + effect), new("Class mask A", 122 + effect),
        new("Class mask B", 125 + effect), new("Class mask C", 128 + effect)
    ];

    private static FieldDefinition[] TextFields() =>
    [
        new("Subtext (enUS)", 153), new("Description (enUS)", 170, true),
        new("Aura description (enUS)", 187, true), new("Description variables ID", 232)
    ];

    private static FieldDefinition[] VisualFields() =>
    [
        new("Primary spell visual ID", 131), new("Secondary spell visual ID", 132), new("Spell icon ID", 133),
        new("Active icon ID", 134), new("Spell priority", 135), new("Missile ID", 227), new("Missile speed", 47),
        new("Difficulty ID", 233), new("Modal next spell", 48), new("Spell class set", 208)
    ];

    private TabItem Tab(string title, IEnumerable<FieldDefinition> fields)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new("Auto,2*,*"),
            ColumnSpacing = 10,
            RowSpacing = 7,
            Margin = new Thickness(12)
        };
        var row = 0;
        foreach (var field in fields.Where(field => field.Column >= 0 && field.Column < _columns.Count))
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var column = _columns[field.Column];
            var current = Convert.ToString(_file.GetDisplayValue(_row, column), CultureInfo.InvariantCulture) ?? string.Empty;
            var label = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center };
            var input = new TextBox
            {
                Text = current,
                IsReadOnly = field.Column == 0,
                AcceptsReturn = field.Multiline,
                TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap
            };
            var meaning = Meaning(field.Column, column);
            Grid.SetRow(label, row);
            Grid.SetRow(input, row); Grid.SetColumn(input, 1);
            Grid.SetRow(meaning, row); Grid.SetColumn(meaning, 2);
            grid.Children.Add(label); grid.Children.Add(input); grid.Children.Add(meaning);
            if (!input.IsReadOnly) _bindings.Add(new FieldBinding(column, input, current));
            row++;
        }
        return new TabItem { Header = title, Content = new ScrollViewer { Content = grid } };
    }

    private TextBlock Meaning(int columnIndex, DbcColumn column)
    {
        var semantic = DbcSemanticCatalog.Get("Spell", columnIndex, _file, _row);
        var text = semantic?.Format(_file.GetRaw(_row, column));
        return new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(text) ? column.Type.ToString() : text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#7F8A9F"),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void ApplyChanges()
    {
        var changed = _bindings
            .Where(binding => !string.Equals(binding.Input.Text, binding.OriginalText, StringComparison.Ordinal))
            .Select(binding => new SpellFieldChange(binding.Column, binding.Input.Text ?? string.Empty))
            .ToArray();
        if (changed.Length == 0) { _status.Text = "No spell fields have changed."; return; }
        try
        {
            foreach (var change in changed) Validate(change.Column, change.Value);
            _apply(changed);
            foreach (var binding in _bindings) binding.OriginalText = binding.Input.Text ?? string.Empty;
            RefreshHeading();
            _status.Text = $"Applied {changed.Length:N0} field change(s) to the staged Spell.dbc. Save writes atomically and keeps a .bak.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Spell changes were rejected: {exception.Message}";
            DesktopCrashLogger.Log("Spell workspace apply failed", exception);
        }
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
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
