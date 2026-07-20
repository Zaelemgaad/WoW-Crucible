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
    private readonly TextBox _raceSource = new() { Text = "1", PlaceholderText = "Existing source race ID" };
    private readonly TextBox _raceTarget = new() { PlaceholderText = "Reserved unoccupied race ID" };
    private readonly TextBox _raceName = new() { PlaceholderText = "New playable race name" };
    private readonly TextBox _racePrefix = new() { PlaceholderText = "1–4 character client prefix" };
    private readonly TextBox _raceToken = new() { PlaceholderText = "Client file/path token" };
    private readonly TextBox _raceMaleDisplay = new() { PlaceholderText = "Optional reviewed male CreatureDisplayInfo ID" };
    private readonly TextBox _raceFemaleDisplay = new() { PlaceholderText = "Optional reviewed female CreatureDisplayInfo ID" };
    private readonly TextBox _raceAppearanceSource = new() { PlaceholderText = "Optional extracted source-layer DBC folder to promote from" };
    private readonly TextBox _raceAssetLibrary = new() { PlaceholderText = "Optional processed asset library for exact closure" };
    private readonly TextBox _raceAssetProvenance = new() { PlaceholderText = "Optional exact provenance; blank requires unambiguous bytes" };
    private readonly TextBox _raceOutput = new() { PlaceholderText = "New/empty bundle output folder…" };
    private readonly TextBlock _raceStatus = Status("Choose a genuinely unoccupied reserved Race ID, then create a read-only dependency plan.");
    private readonly ListBox _raceDetails = new();
    private IReadOnlyList<TargetProfile> _profiles = [];
    private ContentIdOccupancyReport? _report;
    private PlayableClassClonePlan? _classPlan;
    private PlayableRaceClonePlan? _racePlan;
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
        _root.TextChanged += (_, _) => { InvalidateClassPlan(); InvalidateRacePlan(); };
        _dbc.TextChanged += (_, _) => { InvalidateReport(); InvalidateClassPlan(); InvalidateRacePlan(); }; _schema.TextChanged += (_, _) => { InvalidateReport(); InvalidateClassPlan(); InvalidateRacePlan(); }; _manualIds.TextChanged += (_, _) => InvalidateReport();
        foreach (var field in new[] { _classSource, _classTarget, _className, _classToken, _classPower }) field.TextChanged += (_, _) => InvalidateClassPlan();
        foreach (var field in new[] { _raceSource, _raceTarget, _raceName, _racePrefix, _raceToken, _raceMaleDisplay, _raceFemaleDisplay, _raceAppearanceSource, _raceAssetLibrary, _raceAssetProvenance }) field.TextChanged += (_, _) => InvalidateRacePlan();

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
        var connectRace = new Button { Content = "Connect Server & SQL" }; connectRace.Click += (_, _) => ServerSqlRequested?.Invoke(this, EventArgs.Empty);
        var cancelRace = new Button { Content = "Cancel" }; cancelRace.Click += (_, _) => _operation?.Cancel();
        var latestRace = new Button { Content = "Use latest reserved Race ID" }; latestRace.Click += (_, _) => UseLatestRaceReservation();
        var browseRaceOutput = new Button { Content = "Browse…" }; browseRaceOutput.Click += async (_, _) => await PickFolderAsync(_raceOutput, "Choose a new or empty playable-race bundle folder");
        var browseRaceAssets = new Button { Content = "Browse…" }; browseRaceAssets.Click += async (_, _) => await PickFolderAsync(_raceAssetLibrary, "Choose the processed asset library for exact race appearance closure");
        var browseRaceAppearanceSource = new Button { Content = "Browse…" }; browseRaceAppearanceSource.Click += async (_, _) => await PickFolderAsync(_raceAppearanceSource, "Choose extracted source-layer DBCs containing the reviewed displays");
        var useProjectRaceAssets = new Button { Content = "Use project library" }; useProjectRaceAssets.Click += (_, _) => { if (string.IsNullOrWhiteSpace(_assetLibrary.Text)) _raceStatus.Text = "This project has no asset library configured."; else _raceAssetLibrary.Text = _assetLibrary.Text; };
        var racePlan = Accent("Create dependency plan"); racePlan.Click += async (_, _) => await PlanRaceAsync();
        var raceBuild = Accent("Build reviewed bundle"); raceBuild.Click += async (_, _) => await BuildRaceAsync();

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
        var raceForm = Form(("Source race ID", _raceSource), ("Reserved target ID", Row(_raceTarget, latestRace)), ("Race name", _raceName), ("Client prefix", _racePrefix), ("Client file token", _raceToken), ("Male display", _raceMaleDisplay), ("Female display", _raceFemaleDisplay), ("Display source DBCs", Row(_raceAppearanceSource, browseRaceAppearanceSource)), ("Asset library", Row(_raceAssetLibrary, new WrapPanel { Children = { useProjectRaceAssets, browseRaceAssets } })), ("Asset provenance", _raceAssetProvenance), ("Bundle output", Row(_raceOutput, browseRaceOutput)));
        var raceConfiguration = new ScrollViewer { Content = new StackPanel { Spacing = 10, Margin = new Thickness(12), Children =
        {
            new TextBlock { Text = "PLAYABLE RACE BUNDLE", FontSize = 17, FontWeight = FontWeight.SemiBold },
            Status("Clones the complete WotLK character-creation surface for one source race: race identity, playable classes, appearances, barber/hair/facial options, outfits, names, vocal/emote data, faction reputation masks, skills, talents, and recognized SQL starting/stat rows. Nothing is applied live."),
            raceForm, new WrapPanel { Children = { connectRace, racePlan, raceBuild, cancelRace } }, _raceStatus,
            Status("Blank display fields reuse the source race. Without Display source DBCs, reviewed overrides must already exist in the authoritative folder. With a source DBC folder, both display fields identify source-layer records and Crucible promotes their complete model/display dependencies together with semantic deduplication and collision-safe ID/reference remapping. A processed library packages the exact same-provenance M2/SKIN/texture closure.")
        } } };
        var raceEvidence = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 7, Margin = new Thickness(12), Children = { new TextBlock { Text = "Dependency evidence", FontSize = 17, FontWeight = FontWeight.SemiBold }, WithRow(_raceDetails, 1) } };
        var raceBody = new ResponsiveSplitGrid(raceConfiguration, raceEvidence);
        var tabs = new TabControl { ItemsSource = new[]
        {
            new TabItem { Header = "ID reservations", Content = body },
            new TabItem { Header = "Playable class bundle", Content = classBody },
            new TabItem { Header = "Playable race bundle", Content = raceBody }
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
        _classStatus.Text = plan.Ready ? $"Ready · {plan.DbcTables.Sum(table => table.AffectedRows):N0} DBC row operation(s) · {plan.SqlRows:N0} reviewed SQL operation(s). Review the evidence, then build." : $"Blocked · {plan.Blockers.Count:N0} issue(s). Nothing can be built until they are resolved.";
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

    private async Task PlanRaceAsync()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        try
        {
            var (root, source, target, name, prefix, token, appearance) = RaceInputs(); var profile = _session.DatabaseProfile ?? throw new InvalidOperationException("Connect Server & SQL first so Crucible can inspect authoritative race and player-create tables."); var capabilities = _session.DatabaseCapabilities ?? throw new InvalidOperationException("Connect Server & SQL first so Crucible can inspect authoritative race and player-create tables.");
            _raceStatus.Text = $"Inspecting every dependency for race {source:N0} -> {target:N0}…"; _racePlan = await new PlayableRaceCloneService().CreatePlanAsync(root, RequiredPath(_dbc.Text, "Choose the authoritative DBC folder."), RequiredPath(_schema.Text, "Choose the matching WDBX schema XML."), source, target, name, prefix, token, profile, capabilities, appearance, _operation.Token); ShowRacePlan(_racePlan);
            _session.Settings.CoreDbcPath = _dbc.Text ?? string.Empty; _session.Settings.SchemaDefinitionPath = _schema.Text ?? string.Empty; if (!string.IsNullOrWhiteSpace(_raceAssetLibrary.Text)) _session.Settings.ProcessedAssetLibraryPath = _raceAssetLibrary.Text!; _session.Settings.Save(); DesktopCrashLogger.Debug("PROJECT", "race-plan", ("sourceRace", source), ("targetRace", target), ("ready", _racePlan.Ready), ("dbcRows", _racePlan.DbcRows), ("sqlRows", _racePlan.SqlRows), ("appearanceAssets", _racePlan.DisplayBindings.Sum(binding => binding.Assets.Count)));
        }
        catch (OperationCanceledException) { _raceStatus.Text = "Playable-race planning cancelled."; }
        catch (Exception exception) { FailRace("Playable-race planning failed", exception); }
    }

    private async Task BuildRaceAsync()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        try
        {
            if (_racePlan is null) { await PlanRaceAsync(); if (_racePlan is null) return; } if (!_racePlan.Ready) throw new InvalidOperationException($"Resolve the {_racePlan.Blockers.Count:N0} dependency blocker(s) before building."); var profile = _session.DatabaseProfile ?? throw new InvalidOperationException("Reconnect Server & SQL before building."); var output = RequiredPath(_raceOutput.Text, "Choose a new or empty bundle output folder.");
            _raceStatus.Text = "Revalidating bound DBC/SQL inputs and building a new reviewable race bundle…"; var result = await new PlayableRaceCloneService().BuildAsync(_racePlan, profile, output, _operation.Token); _raceStatus.Text = $"Built race {_racePlan.TargetRaceId:N0} bundle. Patch: {result.PatchPath} · SQL: {result.SqlPath} · receipt: {result.ReceiptPath}"; DesktopCrashLogger.Debug("PROJECT", "race-bundle-built", ("race", _racePlan.TargetRaceId), ("output", result.OutputRoot), ("patch", result.PatchPath));
        }
        catch (OperationCanceledException) { _raceStatus.Text = "Playable-race build cancelled."; }
        catch (Exception exception) { FailRace("Playable-race build failed", exception); }
    }

    private (string Root, uint Source, uint Target, string Name, string Prefix, string Token, PlayableRaceAppearanceOptions Appearance) RaceInputs()
    {
        var root = RequiredPath(_root.Text, "Open or create a Crucible project first."); _ = CrucibleContentProjectService.Load(root); if (!uint.TryParse(_raceSource.Text, out var source) || source is 0 or > 31) throw new FormatException("Source race ID must be from 1 through 31."); if (!uint.TryParse(_raceTarget.Text, out var target) || target is 0 or > 31) throw new FormatException("Target race ID must be from 1 through 31.");
        var name = (_raceName.Text ?? string.Empty).Trim(); if (name.Length == 0) throw new FormatException("Enter the new race name."); var prefix = (_racePrefix.Text ?? string.Empty).Trim(); if (prefix.Length == 0) throw new FormatException("Enter the client prefix, such as Cr."); var token = (_raceToken.Text ?? string.Empty).Trim(); if (token.Length == 0) throw new FormatException("Enter the client file token, such as CrucibleRace.");
        uint? male = ParseOptionalDisplay(_raceMaleDisplay.Text, "Male"); uint? female = ParseOptionalDisplay(_raceFemaleDisplay.Text, "Female");
        return (root, source, target, name, prefix, token, new(male, female, EmptyNull(_raceAssetLibrary.Text), EmptyNull(_raceAssetProvenance.Text), EmptyNull(_raceAppearanceSource.Text)));
    }

    private void ShowRacePlan(PlayableRaceClonePlan plan)
    {
        var details = new List<string> { $"SOURCE  {plan.SourceRaceId:N0} · {plan.SourceRaceName}", $"TARGET  {plan.TargetRaceId:N0} · {plan.TargetRaceName} · {plan.TargetClientPrefix} · {plan.TargetFileToken}", $"BINDING {plan.ContentSha256}" };
        if (plan.AppearancePromotion is not null)
        {
            details.Add($"PROMOTE {plan.AppearancePromotion.SourceDbcRoot} → authoritative DBCs · add {plan.AppearancePromotion.AddedRows:N0} · reuse {plan.AppearancePromotion.ReusedRows:N0}");
            details.AddRange(plan.AppearancePromotion.Bindings.Select(binding => $"MAP     {binding.Role.ToUpperInvariant()} · display {binding.SourceDisplayId:N0} → {binding.TargetDisplayId:N0} · model {binding.SourceModelId:N0} → {binding.TargetModelId:N0}"));
        }
        details.AddRange(plan.DisplayBindings.Select(binding => $"DISPLAY {binding.Role.ToUpperInvariant()} · ID {binding.DisplayId:N0} · model {binding.ModelId:N0} · {binding.ModelClientPath} · provenance {binding.EffectiveProvenance ?? "target client assumed"} · {binding.Assets.Count:N0} packaged file(s)"));
        details.AddRange(plan.DisplayBindings.SelectMany(binding => binding.Assets).DistinctBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).Select(asset => $"ASSET   {asset.ClientPath} · {asset.Provenance} · {asset.Sha256}"));
        details.AddRange(plan.DbcTables.Select(table => $"DBC     {table.Table} · {table.AffectedRows:N0} row(s) · {table.Action}")); details.AddRange(plan.SqlTables.Select(table => $"SQL     {table.Table} · source {table.SourceRows:N0} · plan {table.Rows.Count:N0} · covered {table.AlreadyCovered:N0} · conflicts {table.Conflicts:N0}")); details.AddRange(plan.Blockers.Select(value => $"BLOCKER {value}")); details.AddRange(plan.Warnings.Select(value => $"WARNING {value}")); _raceDetails.ItemsSource = details;
        var assetCount = plan.DisplayBindings.SelectMany(binding => binding.Assets).Select(asset => asset.ClientPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(); _raceStatus.Text = plan.Ready ? $"Ready · {plan.DbcRows:N0} DBC row operation(s) · {plan.SqlRows:N0} reviewed SQL operation(s) · {assetCount:N0} exact appearance asset(s). Review the evidence, then build." : $"Blocked · {plan.Blockers.Count:N0} issue(s). Nothing can be built until they are resolved."; if (string.IsNullOrWhiteSpace(_raceOutput.Text)) _raceOutput.Text = Path.Combine(plan.ProjectRoot, "Staging", $"Race-{plan.TargetRaceId}-{plan.TargetFileToken}");
    }

    private void UseLatestRaceReservation()
    {
        try { var root = RequiredPath(_root.Text, "Open or create a Crucible project first."); var reservation = CrucibleContentProjectService.LoadRegistry(root).Reservations.Where(value => value.Domain == ContentIdDomain.Race).OrderByDescending(value => value.CreatedUtc).FirstOrDefault() ?? throw new InvalidOperationException("This project has no reserved Race ID yet."); _raceTarget.Text = reservation.Values.Last().ToString(); }
        catch (Exception exception) { FailRace("Could not load a reserved Race ID", exception); }
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

    private static uint? ParseOptionalDisplay(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!uint.TryParse(text, out var value) || value == 0) throw new FormatException($"{label} display ID must be a positive unsigned integer.");
        return value;
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
    private void InvalidateRacePlan() { _racePlan = null; _raceDetails.ItemsSource = Array.Empty<string>(); _raceStatus.Text = "Race inputs changed · create a fresh dependency plan before building."; }
    private void SessionChanged(object? sender, EventArgs e) { InvalidateReport(); InvalidateClassPlan(); InvalidateRacePlan(); }
    private void Fail(string operation, Exception exception) { _status.Text = $"{operation}: {exception.Message}"; DesktopCrashLogger.Log(operation, exception); }
    private void FailClass(string operation, Exception exception) { _classStatus.Text = $"{operation}: {exception.Message}"; DesktopCrashLogger.Log(operation, exception); }
    private void FailRace(string operation, Exception exception) { _raceStatus.Text = $"{operation}: {exception.Message}"; DesktopCrashLogger.Log(operation, exception); }
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
