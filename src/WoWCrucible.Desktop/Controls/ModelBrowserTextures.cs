using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

internal sealed class ModelBrowserTextures : UserControl
{
    private sealed record TextureChoice(string? Path)
    {
        public override string ToString() => Path is null ? "Unassigned" : System.IO.Path.GetFileName(Path);
    }

    private sealed record TextureRow(StackPanel Panel, ComboBox Picker, TextBlock Usage);
    private readonly StackPanel _materials = new() { Spacing = 12, Margin = new Thickness(4) };
    private readonly Dictionary<int, TextureRow> _rows = [];
    private readonly TextBox _search = new() { PlaceholderText = "Filter texture choices" };
    private readonly CheckBox _inactive = new() { Content = "Show unused materials", IsChecked = true };
    private M2PreviewGeometry? _geometry;
    private IReadOnlyDictionary<int, string> _bindings = new Dictionary<int, string>();
    private IReadOnlySet<int> _resolved = new HashSet<int>();
    private Dictionary<int, int> _usage = [];
    private HashSet<int> _effectTextures = [];
    private string[] _paths = [];
    private bool _synchronizing;
    private int _generation;
    public Func<int, string?, Task>? BindingChanged { get; set; }

    public ModelBrowserTextures()
    {
        _search.TextChanged += (_, _) => RefreshChoices();
        _inactive.IsCheckedChanged += (_, _) => RefreshUsage();
        var scroll = new ScrollViewer { Content = _materials, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var layout = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 5, Margin = new Thickness(4), Children = { _search, _inactive, scroll } };
        Grid.SetRow(_inactive, 1); Grid.SetRow(scroll, 2); Content = layout;
    }

    public void Load(M2PreviewGeometry geometry, IReadOnlyDictionary<int, string> bindings, IEnumerable<int> resolved, IEnumerable<string> paths)
    {
        _generation++; _geometry = geometry; _bindings = bindings; _resolved = resolved.ToHashSet();
        _paths = paths.Concat(bindings.Values).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        _rows.Clear(); _materials.Children.Clear();
        foreach (var slot in geometry.TextureSlots) AddRow(slot);
        UpdateVisibleGeometry(geometry); RefreshChoices();
    }

    public void Clear()
    {
        _generation++; _geometry = null; _paths = []; _bindings = new Dictionary<int, string>(); _resolved = new HashSet<int>();
        _usage.Clear(); _effectTextures.Clear(); _rows.Clear(); _materials.Children.Clear();
    }

    private void AddRow(M2TextureSlot slot)
    {
        var generation = _generation;
        var title = new TextBlock { Text = $"{ModelBrowserTextureService.SlotName(slot.Type)} [{slot.Index}]", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        ToolTip.SetTip(title, $"Material slot {slot.Index}" + (slot.FileDataId == 0 ? "" : $", FileDataID {slot.FileDataId}"));
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, MaxDropDownHeight = 330 };
        picker.ItemTemplate = new FuncDataTemplate<TextureChoice>((choice, _) =>
        {
            var label = new TextBlock { Text = choice?.ToString() ?? "", TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(label, choice?.Path); return label;
        });
        picker.SelectionChanged += async (_, _) =>
        {
            if (!_synchronizing && generation == _generation && picker.SelectedItem is TextureChoice choice && BindingChanged is not null)
                await BindingChanged(slot.Index, choice.Path);
        };
        var browse = new Button { Content = "...", Width = 32, Padding = new Thickness(3) };
        var clear = new Button { Content = "\u00D7", Width = 32, Padding = new Thickness(3) };
        ToolTip.SetTip(browse, "Choose a BLP texture"); ToolTip.SetTip(clear, "Clear texture assignment");
        clear.Click += async (_, _) => { if (generation == _generation && BindingChanged is not null) await BindingChanged(slot.Index, null); };
        browse.Click += async (_, _) =>
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null || generation != _generation) return;
            var path = (await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose BLP texture", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("BLP textures") { Patterns = ["*.blp"] }] })).FirstOrDefault()?.TryGetLocalPath();
            if (path is null || generation != _generation || BindingChanged is null) return;
            _paths = _paths.Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await BindingChanged(slot.Index, path);
        };
        var controls = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 4, Children = { picker, browse, clear } };
        Grid.SetColumn(browse, 1); Grid.SetColumn(clear, 2);
        var usage = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 3, Children = { title, controls, usage } };
        _rows[slot.Index] = new(panel, picker, usage); _materials.Children.Add(panel);
    }

    public void UpdateVisibleGeometry(M2PreviewGeometry geometry)
    {
        _effectTextures = geometry.ParticleEmitters.SelectMany(emitter => emitter.TextureDefinitionIndices).Concat(geometry.RibbonEmitters.Select(emitter => emitter.TextureDefinitionIndex)).ToHashSet();
        _usage = geometry.TextureSlots.ToDictionary(slot => slot.Index, slot => geometry.Batches
            .Where(batch => batch.TextureDefinitionIndex == slot.Index || batch.TextureStages.Any(stage => stage.TextureDefinitionIndex == slot.Index))
            .Sum(batch => batch.TriangleIndexCount / 3));
        RefreshUsage();
    }

    public void UpdateBindings(IReadOnlyDictionary<int, string> bindings, IEnumerable<int> resolved)
    {
        _bindings = bindings; _resolved = resolved.ToHashSet(); RefreshChoices(); RefreshUsage();
    }

    private void RefreshUsage()
    {
        foreach (var (slot, row) in _rows)
        {
            var triangles = _usage.GetValueOrDefault(slot);
            row.Panel.IsVisible = _inactive.IsChecked == true || triangles > 0 || _effectTextures.Contains(slot);
            row.Usage.Text = (triangles > 0 ? $"{triangles:N0} visible triangles" : _effectTextures.Contains(slot) ? "Particle / ribbon texture" : "No visible geometry")
                + (_bindings.ContainsKey(slot) && !_resolved.Contains(slot) ? " | Unresolved texture" : "");
        }
    }

    private void RefreshChoices()
    {
        if (_geometry is null) return;
        var query = (_search.Text ?? "").Trim();
        var matches = _paths.Concat(_bindings.Values).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(path => new TextureChoice(path)).ToArray();
        var unassigned = new TextureChoice(null);
        var choices = new[] { unassigned }.Concat(matches).ToArray();
        var byPath = matches.ToDictionary(choice => choice.Path!, StringComparer.OrdinalIgnoreCase);
        _synchronizing = true;
        try
        {
            foreach (var (slot, row) in _rows)
            {
                var path = _bindings.GetValueOrDefault(slot);
                var selected = path is null ? unassigned : byPath.GetValueOrDefault(path) ?? new TextureChoice(path);
                // Filtering choices must not unassign a texture already on the model.
                row.Picker.ItemsSource = path is not null && !byPath.ContainsKey(path) ? choices.Append(selected).ToArray() : choices;
                row.Picker.SelectedItem = selected; ToolTip.SetTip(row.Picker, path);
            }
        }
        finally { _synchronizing = false; }
    }
}
