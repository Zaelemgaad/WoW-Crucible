using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class PetAbilityGraphView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly NumericUpDown _creature = Number(416);
    private readonly TextBox _dbc = new() { PlaceholderText = "Server DBC folder" };
    private readonly TextBox _schema = new() { PlaceholderText = "WotLK 3.3.5a (12340) definitions XML" };
    private readonly TextBox _search = new() { PlaceholderText = "Filter all nodes by ID, name, type, or detail" };
    private readonly ComboBox _kind = new();
    private readonly ListBox _nodes = new();
    private readonly ListBox _edges = new();
    private readonly TextBlock _detail = Status("Select a graph node or evidence edge to inspect it.");
    private readonly TextBlock _summary = Status("Connect Server & SQL, then load a creature's real client/server ability relationships.");
    private readonly TextBlock _status = Status("No graph loaded.");
    private readonly PetAbilityGraphOverview _overview = new();
    private readonly Button _load = AccentButton("Load evidenced graph");
    private PetAbilityGraph? _graph;
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;
    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public PetAbilityGraphView(DesktopWorkspaceSession session)
    {
        _session = session;
        _session.Changed += SessionChanged;
        _dbc.Text = session.Settings.CoreDbcPath;
        _schema.Text = session.Settings.SchemaDefinitionPath;
        _kind.ItemsSource = new[] { "All node types" }.Concat(Enum.GetNames<PetAbilityNodeKind>()).ToArray();
        _kind.SelectedIndex = 0;

        var back = new Button { Content = "← Pets" };
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var find = new Button { Content = "Find creature…" };
        find.Click += (_, _) => ReferenceLookupRequested?.Invoke(this, new(ReferenceDomain.Creature, "Pet ability graph creature", Entry(), selected => _creature.Value = selected));
        var browseDbc = new Button { Content = "Browse DBC…" };
        browseDbc.Click += async (_, _) => await BrowseFolderAsync(_dbc, "Choose the server DBC folder");
        var browseSchema = new Button { Content = "Browse schema…" };
        browseSchema.Click += async (_, _) => await BrowseSchemaAsync();
        _load.Click += async (_, _) => await LoadAsync();
        _search.TextChanged += (_, _) => RefreshNodes();
        _kind.SelectionChanged += (_, _) => RefreshNodes();
        _nodes.SelectionChanged += (_, _) => SelectNode();
        _edges.SelectionChanged += (_, _) => SelectEdge();

        var heading = new WrapPanel { Margin = new Thickness(12, 8), Children =
        {
            back,
            new TextBlock { Text = "PET TALENTS & ABILITIES", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            _status
        } };
        var configuration = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children =
        {
            Help("This graph follows exact live SQL and build-12340 DBC links. Every node and relationship remains listed even when the visual overview only has room for the selected node's nearest neighbors."),
            Field("Creature entry", _creature, find),
            Field("Server DBC folder", _dbc, browseDbc),
            Field("Schema definitions", _schema, browseSchema),
            new WrapPanel { Children = { _load } },
            _summary
        } };
        var overview = new Grid { RowDefinitions = new("*,Auto"), Children = { _overview, WithRow(new Border { Padding = new Thickness(10), BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _detail }, 1) } };
        var nodeTools = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _search, WithColumn(_kind, 1) } };
        var nodeTab = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(8), Children = { nodeTools, WithRow(_nodes, 1) } };
        var tabs = new TabControl { Items =
        {
            new TabItem { Header = "Ability map", Content = overview },
            new TabItem { Header = "All nodes", Content = nodeTab },
            new TabItem { Header = "All evidence edges", Content = _edges },
            new TabItem { Header = "Findings", Content = new ScrollViewer { Content = _summary } }
        } };
        Content = new Grid { RowDefinitions = new("Auto,Auto,*"), Children =
        {
            new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading },
            WithRow(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = configuration }, 1),
            WithRow(tabs, 2)
        } };
    }

    public void SetCreature(uint entry) => _creature.Value = entry;

    private async Task LoadAsync()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        try
        {
            if (_session.DatabaseProfile is null || !_session.DatabaseTested) throw new InvalidOperationException("Connect and verify Server & SQL first; the graph needs the live creature schema.");
            var dbc = Path.GetFullPath(_dbc.Text ?? string.Empty);
            var schema = Path.GetFullPath(_schema.Text ?? string.Empty);
            _load.IsEnabled = false;
            _status.Text = $"Resolving creature {Entry():N0}…";
            _graph = await new PetAbilityGraphService().BuildAsync(_session.DatabaseProfile, dbc, schema, Entry(), _operation.Token);
            _session.Settings.CoreDbcPath = dbc;
            _session.Settings.SchemaDefinitionPath = schema;
            _session.Settings.Save();
            _overview.SetGraph(_graph);
            RefreshNodes();
            RefreshEdges();
            var counts = _graph.Nodes.GroupBy(node => node.Kind).OrderBy(group => group.Key).Select(group => $"{group.Key}: {group.Count():N0}");
            _summary.Text = $"{_graph.CreatureName} [{_graph.CreatureEntry}] · family {(_graph.FamilyId == 0 ? "none" : $"{_graph.FamilyName} [{_graph.FamilyId}]")} · pet talent type {_graph.PetTalentType}\n{string.Join(" · ", counts)}\n\n{string.Join("\n", _graph.Findings)}";
            _status.Text = $"Loaded {_graph.Nodes.Count:N0} nodes · {_graph.Edges.Count:N0} evidence edges";
            _detail.Text = "Select any node to show its direct relationship neighborhood, or select an evidence edge for its exact SQL/DBC source.";
        }
        catch (OperationCanceledException) { _status.Text = "Graph load cancelled."; }
        catch (Exception exception)
        {
            _status.Text = $"Graph failed: {exception.Message}";
            DesktopCrashLogger.Log("Pet ability graph failed", exception);
        }
        finally { _load.IsEnabled = true; }
    }

    private void RefreshNodes()
    {
        if (_graph is null) { _nodes.ItemsSource = null; return; }
        var query = (_search.Text ?? string.Empty).Trim();
        PetAbilityNodeKind? kind = _kind.SelectedIndex <= 0 ? null : Enum.TryParse<PetAbilityNodeKind>(Convert.ToString(_kind.SelectedItem, CultureInfo.InvariantCulture), out var parsed) ? parsed : null;
        _nodes.ItemsSource = _graph.Nodes
            .Where(node => kind is null || node.Kind == kind)
            .Where(node => query.Length == 0 || node.NumericId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) || node.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || node.Detail.Contains(query, StringComparison.OrdinalIgnoreCase) || node.Kind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(node => new NodeItem(node)).ToArray();
    }

    private void RefreshEdges()
    {
        _edges.ItemsSource = _graph?.Edges.Select(edge => new EdgeItem(edge, Label(edge.From), Label(edge.To))).ToArray();
    }

    private void SelectNode()
    {
        if (_nodes.SelectedItem is not NodeItem item || _graph is null) return;
        _edges.SelectedItem = null;
        var incoming = _graph.Edges.Count(edge => edge.To.Equals(item.Node.Id, StringComparison.OrdinalIgnoreCase));
        var outgoing = _graph.Edges.Count(edge => edge.From.Equals(item.Node.Id, StringComparison.OrdinalIgnoreCase));
        _detail.Text = $"{item.Node.Kind} · {item.Node.Label}\n{item.Node.Detail}\n{incoming:N0} incoming · {outgoing:N0} outgoing relationship(s)";
        _overview.SetGraph(_graph, item.Node.Id);
    }

    private void SelectEdge()
    {
        if (_edges.SelectedItem is not EdgeItem item || _graph is null) return;
        _nodes.SelectedItem = null;
        _detail.Text = $"{item.FromLabel}\n→ {item.Edge.Relation}\n{item.ToLabel}\n\nEvidence: {item.Edge.Evidence}";
        _overview.SetGraph(_graph, item.Edge.From);
    }

    private string Label(string id) => _graph?.Nodes.FirstOrDefault(node => node.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Label ?? id;
    private uint Entry() => Convert.ToUInt32(_creature.Value ?? 0, CultureInfo.InvariantCulture);
    private void SessionChanged(object? sender, EventArgs e) { if (string.IsNullOrWhiteSpace(_dbc.Text)) _dbc.Text = _session.Settings.CoreDbcPath; if (string.IsNullOrWhiteSpace(_schema.Text)) _schema.Text = _session.Settings.SchemaDefinitionPath; }

    private async Task BrowseFolderAsync(TextBox target, string title)
    {
        var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) target.Text = path;
    }

    private async Task BrowseSchemaAsync()
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose WotLK schema definitions", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Schema XML") { Patterns = ["*.xml"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) _schema.Text = path;
    }

    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Pet graph is not attached to the main window.");
    public void Dispose() { _session.Changed -= SessionChanged; _operation?.Cancel(); _operation?.Dispose(); }

    private static NumericUpDown Number(uint value) => new() { Value = value, Minimum = 1, Maximum = uint.MaxValue, Increment = 1 };
    private static Grid Field(string label, Control field, Control action)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, WithColumn(field, 1), WithColumn(action, 2) } };
        return grid;
    }
    private static TextBlock Help(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#B8C3D5") };
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), VerticalAlignment = VerticalAlignment.Center };
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private sealed record NodeItem(PetAbilityGraphNode Node) { public override string ToString() => $"{Node.Kind} · {Node.Label} · {Node.Detail}"; }
    private sealed record EdgeItem(PetAbilityGraphEdge Edge, string FromLabel, string ToLabel) { public override string ToString() => $"{FromLabel} → {Edge.Relation} → {ToLabel} · {Edge.Evidence}"; }
}
