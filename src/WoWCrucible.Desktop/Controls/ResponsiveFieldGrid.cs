using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WoWCrucible.Desktop.Controls;

/// <summary>Gives every labeled field a flexible share of the row and reflows by available shape without rigid dimensions.</summary>
internal sealed class ResponsiveFieldGrid : Grid
{
    private readonly IReadOnlyList<(string Label, Control Field)> _fields;
    private int _columns;

    public ResponsiveFieldGrid(params (string Label, Control Field)[] fields)
    {
        if (fields.Length == 0) throw new ArgumentException("At least one responsive field is required.", nameof(fields)); _fields = fields;
        ColumnSpacing = 8; RowSpacing = 6; SizeChanged += (_, args) => Apply(args.NewSize); Apply(default);
    }

    private void Apply(Size size)
    {
        var columns = size.Width >= 640 ? Math.Min(4, _fields.Count) : size.Width >= 320 ? Math.Min(2, _fields.Count) : 1;
        if (_columns == columns) return; _columns = columns; Children.Clear(); ColumnDefinitions.Clear(); RowDefinitions.Clear();
        for (var column = 0; column < columns; column++) { ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); }
        var rows = (int)Math.Ceiling(_fields.Count / (double)columns); for (var row = 0; row < rows; row++) RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var index = 0; index < _fields.Count; index++)
        {
            var row = index / columns; var column = index % columns * 2; var label = new TextBlock { Text = _fields[index].Label, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#7F8A9F"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row); Grid.SetColumn(label, column); Children.Add(label); Grid.SetRow(_fields[index].Field, row); Grid.SetColumn(_fields[index].Field, column + 1); Children.Add(_fields[index].Field);
        }
    }
}
