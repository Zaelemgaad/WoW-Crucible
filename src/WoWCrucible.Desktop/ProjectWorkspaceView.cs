using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ProjectWorkspaceView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _root = new() { PlaceholderText = "Crucible project folder…" };
    private readonly TextBox _name = new() { PlaceholderText = "Project name" };
    private readonly ComboBox _target = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _assetLibrary = new() { PlaceholderText = "Optional processed asset library…" };
    private readonly TextBox _dbc = new() { PlaceholderText = "Authoritative server DBC folder…" };
    private readonly TextBox _schema = new() { PlaceholderText = "Matching WDBX schema XML…" };
    private readonly TextBox _manualIds = new() { PlaceholderText = "Optional occupied-ID text file for custom/unmapped domains…" };
    private readonly ComboBox _domain = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _count = new() { Text = "1", PlaceholderText = "Reservation count" };
    private readonly TextBox _start = new() { PlaceholderText = "Use recommended start" };
    private readonly TextBox _purpose = new() { PlaceholderText = "What these IDs are for" };
    private readonly TextBlock _projectSummary = Status("Create or open a portable Crucible content project.");
    private readonly TextBlock _policy = Status(string.Empty);
    private readonly TextBlock _status = Status("No occupancy scan has been run.");
    private readonly ListBox _sources = new();
    private readonly ListBox _reservations = new();
    private IReadOnlyList<TargetProfile> _profiles = [];
    private ContentIdOccupancyReport? _report;
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;
    public event EventHandler? ServerSqlRequested;

    public ProjectWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged;
        _profiles = TargetProfileCatalog.Load(); _target.ItemsSource = _profiles; _target.SelectedItem = TargetProfileCatalog.Find(_profiles, TargetProfileCatalog.DefaultProfileId);
        _domain.ItemsSource = ContentIdDomainCatalog.All; _domain.SelectedItem = ContentIdDomainCatalog.Get(ContentIdDomain.Item);
        _dbc.Text = session.Settings.CoreDbcPath; _schema.Text = session.Settings.SchemaDefinitionPath; _root.Text = session.Settings.ActiveProjectPath;
        _domain.SelectionChanged += (_, _) => { InvalidateReport(); ShowPolicy(); };
        _dbc.TextChanged += (_, _) => InvalidateReport(); _schema.TextChanged += (_, _) => InvalidateReport(); _manualIds.TextChanged += (_, _) => InvalidateReport();

        _sources.ItemTemplate = new FuncDataTemplate<ContentIdOccupancySource>((source, _) => source is null ? new TextBlock() : new Grid
        {
            ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(5, 4), Children =
            {
                new TextBlock { Text = source.Available ? "READY" : "MISSING", Foreground = Brush.Parse(source.Available ? "#79B58A" : "#D96C68"), FontWeight = FontWeight.Bold },
                WithColumn(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = $"{source.Kind} · {source.Name}", FontWeight = FontWeight.SemiBold }, new TextBlock { Text = $"{source.Location}\n{source.Detail}", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A8"), FontSize = 10 } } }, 1),
                WithColumn(new TextBlock { Text = source.Ids.ToString("N0"), VerticalAlignment = VerticalAlignment.Center }, 2)
            }
        });
        _reservations.ItemTemplate = new FuncDataTemplate<ContentIdReservation>((reservation, _) => reservation is null ? new TextBlock() : new StackPanel
        {
            Spacing = 2, Margin = new Thickness(5, 4), Children =
            {
                new TextBlock { Text = $"{reservation.Domain} · {reservation.Values.Count:N0} ID(s) · {Range(reservation.Values)}", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"{reservation.Purpose} · {reservation.CreatedUtc.LocalDateTime:g}", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A8"), FontSize = 10 }
            }
        });

        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var create = Accent("Create project"); create.Click += async (_, _) => await CreateAsync();
        var open = new Button { Content = "Open project" }; open.Click += async (_, _) => await OpenAsync();
        var browseRoot = new Button { Content = "Browse…" }; browseRoot.Click += async (_, _) => await PickFolderAsync(_root, "Choose a Crucible project folder");
        var browseAssets = new Button { Content = "Browse…" }; browseAssets.Click += async (_, _) => await PickFolderAsync(_assetLibrary, "Choose an optional processed asset library");
        var browseDbc = new Button { Content = "Browse…" }; browseDbc.Click += async (_, _) => await PickFolderAsync(_dbc, "Choose the authoritative DBC folder");
        var browseSchema = new Button { Content = "Browse…" }; browseSchema.Click += async (_, _) => await PickFileAsync(_schema, "Choose the matching WDBX schema", "*.xml");
        var browseManual = new Button { Content = "Browse…" }; browseManual.Click += async (_, _) => await PickFileAsync(_manualIds, "Choose an occupied-ID list", "*.txt");
        var scan = Accent("Scan authoritative occupancy"); scan.Click += async (_, _) => await ScanAsync();
        var reserve = Accent("Reserve collision-checked IDs"); reserve.Click += async (_, _) => await ReserveAsync();
        var connect = new Button { Content = "Connect Server & SQL" }; connect.Click += (_, _) => ServerSqlRequested?.Invoke(this, EventArgs.Empty);
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _operation?.Cancel();

        var heading = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8), Child = new WrapPanel { Children = { back, new TextBlock { Text = "PROJECTS & SHARED IDS", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, create, open } } };
        var projectForm = Form(("Project folder", Row(_root, browseRoot)), ("Project name", _name), ("Target profile", _target), ("Asset library", Row(_assetLibrary, browseAssets)));
        var sourceForm = Form(("DBC folder", Row(_dbc, browseDbc)), ("Schema XML", Row(_schema, browseSchema)), ("Manual occupied IDs", Row(_manualIds, browseManual)));
        var allocationForm = Form(("ID domain", _domain), ("Count", _count), ("Start ID", _start), ("Purpose", _purpose));
        var configuration = new ScrollViewer { Content = new StackPanel { Spacing = 10, Margin = new Thickness(12), Children = { new TextBlock { Text = "Portable project", FontSize = 17, FontWeight = FontWeight.SemiBold }, projectForm, _projectSummary, new TextBlock { Text = "Authoritative occupancy", FontSize = 17, FontWeight = FontWeight.SemiBold }, sourceForm, new TextBlock { Text = "Reservation", FontSize = 17, FontWeight = FontWeight.SemiBold }, allocationForm, _policy, new WrapPanel { Children = { connect, scan, reserve, cancel } }, _status } } };
        var evidence = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,*"), RowSpacing = 7, Margin = new Thickness(12), Children = { new TextBlock { Text = "Occupancy sources", FontSize = 17, FontWeight = FontWeight.SemiBold }, WithRow(_sources, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(new TextBlock { Text = "Project reservations", FontSize = 17, FontWeight = FontWeight.SemiBold }, 3), WithRow(_reservations, 4) } };
        var body = new ResponsiveSplitGrid(configuration, evidence);
        Content = new Grid { RowDefinitions = new("Auto,*"), Children = { heading, WithRow(body, 1) } };
        ShowPolicy(); TryLoadConfiguredProject();
    }

    public void Activate()
    {
        if (string.IsNullOrWhiteSpace(_dbc.Text)) _dbc.Text = _session.Settings.CoreDbcPath;
        if (string.IsNullOrWhiteSpace(_schema.Text)) _schema.Text = _session.Settings.SchemaDefinitionPath;
        if (!string.IsNullOrWhiteSpace(_session.Settings.ActiveProjectPath) && !string.Equals(_root.Text, _session.Settings.ActiveProjectPath, StringComparison.OrdinalIgnoreCase)) _root.Text = _session.Settings.ActiveProjectPath;
        TryLoadConfiguredProject();
    }

    private async Task CreateAsync()
    {
        try
        {
            var root = RequiredPath(_root.Text, "Choose a project folder first."); var name = (_name.Text ?? string.Empty).Trim(); if (name.Length == 0) throw new InvalidOperationException("Enter a project name.");
            var target = (_target.SelectedItem as TargetProfile)?.Id ?? TargetProfileCatalog.DefaultProfileId; var project = CrucibleContentProjectService.Create(root, name, target, EmptyNull(_assetLibrary.Text));
            SetActive(root); ShowProject(project); _status.Text = $"Created {project.Name}. Run occupancy before reserving deployable IDs.";
        }
        catch (Exception exception) { Fail("Project creation failed", exception); }
        await Task.CompletedTask;
    }

    private async Task OpenAsync()
    {
        try { var root = RequiredPath(_root.Text, "Choose a project folder first."); var project = CrucibleContentProjectService.Load(root); SetActive(root); ShowProject(project); _status.Text = $"Opened {project.Name}."; }
        catch (Exception exception) { Fail("Project open failed", exception); }
        await Task.CompletedTask;
    }

    private async Task ScanAsync()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        try
        {
            var policy = SelectedPolicy(); var manual = ReadManual(); _status.Text = $"Scanning {policy.Domain} occupancy…";
            _report = await new ContentIdOccupancyService().InspectAsync(policy.Domain, _session.DatabaseProfile, _session.DatabaseCapabilities, EmptyNull(_dbc.Text), EmptyNull(_schema.Text), manual, _operation.Token);
            _sources.ItemsSource = _report.Sources; _status.Text = _report.Complete
                ? $"Complete · {_report.OccupiedIds.Count:N0} occupied {policy.RegistryNamespace} ID(s) · maximum {_report.MaximumOccupied?.ToString("N0") ?? "none"}. Reservation is enabled."
                : $"Incomplete · refusing reservations. {string.Join(" ", _report.Warnings)}";
            _session.Settings.CoreDbcPath = _dbc.Text ?? string.Empty; _session.Settings.SchemaDefinitionPath = _schema.Text ?? string.Empty; _session.Settings.Save();
            DesktopCrashLogger.Debug("PROJECT", "occupancy-scan", ("domain", policy.Domain), ("complete", _report.Complete), ("occupied", _report.OccupiedIds.Count), ("sources", _report.Sources.Count));
        }
        catch (OperationCanceledException) { _status.Text = "Occupancy scan cancelled."; }
        catch (Exception exception) { Fail("Occupancy scan failed", exception); }
    }

    private async Task ReserveAsync()
    {
        try
        {
            var root = RequiredPath(_root.Text, "Open or create a project first."); _ = CrucibleContentProjectService.Load(root);
            if (_report is null || _report.Domain != SelectedPolicy().Domain) { await ScanAsync(); if (_report is null) return; }
            if (!int.TryParse(_count.Text, out var count) || count is < 1 or > 1_000_000) throw new FormatException("Count must be from 1 through 1,000,000.");
            uint? start = null; if (!string.IsNullOrWhiteSpace(_start.Text)) { if (!uint.TryParse(_start.Text, out var parsed)) throw new FormatException("Start ID must be an unsigned integer."); start = parsed; }
            var result = CrucibleContentProjectService.ReserveVerifiedIds(root, _report, count, start, _purpose.Text ?? string.Empty);
            _reservations.ItemsSource = result.Registry.Reservations.OrderByDescending(value => value.CreatedUtc).ToArray(); _status.Text = $"Reserved {result.Reservation.Values.Count:N0} {result.Reservation.Domain} ID(s): {Range(result.Reservation.Values)}.";
            DesktopCrashLogger.Debug("PROJECT", "ids-reserved", ("domain", result.Reservation.Domain), ("count", result.Reservation.Values.Count), ("range", Range(result.Reservation.Values)), ("project", root));
        }
        catch (Exception exception) { Fail("ID reservation failed", exception); }
    }

    private void TryLoadConfiguredProject()
    {
        if (string.IsNullOrWhiteSpace(_root.Text)) return;
        try { ShowProject(CrucibleContentProjectService.Load(_root.Text)); }
        catch { _projectSummary.Text = "The selected folder is not an existing Crucible project yet."; _reservations.ItemsSource = Array.Empty<ContentIdReservation>(); }
    }

    private void ShowProject(CrucibleContentProject project)
    {
        _name.Text = project.Name; _assetLibrary.Text = project.AssetLibrary ?? string.Empty; _target.SelectedItem = TargetProfileCatalog.Find(_profiles, project.TargetProfile);
        var registry = CrucibleContentProjectService.LoadRegistry(_root.Text!); _reservations.ItemsSource = registry.Reservations.OrderByDescending(value => value.CreatedUtc).ToArray();
        _projectSummary.Text = $"{project.Name} · {project.TargetProfile} · {registry.Reservations.Count:N0} reservation(s) · {registry.Reservations.Sum(value => value.Values.Count):N0} reserved ID(s)";
    }

    private void ShowPolicy()
    {
        var policy = SelectedPolicy(); _start.PlaceholderText = $"Recommended: {policy.RecommendedStart:N0}";
        _policy.Text = $"Registry namespace: {policy.RegistryNamespace} · valid through {policy.Maximum:N0}. {policy.Guidance}";
    }

    private ContentIdDomainPolicy SelectedPolicy() => _domain.SelectedItem as ContentIdDomainPolicy ?? ContentIdDomainCatalog.Get(ContentIdDomain.Item);
    private IReadOnlyList<uint>? ReadManual() => string.IsNullOrWhiteSpace(_manualIds.Text) ? null : CrucibleContentProjectService.ReadOccupiedIds(_manualIds.Text);
    private void SetActive(string root) { root = Path.GetFullPath(root); _root.Text = root; _session.Settings.ActiveProjectPath = root; _session.Settings.Save(); }
    private void InvalidateReport() { _report = null; _sources.ItemsSource = Array.Empty<ContentIdOccupancySource>(); _status.Text = "Occupancy inputs changed · scan again before reserving."; }
    private void SessionChanged(object? sender, EventArgs e) => InvalidateReport();
    private void Fail(string operation, Exception exception) { _status.Text = $"{operation}: {exception.Message}"; DesktopCrashLogger.Log(operation, exception); }
    private static string RequiredPath(string? value, string message) { if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(message); return Path.GetFullPath(value.Trim()); }
    private static string? EmptyNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Range(IReadOnlyList<uint> values) => values.Count == 0 ? "none" : values.Count == 1 ? values[0].ToString("N0") : $"{values.Min():N0}–{values.Max():N0}";
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
    private static Grid Form(params (string Label, Control Field)[] rows) { var grid = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", rows.Length))), RowSpacing = 5, ColumnSpacing = 8 }; for (var index = 0; index < rows.Length; index++) { var label = new TextBlock { Text = rows[index].Label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(label, index); grid.Children.Add(label); Grid.SetRow(rows[index].Field, index); Grid.SetColumn(rows[index].Field, 1); grid.Children.Add(rows[index].Field); } return grid; }
    private static Grid Row(Control field, Control action) { var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7, Children = { field, WithColumn(action, 1) } }; return grid; }
    private async Task PickFolderAsync(TextBox target, string title) { var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The project workspace is not attached to the main window."); var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }); var path = result.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) target.Text = path; }
    private async Task PickFileAsync(TextBox target, string title, string pattern) { var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The project workspace is not attached to the main window."); var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }] }); var path = result.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) target.Text = path; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); _session.Changed -= SessionChanged; }
}
