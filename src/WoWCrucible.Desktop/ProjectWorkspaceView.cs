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
    private readonly TextBox _classSource = new() { Text = "1", PlaceholderText = "Existing source class ID" };
    private readonly TextBox _classTarget = new() { PlaceholderText = "Reserved target class ID" };
    private readonly TextBox _className = new() { PlaceholderText = "New playable class name" };
    private readonly TextBox _classToken = new() { PlaceholderText = "Uppercase client file token" };
    private readonly TextBox _classPower = new() { PlaceholderText = "Leave blank to clone source power" };
    private readonly TextBox _classOutput = new() { PlaceholderText = "New/empty bundle output folder…" };
    private readonly TextBlock _classStatus = Status("Choose a reserved Class ID, then create a read-only dependency plan.");
    private readonly ListBox _classDetails = new();
    private IReadOnlyList<TargetProfile> _profiles = [];
    private ContentIdOccupancyReport? _report;
    private PlayableClassClonePlan? _classPlan;
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
        _root.TextChanged += (_, _) => InvalidateClassPlan();
        _dbc.TextChanged += (_, _) => { InvalidateReport(); InvalidateClassPlan(); }; _schema.TextChanged += (_, _) => { InvalidateReport(); InvalidateClassPlan(); }; _manualIds.TextChanged += (_, _) => InvalidateReport();
        foreach (var field in new[] { _classSource, _classTarget, _className, _classToken, _classPower }) field.TextChanged += (_, _) => InvalidateClassPlan();

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
        var connectClass = new Button { Content = "Connect Server & SQL" }; connectClass.Click += (_, _) => ServerSqlRequested?.Invoke(this, EventArgs.Empty);
        var cancelClass = new Button { Content = "Cancel" }; cancelClass.Click += (_, _) => _operation?.Cancel();
        var latestClass = new Button { Content = "Use latest reserved Class ID" }; latestClass.Click += (_, _) => UseLatestClassReservation();
        var browseClassOutput = new Button { Content = "Browse…" }; browseClassOutput.Click += async (_, _) => await PickFolderAsync(_classOutput, "Choose a new or empty playable-class bundle folder");
        var classPlan = Accent("Create dependency plan"); classPlan.Click += async (_, _) => await PlanClassAsync();
        var classBuild = Accent("Build reviewed bundle"); classBuild.Click += async (_, _) => await BuildClassAsync();

        var heading = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8), Child = new WrapPanel { Children = { back, new TextBlock { Text = "PROJECTS & SHARED IDS", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, create, open } } };
        var projectForm = Form(("Project folder", Row(_root, browseRoot)), ("Project name", _name), ("Target profile", _target), ("Asset library", Row(_assetLibrary, browseAssets)));
        var sourceForm = Form(("DBC folder", Row(_dbc, browseDbc)), ("Schema XML", Row(_schema, browseSchema)), ("Manual occupied IDs", Row(_manualIds, browseManual)));
        var allocationForm = Form(("ID domain", _domain), ("Count", _count), ("Start ID", _start), ("Purpose", _purpose));
        var configuration = new ScrollViewer { Content = new StackPanel { Spacing = 10, Margin = new Thickness(12), Children = { new TextBlock { Text = "Portable project", FontSize = 17, FontWeight = FontWeight.SemiBold }, projectForm, _projectSummary, new TextBlock { Text = "Authoritative occupancy", FontSize = 17, FontWeight = FontWeight.SemiBold }, sourceForm, new TextBlock { Text = "Reservation", FontSize = 17, FontWeight = FontWeight.SemiBold }, allocationForm, _policy, new WrapPanel { Children = { connect, scan, reserve, cancel } }, _status } } };
        var evidence = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,*"), RowSpacing = 7, Margin = new Thickness(12), Children = { new TextBlock { Text = "Occupancy sources", FontSize = 17, FontWeight = FontWeight.SemiBold }, WithRow(_sources, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(new TextBlock { Text = "Project reservations", FontSize = 17, FontWeight = FontWeight.SemiBold }, 3), WithRow(_reservations, 4) } };
        var body = new ResponsiveSplitGrid(configuration, evidence);
        var classForm = Form(
            ("Source class ID", _classSource),
            ("Reserved target ID", Row(_classTarget, latestClass)),
            ("Class name", _className),
            ("Client file token", _classToken),
            ("Primary display power", _classPower),
            ("Bundle output", Row(_classOutput, browseClassOutput)));
        var classConfiguration = new ScrollViewer { Content = new StackPanel { Spacing = 10, Margin = new Thickness(12), Children =
        {
            new TextBlock { Text = "PLAYABLE CLASS BUNDLE", FontSize = 17, FontWeight = FontWeight.SemiBold },
            Status("Creates a reviewable, additive WotLK 3.3.5a bundle from a complete existing class row plus its playable races, outfits, access masks, and recognized SQL starting/stat data. It never edits the live server, database, or client."),
            classForm,
            new WrapPanel { Children = { connectClass, classPlan, classBuild, cancelClass } },
            _classStatus,
            Status("DisplayPower selects one primary stock client bar. Multi-resource classes still require explicit core, spell, form, and UI work; Crucible reports that boundary instead of hiding it.")
        } } };
        var classEvidence = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 7, Margin = new Thickness(12), Children = { new TextBlock { Text = "Dependency evidence", FontSize = 17, FontWeight = FontWeight.SemiBold }, WithRow(_classDetails, 1) } };
        var classBody = new ResponsiveSplitGrid(classConfiguration, classEvidence);
        var tabs = new TabControl { ItemsSource = new[]
        {
            new TabItem { Header = "ID reservations", Content = body },
            new TabItem { Header = "Playable class bundle", Content = classBody }
        } };
        Content = new Grid { RowDefinitions = new("Auto,*"), Children = { heading, WithRow(tabs, 1) } };
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

    private async Task PlanClassAsync()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        try
        {
            var (root, source, target, name, token, power) = ClassInputs();
            var profile = _session.DatabaseProfile ?? throw new InvalidOperationException("Connect Server & SQL first so Crucible can inspect authoritative class and player-create tables.");
            var capabilities = _session.DatabaseCapabilities ?? throw new InvalidOperationException("Connect Server & SQL first so Crucible can inspect authoritative class and player-create tables.");
            _classStatus.Text = $"Inspecting every dependency for class {source:N0} -> {target:N0}…";
            _classPlan = await new PlayableClassCloneService().CreatePlanAsync(root, RequiredPath(_dbc.Text, "Choose the authoritative DBC folder."), RequiredPath(_schema.Text, "Choose the matching WDBX schema XML."), source, target, name, token, power, profile, capabilities, _operation.Token);
            ShowClassPlan(_classPlan);
            _session.Settings.CoreDbcPath = _dbc.Text ?? string.Empty; _session.Settings.SchemaDefinitionPath = _schema.Text ?? string.Empty; _session.Settings.Save();
            DesktopCrashLogger.Debug("PROJECT", "class-plan", ("sourceClass", source), ("targetClass", target), ("ready", _classPlan.Ready), ("dbcRows", _classPlan.DbcTables.Sum(table => table.AffectedRows)), ("sqlRows", _classPlan.SqlRows));
        }
        catch (OperationCanceledException) { _classStatus.Text = "Playable-class planning cancelled."; }
        catch (Exception exception) { FailClass("Playable-class planning failed", exception); }
    }

    private async Task BuildClassAsync()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        try
        {
            if (_classPlan is null) { await PlanClassAsync(); if (_classPlan is null) return; }
            if (!_classPlan.Ready) throw new InvalidOperationException($"Resolve the {_classPlan.Blockers.Count:N0} dependency blocker(s) before building.");
            var profile = _session.DatabaseProfile ?? throw new InvalidOperationException("Reconnect Server & SQL before building.");
            var output = RequiredPath(_classOutput.Text, "Choose a new or empty bundle output folder.");
            _classStatus.Text = "Revalidating bound DBC/SQL inputs and building a new reviewable bundle…";
            var result = await new PlayableClassCloneService().BuildAsync(_classPlan, profile, output, _operation.Token);
            _classStatus.Text = $"Built class {_classPlan.TargetClassId:N0} bundle. Patch: {result.PatchPath} · SQL: {result.SqlPath} · receipt: {result.ReceiptPath}";
            DesktopCrashLogger.Debug("PROJECT", "class-bundle-built", ("class", _classPlan.TargetClassId), ("output", result.OutputRoot), ("patch", result.PatchPath));
        }
        catch (OperationCanceledException) { _classStatus.Text = "Playable-class build cancelled."; }
        catch (Exception exception) { FailClass("Playable-class build failed", exception); }
    }

    private (string Root, uint Source, uint Target, string Name, string Token, uint? Power) ClassInputs()
    {
        var root = RequiredPath(_root.Text, "Open or create a Crucible project first."); _ = CrucibleContentProjectService.Load(root);
        if (!uint.TryParse(_classSource.Text, out var source) || source is 0 or > 31) throw new FormatException("Source class ID must be from 1 through 31.");
        if (!uint.TryParse(_classTarget.Text, out var target) || target is 0 or > 31) throw new FormatException("Target class ID must be from 1 through 31.");
        var name = (_className.Text ?? string.Empty).Trim(); if (name.Length == 0) throw new FormatException("Enter the new class name.");
        var token = (_classToken.Text ?? string.Empty).Trim(); if (token.Length == 0) throw new FormatException("Enter the client file token, such as ADVENTURER.");
        uint? power = null; if (!string.IsNullOrWhiteSpace(_classPower.Text)) { if (!uint.TryParse(_classPower.Text, out var parsed)) throw new FormatException("Primary display power must be an unsigned integer."); power = parsed; }
        return (root, source, target, name, token, power);
    }

    private void ShowClassPlan(PlayableClassClonePlan plan)
    {
        var details = new List<string>
        {
            $"SOURCE  {plan.SourceClassId:N0} · {plan.SourceClassName}",
            $"TARGET  {plan.TargetClassId:N0} · {plan.TargetClassName} · {plan.TargetFileToken}",
            $"POWER   {plan.DisplayPower:N0}",
            $"BINDING {plan.ContentSha256}"
        };
        details.AddRange(plan.DbcTables.Select(table => $"DBC     {table.Table} · {table.AffectedRows:N0} row(s) · {table.Action}"));
        details.AddRange(plan.SqlTables.Select(table => $"SQL     {table.Table} · source {table.SourceRows:N0} · plan {table.Rows.Count:N0} · covered {table.AlreadyCovered:N0} · conflicts {table.Conflicts:N0}"));
        details.AddRange(plan.Blockers.Select(value => $"BLOCKER {value}")); details.AddRange(plan.Warnings.Select(value => $"WARNING {value}")); _classDetails.ItemsSource = details;
        _classStatus.Text = plan.Ready ? $"Ready · {plan.DbcTables.Sum(table => table.AffectedRows):N0} DBC row operation(s) · {plan.SqlRows:N0} additive SQL row(s). Review the evidence, then build." : $"Blocked · {plan.Blockers.Count:N0} issue(s). Nothing can be built until they are resolved.";
        if (string.IsNullOrWhiteSpace(_classOutput.Text)) _classOutput.Text = Path.Combine(plan.ProjectRoot, "Staging", $"Class-{plan.TargetClassId}-{plan.TargetFileToken}");
    }

    private void UseLatestClassReservation()
    {
        try
        {
            var root = RequiredPath(_root.Text, "Open or create a Crucible project first."); var reservation = CrucibleContentProjectService.LoadRegistry(root).Reservations.Where(value => value.Domain == ContentIdDomain.Class).OrderByDescending(value => value.CreatedUtc).FirstOrDefault() ?? throw new InvalidOperationException("This project has no reserved Class ID yet.");
            _classTarget.Text = reservation.Values.Last().ToString();
        }
        catch (Exception exception) { FailClass("Could not load a reserved Class ID", exception); }
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
    private void InvalidateClassPlan() { _classPlan = null; _classDetails.ItemsSource = Array.Empty<string>(); _classStatus.Text = "Class inputs changed · create a fresh dependency plan before building."; }
    private void SessionChanged(object? sender, EventArgs e) { InvalidateReport(); InvalidateClassPlan(); }
    private void Fail(string operation, Exception exception) { _status.Text = $"{operation}: {exception.Message}"; DesktopCrashLogger.Log(operation, exception); }
    private void FailClass(string operation, Exception exception) { _classStatus.Text = $"{operation}: {exception.Message}"; DesktopCrashLogger.Log(operation, exception); }
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
