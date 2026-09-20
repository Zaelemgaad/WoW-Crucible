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
    private sealed record Material(int Slot, string Name, string Texture, string Usage, uint FileDataId);
    private readonly ListBox _materials = new();
    private readonly ListBox _files = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search BLP textures" };
    private readonly CheckBox _inactive = new() { Content = "Show unused materials" };
    private readonly TextBlock _selected = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _browse = new() { Content = "...", Width = 34 };
    private readonly Button _clear = new() { Content = "\u00D7", Width = 34 };
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
        ToolTip.SetTip(_browse, "Choose a BLP texture"); ToolTip.SetTip(_clear, "Clear texture assignment");
        _materials.ItemTemplate = new FuncDataTemplate<Material>((material, _) =>
        {
            if (material is null) return new TextBlock();
            var row = new StackPanel { Spacing = 3, Margin = new Thickness(4), Children =
            {
                new TextBlock { Text = material.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = material.Texture, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = material.Usage, FontSize = 11 }
            }};
            ToolTip.SetTip(row, $"Material slot {material.Slot}" + (material.FileDataId == 0 ? "" : $", FileDataID {material.FileDataId}"));
            return row;
        });
        _files.ItemTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            if (path is null) return new TextBlock();
            var label = new StackPanel { Margin = new Thickness(4), Spacing = 3, Children =
            {
                new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = Path.GetDirectoryName(path), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 }
            }};
            ToolTip.SetTip(label, path); return label;
        });
        _materials.SelectionChanged += (_, _) => { if (!_synchronizing) SelectMaterial(); };
        _files.SelectionChanged += async (_, _) =>
        {
            if (!_synchronizing && _materials.SelectedItem is Material material && _files.SelectedItem is string path && BindingChanged is not null)
                await BindingChanged(material.Slot, path);
        };
        _search.TextChanged += (_, _) => FilterFiles(); _inactive.IsCheckedChanged += (_, _) => RefreshMaterials();
        _clear.Click += async (_, _) => { if (_materials.SelectedItem is Material material && BindingChanged is not null) await BindingChanged(material.Slot, null); };
        _browse.Click += async (_, _) =>
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null || _materials.SelectedItem is not Material material) return;
            var generation = _generation;
            var path = (await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose BLP texture", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("BLP textures") { Patterns = ["*.blp"] }] })).FirstOrDefault()?.TryGetLocalPath();
            if (path is null || generation != _generation || BindingChanged is null) return;
            _paths = _paths.Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await BindingChanged(material.Slot, path);
        };
        var selection = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 4, Children = { _selected, _browse, _clear } };
        Grid.SetColumn(_browse, 1); Grid.SetColumn(_clear, 2);
        var layout = new Grid { RowDefinitions = new("Auto,*,5,Auto,Auto,*"), RowSpacing = 5, Margin = new Thickness(4) };
        layout.Children.Add(_inactive); Add(_materials, 1);
        Add(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch }, 2);
        Add(selection, 3); Add(_search, 4); Add(_files, 5); Content = layout;
        void Add(Control control, int row) { Grid.SetRow(control, row); layout.Children.Add(control); }
        SelectMaterial();
    }

    public void Load(M2PreviewGeometry geometry, IReadOnlyDictionary<int, string> bindings, IEnumerable<int> resolved, IEnumerable<string> paths)
    {
        _generation++; _geometry = geometry; _bindings = bindings; _resolved = resolved.ToHashSet();
        _paths = paths.Concat(bindings.Values).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        UpdateVisibleGeometry(geometry); FilterFiles();
    }

    public void Clear()
    {
        _generation++; _geometry = null; _paths = []; _bindings = new Dictionary<int, string>(); _resolved = new HashSet<int>(); _usage.Clear(); _effectTextures.Clear();
        RefreshMaterials(); FilterFiles();
    }

    public void UpdateVisibleGeometry(M2PreviewGeometry geometry)
    {
        _effectTextures = geometry.ParticleEmitters.SelectMany(emitter => emitter.TextureDefinitionIndices).Concat(geometry.RibbonEmitters.Select(emitter => emitter.TextureDefinitionIndex)).ToHashSet();
        _usage = geometry.TextureSlots.ToDictionary(slot => slot.Index, slot => geometry.Batches
            .Where(batch => batch.TextureDefinitionIndex == slot.Index || batch.TextureStages.Any(stage => stage.TextureDefinitionIndex == slot.Index))
            .Sum(batch => batch.TriangleIndexCount / 3));
        RefreshMaterials();
    }

    public void UpdateBindings(IReadOnlyDictionary<int, string> bindings, IEnumerable<int> resolved) { _bindings = bindings; _resolved = resolved.ToHashSet(); RefreshMaterials(); }

    private void RefreshMaterials()
    {
        var selected = (_materials.SelectedItem as Material)?.Slot;
        var rows = _geometry?.TextureSlots.Where(slot => _inactive.IsChecked == true || _usage.GetValueOrDefault(slot.Index) > 0 || _effectTextures.Contains(slot.Index))
            .Select(slot => new Material(slot.Index, ModelBrowserTextureService.SlotName(slot.Type),
                _bindings.TryGetValue(slot.Index, out var path) ? (_resolved.Contains(slot.Index) ? "" : "Unresolved: ") + Path.GetFileName(path) : "Unassigned",
                _usage.GetValueOrDefault(slot.Index) > 0 ? $"{_usage[slot.Index]:N0} visible triangles" : _effectTextures.Contains(slot.Index) ? "Particle / ribbon texture" : "No visible geometry", slot.FileDataId)).ToArray() ?? [];
        _synchronizing = true; _materials.ItemsSource = rows;
        _materials.SelectedItem = rows.FirstOrDefault(row => row.Slot == selected) ?? rows.FirstOrDefault(); _synchronizing = false;
        SelectMaterial();
    }

    private void SelectMaterial()
    {
        var material = _materials.SelectedItem as Material;
        _selected.Text = material?.Name ?? "No material selected"; _files.IsEnabled = _browse.IsEnabled = _clear.IsEnabled = material is not null;
        FilterFiles();
    }

    private void FilterFiles()
    {
        var query = (_search.Text ?? "").Trim();
        var paths = _paths.Concat(_bindings.Values).Distinct(StringComparer.OrdinalIgnoreCase).Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _synchronizing = true; _files.ItemsSource = paths;
        _files.SelectedItem = _materials.SelectedItem is Material material ? _bindings.GetValueOrDefault(material.Slot) : null;
        _synchronizing = false;
    }
}
