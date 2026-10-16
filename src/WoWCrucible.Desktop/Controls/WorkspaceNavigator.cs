using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

internal sealed record WorkspaceGroup(string Title, string[] Commands);

/// <summary>Presentation groups over the shared command catalog; never a second route registry.</summary>
internal static class WorkspaceNavigation
{
    public static IReadOnlyList<WorkspaceGroup> Groups { get; } =
    [
        new("Game content", ["workspace.class-studio", "workspace.items", "workspace.creatures", "workspace.quests", "workspace.gameobjects", "workspace.pets", "workspace.behaviors", "workspace.projects"]),
        new("World & artwork", ["workspace.maps", "workspace.lighting", "workspace.models", "workspace.textures", "workspace.assets", "workspace.conversion"]),
        new("Data & packaging", ["workspace.dbc", "workspace.sql", "workspace.dbc-layers", "workspace.mpq", "workspace.mpq-merge", "workspace.cache", "workspace.dbd", "workspace.item-acquisition", "workspace.sql-favorites"]),
        new("Workspace & delivery", ["workspace.setup", "workspace.server", "workspace.client", "workspace.compatibility-lab", "workspace.addons"]),
        new("Help & reference", ["workspace.knowledge", "workspace.tools", "workspace.cli-guide"])
    ];

    public static CrucibleCommandDescriptor Command(string id) => CrucibleCommandCatalog.All.Single(command => command.Id == id);
    public static string Label(string id) => id switch
    {
        "workspace.projects" => "Projects, classes & races",
        "workspace.dbc" => "Client data tables (DBC / DB2)",
        "workspace.sql" => "Database tables & queries",
        "workspace.mpq" => "Archives & patches",
        "workspace.server" => "Server connection & controls",
        "workspace.knowledge" => "Field help & reference",
        _ => Command(id).Title
    };
}

internal sealed class WorkspaceNavigator : UserControl
{
    private readonly TextBox _search = new() { PlaceholderText = "Find a tool or task…" };
    private readonly StackPanel _results = new() { Spacing = 3 };
    private readonly Dictionary<string, bool> _expanded = [];
    private string? _selected;
    public event EventHandler<string>? CommandRequested;

    public WorkspaceNavigator()
    {
        AutomationProperties.SetName(_search, "Search tools and tasks");
        _search.TextChanged += (_, _) => Refresh();
        var scroller = new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1);
        Content = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8, Children = { _search, scroller } };
        Refresh();
    }

    public void SelectCommand(string? id) { _selected = id; Refresh(); }

    private Button Entry(string id)
    {
        var command = WorkspaceNavigation.Command(id);
        var button = new Button
        {
            Content = new TextBlock { Text = WorkspaceNavigation.Label(id), TextWrapping = TextWrapping.Wrap },
            HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(9, 7), BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = id == _selected ? Brush.Parse("#E0A33C") : Brushes.Transparent,
            Background = id == _selected ? Brush.Parse("#243149") : Brushes.Transparent
        };
        ToolTip.SetTip(button, command.Description);
        button.Click += (_, _) => CommandRequested?.Invoke(this, id);
        return button;
    }

    private void Refresh()
    {
        _results.Children.Clear();
        if (!string.IsNullOrWhiteSpace(_search.Text))
        {
            var matches = CrucibleCommandCatalog.Search(_search.Text, CrucibleCommandCatalog.All.Count)
                .Where(match => match.Command.Id.StartsWith("workspace.", StringComparison.Ordinal)).ToArray();
            foreach (var match in matches) _results.Children.Add(Entry(match.Command.Id));
            if (matches.Length == 0) _results.Children.Add(new TextBlock { Text = "No matching tools. Try an object name, such as item, spell or texture.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) });
            return;
        }
        foreach (var group in WorkspaceNavigation.Groups)
        {
            var children = new StackPanel { Spacing = 1 };
            foreach (var id in group.Commands) children.Children.Add(Entry(id));
            var expander = new Expander { Header = group.Title, Content = children, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = _expanded.GetValueOrDefault(group.Title, group.Title is "Game content" or "World & artwork") };
            expander.Expanded += (_, _) => _expanded[group.Title] = true;
            expander.Collapsed += (_, _) => _expanded[group.Title] = false;
            _results.Children.Add(expander);
        }
    }
}
