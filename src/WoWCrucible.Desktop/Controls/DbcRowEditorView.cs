using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

internal sealed class DbcRowEditorView : UserControl
{
    private sealed record Field(DbcColumn Column, TextBlock Label, TextBox Value, ComboBox Choice, TextBlock Meaning, TextBlock Error)
    {
        public string Original { get; set; } = string.Empty;
    }

    private readonly TextBlock _heading = new() { Text = "Select a record", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _form = new() { Spacing = 12 };
    private readonly List<Field> _fields = [];
    private DbcDocumentSession? _document;
    private bool _refreshing;
    private bool _committing;

    public int RowIndex { get; private set; } = -1;
    public Func<DbcDocumentSession, int, DbcColumn, string, string?>? CommitValue { get; set; }

    public DbcRowEditorView()
    {
        var scroll = new ScrollViewer { Content = _form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        Content = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(10), Children = { _heading, scroll } };
    }

    public bool SelectRow(DbcDocumentSession? document, int row)
    {
        if (ReferenceEquals(_document, document) && RowIndex == row) return true;
        if (!TryCommitPending()) return false;
        var rebuild = !ReferenceEquals(_document, document);
        _document = document;
        RowIndex = row;
        if (rebuild) { _form.Children.Clear(); _fields.Clear(); }
        if (document is null || row < 0 || row >= document.File.RowCount)
        {
            _form.IsVisible = false;
            _heading.Text = "Select a record";
            return true;
        }
        if (_fields.Count == 0) BuildFields(document);
        _form.IsVisible = true;
        RefreshValues(reset: true);
        return true;
    }

    private void BuildFields(DbcDocumentSession document)
    {
        IEnumerable<DbcColumn> columns = document.Schema.Columns;
        // Pair each scaling stat with its weight, retaining the exact schema fields.
        if (IsScalingDistribution(document.File))
            columns = new[] { document.Schema.Columns[0], document.Schema.Columns[21] }
                .Concat(Enumerable.Range(1, 10).SelectMany(index => new[] { document.Schema.Columns[index], document.Schema.Columns[index + 10] }));
        foreach (var column in columns)
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush.Parse("#BBC1C8") };
            ToolTip.SetTip(label, $"{column.Name}\n{column.Type}");
            var value = new TextBox { FontSize = 13 };
            AutomationProperties.SetAutomationId(value, $"DbcRowField_{column.Index}");
            AutomationProperties.SetName(value, column.Name);
            var choice = new ComboBox
            {
                IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemTemplate = new FuncDataTemplate<SemanticOption>((option, _) => new TextBlock { Text = option?.Name, TextTrimming = TextTrimming.CharacterEllipsis })
            };
            AutomationProperties.SetAutomationId(choice, $"DbcRowChoice_{column.Index}");
            AutomationProperties.SetName(choice, $"{column.Name} named value");
            var meaning = new TextBlock { FontSize = 11, Foreground = Brush.Parse("#93B6D7"), TextWrapping = TextWrapping.Wrap };
            var error = new TextBlock { IsVisible = false, FontSize = 12, Foreground = Brush.Parse("#F18F88"), TextWrapping = TextWrapping.Wrap };
            var field = new Field(column, label, value, choice, meaning, error);
            _fields.Add(field);
            var controls = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6, Children = { value, choice } };
            Grid.SetColumn(choice, 1);
            value.LostFocus += (_, _) => { if (!_refreshing) TryCommit(field, focusOnError: false); };
            value.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { TryCommit(field); e.Handled = true; }
                else if (e.Key == Key.Escape) { value.Text = field.Original; error.IsVisible = false; e.Handled = true; }
            };
            choice.SelectionChanged += (_, _) =>
            {
                if (_refreshing || choice.SelectedItem is not SemanticOption option) return;
                value.Text = column.Type == DbcValueType.Int32 ? unchecked((int)option.Value).ToString(CultureInfo.InvariantCulture) : option.Value.ToString(CultureInfo.InvariantCulture);
                TryCommit(field);
            };
            _form.Children.Add(new StackPanel { Spacing = 4, Children = { label, controls, meaning, error } });
        }
    }

    public bool TryCommitPending()
    {
        if (_committing || _refreshing) return true;
        foreach (var field in _fields)
            if (!TryCommit(field)) return false;
        return true;
    }

    private bool TryCommit(Field field, bool focusOnError = true)
    {
        if (_committing || _refreshing || _document is null || RowIndex < 0) return true;
        if (field.Value.Text == field.Original) { field.Error.IsVisible = false; return true; }
        _committing = true;
        try
        {
            var error = CommitValue is null ? "No file is open for editing." : CommitValue(_document, RowIndex, field.Column, field.Value.Text ?? string.Empty);
            field.Error.Text = error;
            field.Error.IsVisible = error is not null;
            if (error is not null)
            {
                if (focusOnError)
                {
                    var document = _document;
                    var row = RowIndex;
                    var rejected = field.Value.Text;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (TopLevel.GetTopLevel(this) is Window { IsActive: true } &&
                            ReferenceEquals(_document, document) && RowIndex == row &&
                            field.Error.IsVisible && field.Value.Text == rejected)
                            field.Value.Focus();
                    }, DispatcherPriority.Input);
                }
                return false;
            }
        }
        finally { _committing = false; }
        field.Original = Convert.ToString(_document.File.GetDisplayValue(RowIndex, field.Column), CultureInfo.InvariantCulture) ?? string.Empty;
        field.Value.Text = field.Original;
        RefreshValues();
        return true;
    }

    public void RefreshValues(bool reset = false)
    {
        if (_document is null || RowIndex < 0 || _committing) return;
        var file = _document.File;
        _refreshing = true;
        try
        {
            _heading.Text = _document.Schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? $"Row {RowIndex + 1}" : $"Record {DbcRecordIdentity.GetKey(file, RowIndex, _document.Schema.Columns, _document.Schema.KeyStrategy)}";
            foreach (var field in _fields)
            {
                if (!reset && field.Value.Text != field.Original) continue;
                var semantic = field.Column.EffectiveBitWidth <= 32 ? DbcSemanticCatalog.Get(file.LogicalTableName, field.Column.Index, file, RowIndex) : null;
                field.Label.Text = FieldLabel(file, field.Column);
                field.Original = Convert.ToString(file.GetDisplayValue(RowIndex, field.Column), CultureInfo.InvariantCulture) ?? string.Empty;
                field.Value.Text = field.Original;
                field.Choice.IsVisible = semantic?.Kind == SemanticKind.Enum;
                if (field.Value.Parent is Grid controls)
                {
                    controls.ColumnDefinitions[0].Width = field.Choice.IsVisible ? new GridLength(80) : new GridLength(1, GridUnitType.Star);
                    controls.ColumnDefinitions[1].Width = field.Choice.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                }
                if (semantic?.Kind == SemanticKind.Enum)
                {
                    var raw = file.GetRaw(RowIndex, field.Column);
                    var options = semantic.Options.ToList();
                    var selected = options.FirstOrDefault(option => option.Value == raw);
                    if (selected is null) { selected = new(raw, $"Unknown ({field.Original})"); options.Add(selected); }
                    field.Choice.ItemsSource = options;
                    field.Choice.SelectedItem = selected;
                }
                field.Meaning.Text = semantic?.Kind == SemanticKind.Flags ? semantic.Format(file.GetRaw(RowIndex, field.Column)) : string.Empty;
                field.Meaning.IsVisible = !string.IsNullOrEmpty(field.Meaning.Text);
                field.Error.IsVisible = false;
            }
        }
        finally { _refreshing = false; }
    }

    private string FieldLabel(WdbcFile file, DbcColumn column)
    {
        if (IsScalingDistribution(file))
        {
            if (column.Index == 21) return "Maximum level";
            if (column.Index is >= 1 and <= 10) return $"Stat {column.Index}";
            if (column.Index is >= 11 and <= 20)
            {
                var slot = column.Index - 10;
                var type = file.GetRaw(RowIndex, _document!.Schema.Columns[slot]);
                var name = ItemSemanticCatalog.StatTypes.FirstOrDefault(option => option.Value == type)?.Name ?? $"Unknown stat {type}";
                return $"Stat {slot} bonus - {name}";
            }
        }
        if (column.Name.Equals("Charlevel", StringComparison.OrdinalIgnoreCase)) return "Character level";
        var label = Regex.Replace(column.Name.Replace('_', ' '), "([a-z0-9])([A-Z])", "$1 $2");
        return Regex.Replace(label, "([A-Z])([A-Z][a-z])", "$1 $2");
    }

    private static bool IsScalingDistribution(WdbcFile file) => file.LogicalTableName.Equals("ScalingStatDistribution", StringComparison.OrdinalIgnoreCase) && file.FieldCount == 22 && file.RecordSize == 88;
}
