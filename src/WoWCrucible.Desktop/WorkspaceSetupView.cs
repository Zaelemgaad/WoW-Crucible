using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class WorkspaceSetupView : UserControl
{
    private sealed record WorkspaceChoice(CrucibleWorkspaceLayout Layout)
    {
        public override string ToString()
        {
            var server = string.IsNullOrWhiteSpace(Layout.ServerRootPath) ? "no server" : Path.GetFileName(Layout.ServerRootPath.TrimEnd(Path.DirectorySeparatorChar));
            var client = string.IsNullOrWhiteSpace(Layout.ClientRootPath) ? "no client" : Path.GetFileName(Layout.ClientRootPath.TrimEnd(Path.DirectorySeparatorChar));
            return $"{Layout.Name} · {server} ↔ {client}";
        }
    }
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _root = new() { PlaceholderText = "Top-level folder, for example G:\\WotLK" };
    private readonly TextBox _name = new() { PlaceholderText = "Workspace name" };
    private readonly ComboBox _savedWorkspaces = new() { PlaceholderText = "Select a saved workspace…" };
    private readonly ComboBox _serverChoice = new() { PlaceholderText = "Choose the server for this workspace…" };
    private readonly ComboBox _clientChoice = new() { PlaceholderText = "Choose the client for this workspace…" };
    private readonly Border _pairingPanel = new() { IsVisible = false, BorderBrush = new SolidColorBrush(Color.Parse("#4A5A76")), BorderThickness = new Thickness(1), Padding = new Thickness(12) };
    private readonly CheckBox _backupsEnabled = new() { Content = "Keep safety backups before replacing files" };
    private readonly NumericUpDown _backupRetention = new() { Minimum = 1, Maximum = 100, Increment = 1 };
    private readonly NumericUpDown _backupStorageLimit = new() { Minimum = 1, Maximum = 1024, Increment = 1 };
    private readonly TextBox _backupRoot = new() { PlaceholderText = "Default: Backups folder beside WoWCrucible.exe" };
    private readonly Dictionary<string, TextBox> _paths = new(StringComparer.Ordinal);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#C8D0DE")) };
    private IReadOnlyList<string> _findings = [];
    private CrucibleWorkspaceLayout? _discovered;
    private CrucibleWorkspaceDiscovery? _discovery;

    public event EventHandler? BackRequested;

    public WorkspaceSetupView(DesktopWorkspaceSession session)
    {
        _session = session;
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        foreach (var key in new[] { "server", "core", "client", "data", "wow", "dbc", "schema", "dbd", "assets", "projects", "staging", "tools", "noggit", "maps" }) _paths[key] = new TextBox();
        var browseRoot = new Button { Content = "Choose workspace folder…" }; browseRoot.Click += async (_, _) => await BrowseRootAsync();
        var apply = AccentButton("Use this workspace"); apply.Click += async (_, _) => await ApplyAsync();
        var rootRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _root, AtColumn(browseRoot, 1) } };
        _savedWorkspaces.SelectionChanged += async (_, _) => await LoadSelectedWorkspaceAsync();
        _serverChoice.SelectionChanged += (_, _) => ApplyPairingChoices();
        _clientChoice.SelectionChanged += (_, _) => ApplyPairingChoices();
        var pairingForm = new Grid { ColumnDefinitions = new("Auto,*"), RowSpacing = 7, ColumnSpacing = 9 };
        AddRow(pairingForm, "Use this server", _serverChoice, false);
        AddRow(pairingForm, "Use this client", _clientChoice, false);
        AddRow(pairingForm, "Save pairing as", _name, false);
        _pairingPanel.Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Multiple installs found · choose the exact pair", FontWeight = FontWeight.SemiBold }, pairingForm } };

        var commonOverrides = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowSpacing = 7, ColumnSpacing = 9 };
        AddExistingPathRow(commonOverrides, "Server folder", "server", false);
        AddExistingPathRow(commonOverrides, "Server source code", "core", false);
        AddExistingPathRow(commonOverrides, "Game client folder", "client", false);
        AddExistingPathRow(commonOverrides, "Processed asset library", "assets", false);
        AddExistingPathRow(commonOverrides, "Extracted map files", "maps", false);
        AddExistingPathRow(commonOverrides, "Noggit program (optional)", "noggit", true);

        var compatibilityOverrides = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowSpacing = 7, ColumnSpacing = 9 };
        AddExistingPathRow(compatibilityOverrides, "WotLK table definitions", "schema", true);
        AddExistingPathRow(compatibilityOverrides, "Later-version table definitions", "dbd", false);
        var backupForm = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowSpacing = 7, ColumnSpacing = 9 };
        AddRow(backupForm, "Safety copies", _backupsEnabled, false);
        AddRow(backupForm, "Versions kept per file", _backupRetention, false);
        AddRow(backupForm, "Total storage ceiling (GiB)", _backupStorageLimit, false);
        var browseBackup = new Button { Content = "Browse…" }; browseBackup.Click += async (_, _) => await BrowsePathAsync(_backupRoot, false, "backup folder");
        AddRow(backupForm, "Backup folder", _backupRoot, browseBackup);
        var backupSettings = new Expander { Header = "Backups · visible, optional, and bounded", IsExpanded = false, Content = backupForm };
        var advanced = new Expander
        {
            Header = "Advanced overrides · only if automatic detection picked the wrong folder",
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    commonOverrides,
                    new Expander { Header = "Compatibility definitions · normally automatic", IsExpanded = false, Content = compatibilityOverrides }
                }
            }
        };

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
                            new TextBlock { Text = "Choose the one folder that contains your WoW project. Crucible will locate the client, server, database configuration, game tables, and editor support files by itself—and remember them. You should not need to choose another path afterward.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#A5B0C1")) },
                            new TextBlock { Text = "Saved workspaces", FontWeight = FontWeight.SemiBold },
                            _savedWorkspaces,
                            new TextBlock { Text = "Add or rescan a workspace", FontWeight = FontWeight.SemiBold },
                            rootRow,
                            _pairingPanel,
                            new Border { BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = _summary },
                            backupSettings,
                            advanced,
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
        RefreshSavedWorkspaces();
        _root.Text = _session.Settings.WorkspaceRootPath;
        _name.Text = _session.Settings.WorkspaceName;
        Put("server", _session.Settings.ServerRootPath); Put("core", _session.Settings.CoreSourcePath);
        Put("data", _session.Settings.ClientDataPath); Put("wow", _session.Settings.ClientExecutablePath);
        Put("client", Directory.Exists(_session.Settings.ClientDataPath) ? Directory.GetParent(_session.Settings.ClientDataPath)?.FullName ?? string.Empty : string.Empty);
        Put("dbc", _session.Settings.CoreDbcPath); Put("schema", _session.Settings.SchemaDefinitionPath); Put("dbd", _session.Settings.DbdDefinitionsPath);
        Put("assets", _session.Settings.ProcessedAssetLibraryPath); Put("projects", _session.Settings.WorkspaceProjectsPath); Put("staging", _session.Settings.WorkspaceStagingPath);
        Put("tools", _session.Settings.ToolsPath); Put("noggit", _session.Settings.NoggitExecutablePath); Put("maps", _session.Settings.MapSourcePath);
        _backupsEnabled.IsChecked = _session.Settings.BackupsEnabled;
        _backupRetention.Value = Math.Clamp(_session.Settings.BackupRetentionPerSource, 1, 100);
        _backupStorageLimit.Value = Math.Clamp(_session.Settings.BackupStorageLimitGiB, 1, 1024);
        _backupRoot.Text = _session.Settings.BackupRootPath;
        _summary.Text = string.IsNullOrWhiteSpace(_root.Text)
            ? "Nothing selected yet. Choose the folder containing the project."
            : "Saved workspace loaded. Choose the same folder again only if you want Crucible to rescan it.";
        _status.Text = string.IsNullOrWhiteSpace(_root.Text) ? "Choose a workspace folder to begin." : $"Current workspace: {_root.Text}";
    }

    private void RefreshSavedWorkspaces()
    {
        var profiles = new List<CrucibleWorkspaceLayout>((_session.Settings.SavedWorkspaces ?? []).Where(profile => Directory.Exists(profile.RootPath)));
        foreach (var stored in CrucibleWorkspaceLayoutService.LoadAllProfiles().Where(profile => Directory.Exists(profile.RootPath)))
            if (!profiles.Any(profile => profile.Name.Equals(stored.Name, StringComparison.OrdinalIgnoreCase) && profile.RootPath.Equals(stored.RootPath, StringComparison.OrdinalIgnoreCase) && profile.ServerRootPath.Equals(stored.ServerRootPath, StringComparison.OrdinalIgnoreCase) && profile.ClientRootPath.Equals(stored.ClientRootPath, StringComparison.OrdinalIgnoreCase))) profiles.Add(stored);
        // One-time compatibility import: old versions wrote a manifest into the selected root.
        // It is read but never written there again.
        foreach (var root in (_session.Settings.SavedWorkspaceRoots ?? []).Append(_session.Settings.WorkspaceRootPath).Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && File.Exists(CrucibleWorkspaceLayout.ManifestPath(path))))
        {
            try
            {
                var legacy = CrucibleWorkspaceLayoutService.Load(root);
                if (!profiles.Any(profile => profile.Name.Equals(legacy.Name, StringComparison.OrdinalIgnoreCase) && profile.RootPath.Equals(legacy.RootPath, StringComparison.OrdinalIgnoreCase))) profiles.Add(legacy);
            }
            catch { }
        }
        var choices = profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ThenBy(profile => profile.RootPath, StringComparer.OrdinalIgnoreCase).Select(profile => new WorkspaceChoice(profile)).ToArray();
        _savedWorkspaces.ItemsSource = choices;
        _savedWorkspaces.SelectedItem = choices.FirstOrDefault(choice => choice.Layout.Name.Equals(_session.Settings.WorkspaceName, StringComparison.OrdinalIgnoreCase) && choice.Layout.RootPath.Equals(_session.Settings.WorkspaceRootPath, StringComparison.OrdinalIgnoreCase));
    }

    private Task LoadSelectedWorkspaceAsync()
    {
        if (_savedWorkspaces.SelectedItem is not WorkspaceChoice choice || !Directory.Exists(choice.Layout.RootPath)) return Task.CompletedTask;
        try
        {
            var layout = choice.Layout; _discovered = layout; _discovery = null; Populate(layout); _pairingPanel.IsVisible = false;
            _status.Text = $"Selected saved workspace “{layout.Name}”. Choose “Use this workspace” to switch to it.";
        }
        catch (Exception exception) { _status.Text = $"Could not load that saved workspace: {exception.Message}"; }
        return Task.CompletedTask;
    }

    private async Task BrowseRootAsync()
    {
        var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the top-level WoW workspace", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) { _savedWorkspaces.SelectedItem = null; _root.Text = path; await DiscoverAsync(); }
    }

    private async Task DiscoverAsync()
    {
        try
        {
            _status.Text = "Scanning the workspace without moving or deleting anything…";
            var root = _root.Text ?? string.Empty;
            var discovery = await Task.Run(() => CrucibleWorkspaceLayoutService.DiscoverDetailed(root));
            var layout = discovery.Layout;
            _discovery = discovery;
            _discovered = layout;
            Populate(layout);
            ConfigurePairingChoices(discovery);
            _status.Text = "Scan complete. Review the short summary, then choose “Use this workspace”.";
        }
        catch (Exception exception) { _status.Text = $"Discovery failed: {exception.Message}"; }
    }

    private async Task ApplyAsync()
    {
        try
        {
            if (_discovered is null || !_discovered.RootPath.Equals(Path.GetFullPath(_root.Text ?? string.Empty), StringComparison.OrdinalIgnoreCase)) await DiscoverAsync();
            var layout = ReadLayout();
            _session.Settings.BackupsEnabled = _backupsEnabled.IsChecked == true;
            _session.Settings.BackupChoiceRemembered = true;
            _session.Settings.BackupRetentionPerSource = (int)(_backupRetention.Value ?? 3);
            _session.Settings.BackupStorageLimitGiB = (int)(_backupStorageLimit.Value ?? 10);
            _session.Settings.BackupRootPath = _backupRoot.Text?.Trim() ?? string.Empty;
            CrucibleBackupService.Configure(_session.Settings.BackupRootPath, _session.Settings.BackupsEnabled, _session.Settings.BackupRetentionPerSource, (long)_session.Settings.BackupStorageLimitGiB * 1024 * 1024 * 1024);
            _status.Text = "Saving the workspace and detecting its live server configuration…";
            await _session.ConfigureWorkspaceAsync(layout);
            RefreshSavedWorkspaces();
            _status.Text = _session.DatabaseTested
                ? $"Workspace saved and SQL verified. Crucible will reuse these paths from now on.\nConfiguration: {CrucibleWorkspaceLayoutService.ProfilePath(layout)}"
                : $"Workspace saved. SQL is currently offline; use the SQL light above to start it.\nConfiguration: {CrucibleWorkspaceLayoutService.ProfilePath(layout)}";
        }
        catch (Exception exception)
        {
            _status.Text = _session.Settings.SavedWorkspaces.Any(profile => profile.RootPath.Equals(_root.Text, StringComparison.OrdinalIgnoreCase))
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
        PopulateSummary(layout);
    }

    private void ConfigurePairingChoices(CrucibleWorkspaceDiscovery discovery)
    {
        _serverChoice.ItemsSource = discovery.ServerCandidates;
        _clientChoice.ItemsSource = discovery.ClientCandidates;
        _serverChoice.SelectedItem = discovery.ServerCandidates.Count == 1 ? discovery.ServerCandidates[0] : null;
        _clientChoice.SelectedItem = discovery.ClientCandidates.Count == 1 ? discovery.ClientCandidates[0] : null;
        _pairingPanel.IsVisible = discovery.ServerCandidates.Count > 1 || discovery.ClientCandidates.Count > 1;
        ApplyPairingChoices();
    }

    private void ApplyPairingChoices()
    {
        if (_discovery is null) return;
        if (_serverChoice.SelectedItem is string server)
        {
            Put("server", server);
            Put("dbc", FirstExistingDirectory(Path.Combine(server, "data", "dbc"), Path.Combine(server, "Data", "dbc"), Path.Combine(server, "dbc")) ?? string.Empty);
        }
        if (_clientChoice.SelectedItem is string client)
        {
            Put("client", client); Put("data", Directory.Exists(Path.Combine(client, "Data")) ? Path.Combine(client, "Data") : string.Empty);
            Put("wow", File.Exists(Path.Combine(client, "Wow.exe")) ? Path.Combine(client, "Wow.exe") : string.Empty);
        }
        var selectedServer = Get("server"); var selectedClient = Get("client");
        if (!string.IsNullOrWhiteSpace(selectedServer) && !string.IsNullOrWhiteSpace(selectedClient) &&
            (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == Path.GetFileName((_root.Text ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar))))
            _name.Text = $"{Path.GetFileName(selectedServer.TrimEnd(Path.DirectorySeparatorChar))} + {Path.GetFileName(selectedClient.TrimEnd(Path.DirectorySeparatorChar))}";
        if (_discovered is not null)
        {
            var chosen = _discovered with { ServerRootPath = selectedServer, ClientRootPath = selectedClient, ClientDataPath = Get("data"), ClientExecutablePath = Get("wow"), CoreDbcPath = Get("dbc"), Name = _name.Text ?? _discovered.Name };
            _discovered = chosen; PopulateSummary(chosen);
        }
    }

    private void PopulateSummary(CrucibleWorkspaceLayout layout)
    {
        _summary.Text = string.Join(Environment.NewLine, new[]
        {
            SummaryLine("Game client", layout.ClientRootPath, layout.Findings, "Client"),
            SummaryLine("Server", layout.ServerRootPath, layout.Findings, "Server"),
            SummaryLine("Server game tables", layout.CoreDbcPath),
            SummaryLine("Database connection settings", FindServerConfiguration(layout.ServerRootPath)),
            $"Editor support files: {(File.Exists(layout.SchemaDefinitionPath) ? "ready" : "automatic built-in definitions will be used")}",
            "Projects, temporary staging, and bundled tools: managed automatically inside this workspace"
        });
    }

    private CrucibleWorkspaceLayout ReadLayout()
    {
        if (string.IsNullOrWhiteSpace(_root.Text)) throw new InvalidOperationException("Choose a top-level workspace folder first.");
        var root = Path.GetFullPath(_root.Text.Trim());
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Workspace folder does not exist: {root}");
        if (_discovery?.ServerCandidates.Count > 1 && string.IsNullOrWhiteSpace(Get("server"))) throw new InvalidOperationException("Choose which server belongs to this workspace.");
        if (_discovery?.ClientCandidates.Count > 1 && string.IsNullOrWhiteSpace(Get("client"))) throw new InvalidOperationException("Choose which game client belongs to this workspace.");
        var name = string.IsNullOrWhiteSpace(_name.Text) ? Path.GetFileName(root) : _name.Text.Trim();
        var client = Get("client"); var server = Get("server");
        var data = Directory.Exists(Path.Combine(client, "Data")) ? Path.Combine(client, "Data") : Get("data");
        var wow = File.Exists(Path.Combine(client, "Wow.exe")) ? Path.Combine(client, "Wow.exe") : Get("wow");
        var dbc = FirstExistingDirectory(Path.Combine(server, "data", "dbc"), Path.Combine(server, "Data", "dbc"), Path.Combine(server, "dbc")) ?? Get("dbc");
        var hasLibraryChoices = (_discovery?.ServerCandidates.Count ?? 0) > 1 || (_discovery?.ClientCandidates.Count ?? 0) > 1;
        var profileRoot = Path.Combine(CruciblePaths.ProfilesDirectory, "Workspaces", SafeName(name));
        var projects = hasLibraryChoices ? Path.Combine(profileRoot, "Projects") : Get("projects");
        var staging = hasLibraryChoices ? Path.Combine(profileRoot, "Staging") : Get("staging");
        return new(CrucibleWorkspaceLayout.CurrentFormatVersion, name, root,
            server, Get("core"), client, data, wow, dbc, Get("schema"), Get("dbd"), Get("assets"),
            projects, staging, Get("tools"), Get("noggit"), Get("maps"), _findings);
    }

    private void AddExistingPathRow(Grid form, string label, string key, bool file)
    {
        var input = _paths[key];
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
    private string Get(string key) => _paths[key].Text?.Trim() ?? string.Empty;
    private void Put(string key, string value) => _paths[key].Text = value;
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T AtColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T AtRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static string SummaryLine(string label, string path, IReadOnlyList<string>? findings = null, string? findingPrefix = null)
    {
        if (!string.IsNullOrWhiteSpace(path) && (Directory.Exists(path) || File.Exists(path))) return $"{label}: found · {path}";
        var ambiguous = findings?.FirstOrDefault(finding => finding.StartsWith(findingPrefix + ":", StringComparison.OrdinalIgnoreCase) && finding.Contains("AMBIGUOUS", StringComparison.OrdinalIgnoreCase));
        return ambiguous is null ? $"{label}: not found" : $"{label}: multiple installs found · choose the intended one under Advanced overrides";
    }
    private static string FindServerConfiguration(string server)
    {
        if (string.IsNullOrWhiteSpace(server) || !Directory.Exists(server)) return string.Empty;
        try { return Directory.EnumerateFiles(server, "worldserver.conf", SearchOption.AllDirectories).FirstOrDefault() ?? Directory.EnumerateFiles(server, "worldserver.conf.dist", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty; }
        catch { return string.Empty; }
    }
    private static string? FirstExistingDirectory(params string[] candidates) => candidates.FirstOrDefault(Directory.Exists);
    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet(); var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return safe.Length == 0 ? "workspace" : safe;
    }
}
