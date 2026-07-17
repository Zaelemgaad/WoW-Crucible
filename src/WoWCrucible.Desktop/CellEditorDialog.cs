using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class CellEditorDialog : Window
{
    private readonly TextBox _value;
    private readonly SemanticField? _semantic;
    private readonly uint _originalRaw;
    private readonly List<CheckBox> _flagChecks = [];
    private readonly ComboBox? _enumChoice;

    public CellEditorDialog(WdbcFile file, int row, DbcColumn column)
    {
        _semantic = DbcSemanticCatalog.Get(Path.GetFileNameWithoutExtension(file.SourcePath), column.Index, file, row);
        _originalRaw = file.GetRaw(row, column);
        Title = $"Edit {column.Name}";
        Width = 650;
        Height = _semantic?.Kind == SemanticKind.Flags ? 620 : 390;
        MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(22) };
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(new TextBlock { Text = column.Name, FontSize = 22, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = $"Row {row + 1:N0} · {column.Type} · offset {column.Offset:N0} · raw 0x{_originalRaw:X8}",
            Foreground = new SolidColorBrush(Color.Parse("#8F9AAF")), FontSize = 11
        });
        if (_semantic is not null)
            heading.Children.Add(new TextBlock { Text = _semantic.Label, Foreground = new SolidColorBrush(Color.Parse("#E1AA4E")), FontWeight = FontWeight.SemiBold });
        root.Children.Add(heading);

        _value = new TextBox
        {
            Text = _semantic?.Format(_originalRaw) ?? Convert.ToString(file.GetDisplayValue(row, column), CultureInfo.InvariantCulture) ?? string.Empty,
            Margin = new Thickness(0, 16, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(_value, 1);
        root.Children.Add(_value);

        Control guidance;
        if (_semantic?.Kind == SemanticKind.Enum)
        {
            _enumChoice = new ComboBox
            {
                ItemsSource = _semantic.Options,
                ItemTemplate = new FuncDataTemplate<SemanticOption>((option, _) => new TextBlock { Text = option is null ? string.Empty : $"{option.Name}  [{option.Value}]" }),
                SelectedItem = _semantic.Options.FirstOrDefault(option => option.Value == _originalRaw),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top
            };
            _enumChoice.SelectionChanged += (_, _) =>
            {
                if (_enumChoice.SelectedItem is SemanticOption option) _value.Text = option.Value.ToString(CultureInfo.InvariantCulture);
            };
            guidance = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Choose a named value", Foreground = new SolidColorBrush(Color.Parse("#AAB4C5")) },
                    _enumChoice
                }
            };
        }
        else if (_semantic?.Kind == SemanticKind.Flags)
        {
            var options = new StackPanel { Spacing = 3 };
            foreach (var option in _semantic.Options.Where(option => option.Value != 0))
            {
                var check = new CheckBox { Content = $"{option.Name}  [0x{option.Value:X8}]", IsChecked = (_originalRaw & option.Value) == option.Value, Tag = option };
                _flagChecks.Add(check);
                options.Children.Add(check);
            }
            guidance = new Grid
            {
                RowDefinitions = new("Auto,*"),
                Children =
                {
                    new TextBlock { Text = "Toggle readable flags. Unknown bits are preserved.", Foreground = new SolidColorBrush(Color.Parse("#AAB4C5")), Margin = new Thickness(0,0,0,7) },
                    new ScrollViewer { Content = options, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Margin = new Thickness(0,28,0,0) }
                }
            };
        }
        else
        {
            _enumChoice = null;
            guidance = new TextBlock
            {
                Text = column.Type switch
                {
                    DbcValueType.StringOffset => "Enter decoded text. Crucible will reuse or append it safely in the DBC string table.",
                    DbcValueType.Float32 => "Enter a decimal floating-point value.",
                    DbcValueType.Raw32 or DbcValueType.UInt32 => "Enter an unsigned number or a hexadecimal value beginning with 0x.",
                    _ => "Enter the new value. It will be checked against the field type before anything changes."
                },
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#AAB4C5"))
            };
        }
        Grid.SetRow(guidance, 2);
        root.Children.Add(guidance);

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close(null);
        var apply = new Button { Content = "Apply change", Classes = { "accent" } };
        apply.Click += (_, _) => Close(ResolvedValue());
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, apply } };
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        Content = root;
    }

    private string ResolvedValue()
    {
        if (_semantic?.Kind != SemanticKind.Flags) return _value.Text ?? string.Empty;
        var knownMask = _semantic.Options.Aggregate(0u, (mask, option) => mask | option.Value);
        var value = _originalRaw & ~knownMask;
        foreach (var check in _flagChecks)
            if (check.IsChecked == true && check.Tag is SemanticOption option) value |= option.Value;
        return $"0x{value:X8}";
    }
}
