using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class WorkspaceSetupView : UserControl
{
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _root = new() { PlaceholderText = "Top-level folder, for example G:\\WotLK" };
    private readonly TextBox _name = new() { PlaceholderText = "Workspace name" };
    private readonly Dictionary<string, TextBox> _paths = new(StringComparer.Ordinal);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private IReadOnlyList<string> _findings = [];

    public event EventHandler? BackRequested;

    public WorkspaceSetupView(DesktopWorkspaceSession session)
    {
        _session = session;
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var browseRoot = new Button { Content = "Choose root…" }; browseRoot.Click += async (_, _) => await BrowseRootAsync();
        var discover = AccentButton("Discover everything"); discover.Click += async (_, _) => await DiscoverAsync();
        var apply = AccentButton("Remember this workspace"); apply.Click += async (_, _) => await ApplyAsync();
        var rootRow = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _root, AtColumn(browseRoot, 1), AtColumn(discover, 2) } };

        var form = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowSpacing = 7, ColumnSpacing = 9 };
        AddRow(form, "Workspace name", _name, false);
        AddPathRow(form, "Server install", "server", false);
        AddPathRow(form, "Core source", "core", false);
        AddPathRow(form, "Client folder", "client", false);
        AddPathRow(form, "Client Data", "data", false);
        AddPathRow(form, "Wow.exe", "wow", true);
        AddPathRow(form, "Server DBC", "dbc", false);
        AddPathRow(form, "WotLK schema XML", "schema", true);
        AddPathRow(form, "WoWDBDefs definitions", "dbd", false);
        AddPathRow(form, "Processed assets", "assets", false);
        AddPathRow(form, "Projects", "projects", false);
        AddPathRow(form, "Staging", "staging", false);
        AddPathRow(form, "Tools", "tools", false);
        AddPathRow(form, "Noggit.exe (optional)", "noggit", true);
        AddPathRow(form, "Extracted World/Maps", "maps", false);

        Content = new Grid
        {
            RowDefinitions = new("Auto,*"),
            Children =
            {
                new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#2B3445")), BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(12,8),
                    Child = new Grid { ColumnDefinitions = new("Auto,*"), Children = { back, AtColumn(new TextBlock { Text = "WORKSPACE SETUP", FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12,0), VerticalAlignment = VerticalAlignment.Center }, 1) } }
                },
                AtRow(new ScrollViewer
                {
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(18), Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = "Point Crucible at one top-level WotLK workspace. It discovers the server, core, client, DBC definitions, tools, projects, and staging paths, then writes a portable .crucible/workspace.json. You can override every path below; database passwords are never stored in this file.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#A5B0C1")) },
                            rootRow,
                            new Border { BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = form },
                            new WrapPanel { Children = { apply } },
                            _status
                        }
                    }
                }, 1)
            }
        };
        Activate();
    }

    public void Activate()
    {
        _root.Text = _session.Settings.WorkspaceRootPath;
        _name.Text = _session.Settings.WorkspaceName;
        Put("server", _session.Settings.ServerRootPath); Put("core", _session.Settings.CoreSourcePath);
        Put("data", _session.Settings.ClientDataPath); Put("wow", _session.Settings.ClientExecutablePath);
        Put("client", Directory.Exists(_session.Settings.ClientDataPath) ? Directory.GetParent(_session.Settings.ClientDataPath)?.FullName ?? string.Empty : string.Empty);
        Put("dbc", _session.Settings.CoreDbcPath); Put("schema", _session.Settings.SchemaDefinitionPath); Put("dbd", _session.Settings.DbdDefinitionsPath);
        Put("assets", _session.Settings.ProcessedAssetLibraryPath); Put("projects", _session.Settings.WorkspaceProjectsPath); Put("staging", _session.Settings.WorkspaceStagingPath);
        Put("tools", _session.Settings.ToolsPath); Put("noggit", _session.Settings.NoggitExecutablePath); Put("maps", _session.Settings.MapSourcePath);
        _status.Text = string.IsNullOrWhiteSpace(_root.Text) ? "Choose a root folder to create the first workspace." : $"Current workspace: {_root.Text}";
    }

    private async Task BrowseRootAsync()
    {
        var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the top-level WoW workspace", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) _root.Text = path;
    }

    private async Task DiscoverAsync()
    {
        try
        {
            _status.Text = "Scanning the workspace without moving or deleting anything…";
            var root = _root.Text ?? string.Empty;
            var manifest = CrucibleWorkspaceLayout.ManifestPath(root);
            var layout = await Task.Run(() => File.Exists(manifest) ? CrucibleWorkspaceLayoutService.Load(root) : CrucibleWorkspaceLayoutService.Discover(root));
            Populate(layout);
            _status.Text = string.Join(Environment.NewLine, layout.Findings);
        }
        catch (Exception exception) { _status.Text = $"Discovery failed: {exception.Message}"; }
    }

    private async Task ApplyAsync()
    {
        try
        {
            var layout = ReadLayout();
            _status.Text = "Saving the workspace and detecting its live server configuration…";
            await _session.ConfigureWorkspaceAsync(layout);
            _status.Text = _session.DatabaseTested
                ? $"Workspace saved and SQL verified. Crucible will reuse these paths from now on.\n{CrucibleWorkspaceLayout.ManifestPath(layout.RootPath)}"
                : $"Workspace saved. SQL is currently offline; use the SQL light above to start it.\n{CrucibleWorkspaceLayout.ManifestPath(layout.RootPath)}";
        }
        catch (Exception exception)
        {
            _status.Text = ManifestExists(_root.Text)
                ? $"Workspace paths were saved, but the server/database is currently unavailable: {exception.Message}\nUse the status lights above after the service is started."
                : $"Workspace was not saved: {exception.Message}";
        }
    }

    private void Populate(CrucibleWorkspaceLayout layout)
    {
        _root.Text = layout.RootPath; _name.Text = layout.Name; _findings = layout.Findings;
        Put("server", layout.ServerRootPath); Put("core", layout.CoreSourcePath); Put("client", layout.ClientRootPath); Put("data", layout.ClientDataPath); Put("wow", layout.ClientExecutablePath);
        Put("dbc", layout.CoreDbcPath); Put("schema", layout.SchemaDefinitionPath); Put("dbd", layout.DbdDefinitionsPath); Put("assets", layout.ProcessedAssetLibraryPath);
        Put("projects", layout.ProjectsPath); Put("staging", layout.StagingPath); Put("tools", layout.ToolsPath); Put("noggit", layout.NoggitExecutablePath); Put("maps", layout.MapSourcePath);
    }

    private CrucibleWorkspaceLayout ReadLayout()
    {
        if (string.IsNullOrWhiteSpace(_root.Text)) throw new InvalidOperationException("Choose a top-level workspace folder first.");
        var root = Path.GetFullPath(_root.Text.Trim());
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Workspace folder does not exist: {root}");
        var name = string.IsNullOrWhiteSpace(_name.Text) ? Path.GetFileName(root) : _name.Text.Trim();
        return new(CrucibleWorkspaceLayout.CurrentFormatVersion, name, root,
            Get("server"), Get("core"), Get("client"), Get("data"), Get("wow"), Get("dbc"), Get("schema"), Get("dbd"), Get("assets"),
            Get("projects"), Get("staging"), Get("tools"), Get("noggit"), Get("maps"), _findings);
    }

    private void AddPathRow(Grid form, string label, string key, bool file)
    {
        var input = _paths[key] = new TextBox();
        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => await BrowsePathAsync(input, file, label);
        AddRow(form, label, input, browse);
    }
    private static void AddRow(Grid form, string label, Control input, bool _) => AddRow(form, label, input, null);
    private static void AddRow(Grid form, string label, Control input, Control? browse)
    {
        var row = form.RowDefinitions.Count; form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row); form.Children.Add(text); Grid.SetRow(input, row); Grid.SetColumn(input, 1); form.Children.Add(input);
        if (browse is not null) { Grid.SetRow(browse, row); Grid.SetColumn(browse, 2); form.Children.Add(browse); }
    }
    private async Task BrowsePathAsync(TextBox input, bool file, string label)
    {
        if (file)
        {
            var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Choose {label}", AllowMultiple = false });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) input.Text = path;
        }
        else
        {
            var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Choose {label}", AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) input.Text = path;
        }
    }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Workspace setup is not attached to the main window.");
    private static bool ManifestExists(string? root)
    {
        try { return !string.IsNullOrWhiteSpace(root) && File.Exists(CrucibleWorkspaceLayout.ManifestPath(root)); }
        catch { return false; }
    }
    private string Get(string key) => _paths[key].Text?.Trim() ?? string.Empty;
    private void Put(string key, string value) => _paths[key].Text = value;
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T AtColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T AtRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
