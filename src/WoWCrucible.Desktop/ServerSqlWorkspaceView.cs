using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MySqlConnector;
using System.Globalization;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ServerSqlWorkspaceView : UserControl, IDisposable
{
    private sealed record BridgeTableChoice(int Index, string Label) { public override string ToString() => Label; }
    private sealed class BridgeColumnChoice
    {
        public required string Source { get; init; }
        public string Target { get; set; } = string.Empty;
        public bool Drop { get; set; }
        public bool IsKey { get; init; }
    }
    private sealed class BridgeDefaultChoice
    {
        public required string Target { get; init; }
        public LegacyDatabaseAuditValueState State { get; set; } = LegacyDatabaseAuditValueState.Unknown;
        public string Value { get; set; } = string.Empty;
    }
    private sealed record BridgeExpansionChoice(int Index, string Label) { public override string ToString() => Label; }
    private sealed record BridgeLookupChoice(int Index, string Label) { public override string ToString() => Label; }
    private sealed record BridgeValueOption(DatabaseSyncExpansionValueSource? Source, string SourceColumn, string Label, string? LookupName = null) { public override string ToString() => Label; }
    private sealed class BridgeLookupMatchChoice
    {
        public required string LookupColumn { get; init; }
        public bool Included { get; set; }
        public DatabaseSyncExpansionValue? Input { get; set; }
    }
    private sealed class BridgeExpansionBindingChoice
    {
        public required string Target { get; init; }
        public bool IsKey { get; init; }
        public bool Required { get; init; }
        public bool Included { get; set; }
        public DatabaseSyncExpansionValue? KeyValue { get; set; }
        public DatabaseSyncExpansionValue? Before { get; set; }
        public DatabaseSyncExpansionValue? After { get; set; }
    }
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _serverRoot = new();
    private readonly TextBox _host = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { PasswordChar = '●' };
    private readonly TextBox _database = new();
    private readonly TextBox _summary = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly TextBox _legacySnapshot = new() { PlaceholderText = "Legacy edited-world snapshot" };
    private readonly TextBox _baselineSnapshot = new() { PlaceholderText = "Optional matching stock-world snapshot" };
    private readonly TextBox _legacyIncludes = new() { AcceptsReturn = true, PlaceholderText = "Optional table globs to include, one per line" };
    private readonly TextBox _legacyExcludes = new() { AcceptsReturn = true, PlaceholderText = "Optional table globs to exclude, one per line" };
    private readonly TextBlock _legacyStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly TextBox _recoveryAudit = new() { PlaceholderText = "Verified baseline-to-edited recovery audit" };
    private readonly TextBox _recoveryTranslation = new() { PlaceholderText = "Optional target-schema bridge profile for old/different core layouts" };
    private readonly TextBox _recoveryPlan = new() { PlaceholderText = "Target-bound synchronization plan" };
    private readonly TextBox _recoveryReceipt = new() { PlaceholderText = "Apply receipt for exact rollback" };
    private readonly CheckBox _recoveryRemovals = new() { Content = "Include explicitly reviewed removals" };
    private readonly CheckBox _recoveryAutoRemap = new() { Content = "Remap occupied added IDs" };
    private readonly CheckBox _recoveryDependencyClosure = new() { Content = "Include exact changed-row dependency closure", IsChecked = true };
    private readonly TextBox _recoveryRemapStart = new() { PlaceholderText = "Optional first remapped ID" };
    private readonly TextBlock _recoveryRemapSummary = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D5B56A")), IsVisible = false };
    private readonly ListBox _recoveryOperations = new();
    private readonly ComboBox _bridgeTable = new();
    private readonly ComboBox _bridgeTargetTable = new();
    private readonly ListBox _bridgeColumns = new();
    private readonly ListBox _bridgeDefaults = new();
    private readonly CheckBox _bridgeSuppressPrimary = new() { Content = "Suppress the primary row; emit reviewed structural rows only" };
    private readonly ComboBox _bridgeExpansion = new();
    private readonly TextBox _bridgeExpansionName = new() { PlaceholderText = "Expansion name" };
    private readonly ComboBox _bridgeExpansionTargetTable = new();
    private readonly ComboBox _bridgeExpansionTargetKind = new();
    private readonly CheckBox _bridgeExpansionAdded = new() { Content = "Source Added" };
    private readonly CheckBox _bridgeExpansionModified = new() { Content = "Source Modified" };
    private readonly CheckBox _bridgeExpansionRemoved = new() { Content = "Source Removed" };
    private readonly ListBox _bridgeExpansionBindings = new();
    private readonly TextBlock _bridgeExpansionStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly ComboBox _bridgeLookup = new();
    private readonly TextBox _bridgeLookupName = new() { PlaceholderText = "Lookup name" };
    private readonly ComboBox _bridgeLookupSource = new();
    private readonly ComboBox _bridgeLookupTable = new();
    private readonly ComboBox _bridgeLookupResultColumn = new();
    private readonly ComboBox _bridgeLookupResultVersion = new();
    private readonly ListBox _bridgeLookupMatches = new();
    private readonly TextBlock _bridgeLookupStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly TextBlock _bridgeStatus = new() { Text = "Generate or load a schema bridge to edit it here.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private DatabaseSyncTranslationProfile? _loadedBridge;
    private DatabaseCapabilities? _bridgeCapabilities;
    private int _bridgeRuleIndex = -1;
    private List<BridgeColumnChoice> _bridgeColumnRows = [];
    private List<BridgeDefaultChoice> _bridgeDefaultRows = [];
    private List<BridgeExpansionBindingChoice> _bridgeExpansionRows = [];
    private int _bridgeExpansionIndex = -1;
    private List<BridgeLookupMatchChoice> _bridgeLookupRows = [];
    private int _bridgeLookupIndex = -1;
    private bool _bridgeUiChanging;
    private readonly Border _recoveryConfirmation = new() { IsVisible = false, BorderBrush = new SolidColorBrush(Color.Parse("#6E5426")), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private DatabaseSyncPlan? _loadedRecoveryPlan;
    private readonly TextBox _syncSourceDbc = new() { PlaceholderText = "Edited source DBC" };
    private readonly TextBox _syncSchema = new() { PlaceholderText = "Matching WotLK schema XML" };
    private readonly TextBox _syncBundle = new() { PlaceholderText = "New or existing deployment-bundle folder" };
    private readonly TextBox _syncReceipt = new() { PlaceholderText = "Deployment receipt for rollback" };
    private readonly TextBlock _syncStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly Border _syncConfirmation = new() { IsVisible = false, BorderBrush = new SolidColorBrush(Color.Parse("#6E5426")), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly Button _detect = AccentButton("Detect server and connect");
    private readonly Button _test = AccentButton("Test and use connection");
    private readonly ServerLifecycleService _lifecycle = new();
    private readonly TextBlock _runtimeStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) };
    private readonly List<Button> _lifecycleButtons = [];
    private CancellationTokenSource? _operation;
    private bool _activated;

    public event EventHandler? BackRequested;

    public ServerSqlWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged;
        var back = new Button { Content = "← Editor", HorizontalAlignment = HorizontalAlignment.Left }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "SERVER & SQL", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0) }, 1) } };

        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => await BrowseServerAsync();
        _detect.Click += async (_, _) => await DetectAsync();
        var automatic = new Grid { RowDefinitions = new("Auto,Auto,Auto"), Margin = new Thickness(16), RowSpacing = 10 };
        automatic.Children.Add(new TextBlock { Text = "Point Crucible at the installed server folder. It reads the live worldserver.conf, including WSL launchers, locates server DBCs, and tests the world database without writing to it.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#9AA5B7")) });
        var serverPath = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _serverRoot, WithColumn(browse, 1) } }; Grid.SetRow(serverPath, 1); automatic.Children.Add(serverPath);
        Grid.SetRow(_detect, 2); _detect.HorizontalAlignment = HorizontalAlignment.Left; automatic.Children.Add(_detect);

        _test.Click += async (_, _) => await TestManualAsync();
        var manualForm = Form(("Host", _host), ("Port", _port), ("User", _user), ("Password", _password), ("World database", _database));
        var manual = new Grid { RowDefinitions = new("Auto,Auto,Auto"), Margin = new Thickness(16), RowSpacing = 10 };
        manual.Children.Add(new TextBlock { Text = "Manual connection is available for custom layouts. Passwords remain in memory only and are never written to settings or logs.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#9AA5B7")) });
        Grid.SetRow(manualForm, 1); manual.Children.Add(manualForm); Grid.SetRow(_test, 2); _test.HorizontalAlignment = HorizontalAlignment.Left; manual.Children.Add(_test);

        var tabs = new TabControl
        {
            Margin = new Thickness(12),
            Items =
            {
                new TabItem { Header = "Automatic server detection", Content = automatic },
                new TabItem { Header = "Manual SQL", Content = manual },
                new TabItem { Header = "Server controls", Content = LifecyclePage() },
                new TabItem { Header = "Session overview", Content = SummaryCard() },
                new TabItem { Header = "DBC + SQL deployment", Content = DbcSqlDeploymentPage() },
                new TabItem { Header = "Recover legacy SQL edits", Content = LegacyRecoveryPage() }
            }
        };
        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        root.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2B3445")), BorderThickness = new Thickness(0,0,0,1), Child = heading }); Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var status = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2B3445")), BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(14,7), Child = _status }; Grid.SetRow(status, 2); root.Children.Add(status);
        Content = root; PopulateFromSession();
    }

    public void Activate()
    {
        PopulateFromSession();
        if (_session.Server is not null) _ = RefreshLifecycleAsync();
        if (_activated || _session.DatabaseTested || string.IsNullOrWhiteSpace(_serverRoot.Text) || !Directory.Exists(_serverRoot.Text)) return;
        _activated = true; _ = DetectAsync();
    }

    private Border SummaryCard() => new() { Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = _summary } };

    private Control LifecyclePage()
    {
        Button Action(string label, Func<ServerWorkspace, CancellationToken, Task<ServerLifecycleResult>> operation, bool accent = false)
        {
            var button = accent ? AccentButton(label) : new Button { Content = label };
            button.Click += async (_, _) => await RunLifecycleAsync(operation);
            _lifecycleButtons.Add(button);
            return button;
        }
        var refresh = new Button { Content = "Refresh status" };
        refresh.Click += async (_, _) => await RefreshLifecycleAsync();
        _lifecycleButtons.Add(refresh);
        var controls = new WrapPanel
        {
            Children =
            {
                refresh,
                Action("Start auth + world", _lifecycle.StartAllAsync, true),
                Action("Start worldserver only", _lifecycle.StartWorldAsync),
                Action("Gracefully stop worldserver", _lifecycle.StopWorldAsync),
                Action("Gracefully restart worldserver", _lifecycle.RestartWorldAsync, true),
                Action("Gracefully stop auth + world", _lifecycle.StopAllAsync)
            }
        };
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 13,
                Children =
                {
                    new TextBlock { Text = "Server lifecycle", FontSize = 22, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Worldserver stop and restart always use the core's graceful shutdown path and wait for its save to finish. The database stays online for HeidiSQL and Crucible. A local worldserver not launched by this Crucible session is never force-killed.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#9AA5B7"))
                    },
                    controls,
                    new Border { Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = _runtimeStatus }
                }
            }
        };
    }

    private Control LegacyRecoveryPage()
    {
        var capture = AccentButton("Capture connected world database");
        capture.Click += async (_, _) => await CaptureLegacySnapshotAsync();
        var browseLegacy = new Button { Content = "Legacy snapshot…" };
        browseLegacy.Click += async (_, _) => await PickRecoveryInputAsync(_legacySnapshot, "Select the edited legacy-world snapshot", "*.crucible-db-snapshot");
        var inspectLegacy = new Button { Content = "Verify legacy snapshot" };
        inspectLegacy.Click += async (_, _) => await InspectSnapshotAsync(_legacySnapshot.Text);
        var browseBaseline = new Button { Content = "Stock baseline…" };
        browseBaseline.Click += async (_, _) => await PickRecoveryInputAsync(_baselineSnapshot, "Select an optional matching stock-world snapshot", "*.crucible-db-snapshot");
        var audit = AccentButton("Build offline change audit");
        audit.Click += async (_, _) => await BuildRecoveryAuditAsync();
        var inspectAudit = new Button { Content = "Verify an audit…" };
        inspectAudit.Click += async (_, _) => await InspectAuditAsync();
        var browseAudit = new Button { Content = "Audit…" }; browseAudit.Click += async (_, _) => await PickRecoveryInputAsync(_recoveryAudit, "Select a verified baseline-to-edited audit", "*.crucible-db-audit");
        var createBridge = new Button { Content = "Generate schema bridge…" }; createBridge.Click += async (_, _) => await BuildSchemaBridgeAsync();
        var browseBridge = new Button { Content = "Bridge…" }; browseBridge.Click += async (_, _) => await PickAndLoadSchemaBridgeAsync();
        var buildPlan = AccentButton("Compare audit with connected target"); buildPlan.Click += async (_, _) => await BuildDatabaseSyncPlanAsync();
        var browsePlan = new Button { Content = "Plan…" }; browsePlan.Click += async (_, _) => await PickRecoveryInputAsync(_recoveryPlan, "Select a target-bound synchronization plan", "*.json");
        var inspectPlan = new Button { Content = "Verify / load plan" }; inspectPlan.Click += async (_, _) => await LoadDatabaseSyncPlanAsync();
        var exportSql = new Button { Content = "Export non-committing SQL preview…" }; exportSql.Click += async (_, _) => await ExportDatabaseSyncPreviewAsync();
        var chooseReceipt = new Button { Content = "Receipt output…" }; chooseReceipt.Click += async (_, _) => await PickDatabaseSyncReceiptOutputAsync();
        var applyPlan = AccentButton("Review transactional apply"); applyPlan.Click += async (_, _) => await ReviewDatabaseSyncApplyAsync();
        var browseReceipt = new Button { Content = "Existing receipt…" }; browseReceipt.Click += async (_, _) => await PickRecoveryInputAsync(_recoveryReceipt, "Select a database synchronization receipt", "*.json");
        var rollback = new Button { Content = "Review exact rollback" }; rollback.Click += (_, _) => ReviewDatabaseSyncRollback();
        _recoveryOperations.ItemTemplate = new FuncDataTemplate<DatabaseSyncOperation>((operation, _) => operation is null ? new TextBlock() : new StackPanel
        {
            Margin = new Thickness(4, 3),
            Children =
            {
                new TextBlock { Text = $"{operation.Status} · {operation.Kind} · {operation.Identity}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{operation.Domain} · {operation.Fields.Count:N0} field(s) · {operation.Finding}", Foreground = new SolidColorBrush(Color.Parse("#8995A9")), TextWrapping = TextWrapping.Wrap }
            }
        });
        var paths = new Grid { ColumnDefinitions = new("*,Auto,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        Grid.SetRow(_legacySnapshot, 0); paths.Children.Add(_legacySnapshot);
        Grid.SetRow(browseLegacy, 0); Grid.SetColumn(browseLegacy, 1); paths.Children.Add(browseLegacy);
        Grid.SetRow(inspectLegacy, 0); Grid.SetColumn(inspectLegacy, 2); paths.Children.Add(inspectLegacy);
        Grid.SetRow(_baselineSnapshot, 1); paths.Children.Add(_baselineSnapshot);
        Grid.SetRow(browseBaseline, 1); Grid.SetColumn(browseBaseline, 1); paths.Children.Add(browseBaseline);
        var filters = new Expander
        {
            Header = "Optional table filters",
            Content = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { _legacyIncludes, WithColumn(_legacyExcludes, 1) } }
        };
        var auditPath = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _recoveryAudit, WithColumn(browseAudit, 1) } };
        var bridgePath = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _recoveryTranslation, WithColumn(createBridge, 1), WithColumn(browseBridge, 2) } };
        var planPath = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _recoveryPlan, WithColumn(browsePlan, 1), WithColumn(inspectPlan, 2) } };
        var receiptPath = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _recoveryReceipt, WithColumn(chooseReceipt, 1), WithColumn(browseReceipt, 2) } };
        var configuration = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 13,
                Children =
                {
                    new TextBlock { Text = "Recover reusable edits from an older world database", FontSize = 22, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Capture is SELECT-only and excludes accounts, characters, and other runtime state by default. A matching stock baseline lets Crucible identify added and modified content; without one every row is honestly marked as an unattributed candidate.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#9AA5B7"))
                    },
                    new WrapPanel { Children = { capture, inspectAudit } },
                    paths,
                    filters,
                    audit,
                    new Border { Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = _legacyStatus },
                    new TextBlock { Text = "Compare and synchronize with the connected target", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "A plan requires a verified matching-core baseline audit. For a different target layout, generate a schema bridge: unchanged names pass through automatically while table/column renames, omitted source fields, required target defaults, and deliberate one-to-many normalized row outputs stay explicit. The bridge bytes and live target schema are hash-bound; incomplete keys or values block instead of being guessed. Dependency closure follows only exact relationships and keeps every output from an included source row together; occupied IDs remain conflicts unless remapping is enabled.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#9AA5B7")) },
                    auditPath,
                    bridgePath,
                    new WrapPanel { Children = { _recoveryRemovals, _recoveryDependencyClosure, _recoveryAutoRemap, _recoveryRemapStart, buildPlan } },
                    _recoveryRemapSummary,
                    planPath,
                    new WrapPanel { Children = { exportSql, applyPlan } },
                    receiptPath,
                    rollback,
                    _recoveryConfirmation
                }
            }
        };
        var reviewTabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "Target plan rows", Content = _recoveryOperations }, new TabItem { Header = "Schema bridge editor", Content = BuildSchemaBridgeEditor() } } };
        return new ResponsiveSplitGrid(configuration, new Border { BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = reviewTabs }, 3, 2, 1.7);
    }

    private Control DbcSqlDeploymentPage()
    {
        if (string.IsNullOrWhiteSpace(_syncSchema.Text)) _syncSchema.Text = _session.Settings.SchemaDefinitionPath;
        var source = new Button { Content = "Edited DBC…" }; source.Click += async (_, _) => await PickSyncFileAsync(_syncSourceDbc, "Select the edited source DBC", "DBC", "*.dbc");
        var schema = new Button { Content = "Schema XML…" }; schema.Click += async (_, _) => await PickSyncFileAsync(_syncSchema, "Select the matching schema XML", "XML schema", "*.xml");
        var bundle = new Button { Content = "Bundle folder…" }; bundle.Click += async (_, _) => await PickBundleFolderAsync();
        var receipt = new Button { Content = "Receipt…" }; receipt.Click += async (_, _) => await PickSyncFileAsync(_syncReceipt, "Select a deployment receipt", "Crucible deployment receipt", "deployment-receipt.json", "*.json");
        var fields = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnSpacing = 7, RowSpacing = 7 };
        AddPickerRow(fields, _syncSourceDbc, source, 0); AddPickerRow(fields, _syncSchema, schema, 1); AddPickerRow(fields, _syncBundle, bundle, 2); AddPickerRow(fields, _syncReceipt, receipt, 3);
        var plan = AccentButton("Audit and create frozen bundle"); plan.Click += async (_, _) => await CreateSyncBundleAsync();
        var apply = AccentButton("Review synchronized apply"); apply.Click += (_, _) => ReviewSyncApply();
        var rollback = new Button { Content = "Review exact rollback" }; rollback.Click += (_, _) => ReviewSyncRollback();
        var module = new Button { Content = "Export module migration…" }; module.Click += async (_, _) => await ExportModuleMigrationAsync();
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Diagnose and deploy one effective DBC change", FontSize = 22, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Crucible resolves how the detected core consumes this table, compares the edited DBC with the live SQL overlay, freezes the exact SQL and server-file pre-image, stages a one-file client patch manifest, and writes audit/migrate/rollback artifacts. Apply refuses any DBC or SQL row that changed after review.",
                        TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#9AA5B7"))
                    },
                    fields,
                    new WrapPanel { Children = { plan, apply, rollback, module } },
                    _syncConfirmation,
                    new Border { Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = _syncStatus }
                }
            }
        };
    }

    private async Task CreateSyncBundleAsync()
    {
        if (_session.Server is not { } server || _session.DatabaseProfile is not { } profile || _session.DatabaseCapabilities is not { } capabilities)
        { _syncStatus.Text = "Detect the installed server and verify SQL before creating a synchronized deployment bundle."; return; }
        BeginOperation("Auditing the edited DBC against its effective live SQL overlay…");
        try
        {
            var dbcPath = Path.GetFullPath(_syncSourceDbc.Text ?? string.Empty); var schemaPath = Path.GetFullPath(_syncSchema.Text ?? string.Empty); var bundlePath = Path.GetFullPath(_syncBundle.Text ?? string.Empty);
            var dbc = WdbcFile.Load(dbcPath); var resolution = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(Path.GetFileNameWithoutExtension(dbcPath), dbc.FieldCount);
            if (resolution.UsedFallback) throw new InvalidDataException("The selected XML does not contain an exact named schema matching this DBC layout.");
            var sourceRoot = Directory.Exists(_session.Settings.CoreSourcePath) ? _session.Settings.CoreSourcePath : null;
            var binding = ServerTableBindingCatalog.ApplySchemaKey(ServerTableBindingCatalog.ResolveFile(server.CoreFamily, dbcPath, sourceRoot), resolution);
            if (binding.Consumption != ServerTableConsumption.SqlOverlayed || binding.SqlTableName is null)
                throw new InvalidOperationException($"{binding.DbcFileName} is classified as {binding.Consumption} by {binding.Profile}; a synchronized SQL-overlay bundle is not applicable. Destinations: {binding.Destinations}.");
            var table = capabilities.FindTable(binding.SqlTableName) ?? throw new InvalidDataException($"The connected world database has no expected overlay table {binding.SqlTableName}.");
            var audit = await new DbcSqlAuditService().AuditAsync(profile, binding, dbcPath, resolution, table, _operation!.Token);
            var serverDbc = Path.Combine(server.DbcPath, binding.DbcFileName);
            var result = await Task.Run(() => new DbcSqlDeploymentBundleService().Create(bundlePath, profile, audit, resolution, schemaPath, serverDbc), _operation.Token);
            _syncBundle.Text = result.RootPath;
            _syncStatus.Text = $"Bundle created and hash-verified · {result.Plan.Rows.Count:N0} SQL row(s) · {binding.DescribeRow(result.Plan.Rows[0].Key)} through {binding.DescribeRow(result.Plan.Rows[^1].Key)}.\nServer target: {serverDbc}\nClient manifest: {Path.Combine(result.RootPath, result.Plan.ClientManifestFile)}\nRequired after apply: {result.Plan.Restart}. Nothing was deployed yet.";
        }
        catch (OperationCanceledException) { _syncStatus.Text = "DBC/SQL bundle creation cancelled."; }
        catch (Exception exception) { _syncStatus.Text = $"Bundle creation failed safely: {exception.Message}"; DesktopCrashLogger.Log("DBC/SQL deployment bundle creation failed", exception); }
        finally { EndOperation(); }
    }

    private void ReviewSyncApply()
    {
        try
        {
            if (_session.DatabaseProfile is null) throw new InvalidOperationException("Connect Server & SQL first.");
            var bundle = new DbcSqlDeploymentBundleService().Load(_syncBundle.Text ?? string.Empty); var cancel = new Button { Content = "Cancel" }; var confirm = AccentButton("Apply DBC + SQL transaction");
            cancel.Click += (_, _) => _syncConfirmation.IsVisible = false; confirm.Click += async (_, _) => await ApplySyncBundleAsync(confirm);
            _syncConfirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Apply the frozen bundle to {bundle.Plan.Database.Database}? This verifies the exact SQL pre-image and server DBC hash, updates {bundle.Plan.Rows.Count:N0} SQL row(s), backs up and atomically replaces {bundle.Plan.ServerDbcPath}, verifies both destinations, then commits. A receipt is required for rollback.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } };
            _syncConfirmation.IsVisible = true;
        }
        catch (Exception exception) { _syncStatus.Text = $"Apply review failed safely: {exception.Message}"; }
    }

    private async Task ApplySyncBundleAsync(Button button)
    {
        if (_session.DatabaseProfile is null) return; BeginOperation("Applying and verifying the frozen DBC/SQL deployment…"); button.IsEnabled = false;
        try
        {
            var result = await new DbcSqlDeploymentBundleService().ApplyAsync(_syncBundle.Text ?? string.Empty, _session.DatabaseProfile, _operation!.Token);
            _syncReceipt.Text = result.ReceiptPath; _syncConfirmation.IsVisible = false;
            _syncStatus.Text = $"Deployment verified · {result.SqlRows:N0} SQL row(s) · server SHA-256 {result.ServerSha256}.\nReceipt: {result.ReceiptPath}\nNext: {result.Restart}.";
        }
        catch (Exception exception) { _syncStatus.Text = $"Deployment failed and uncommitted work was restored: {exception.Message}"; DesktopCrashLogger.Log("DBC/SQL synchronized deployment failed", exception); }
        finally { button.IsEnabled = true; EndOperation(); }
    }

    private void ReviewSyncRollback()
    {
        try
        {
            if (_session.DatabaseProfile is null) throw new InvalidOperationException("Connect Server & SQL first.");
            var receiptPath = Path.GetFullPath(_syncReceipt.Text ?? string.Empty); if (!File.Exists(receiptPath)) throw new FileNotFoundException("Select the exact deployment receipt first.", receiptPath);
            var cancel = new Button { Content = "Cancel" }; var confirm = new Button { Content = "Restore reviewed pre-image" };
            cancel.Click += (_, _) => _syncConfirmation.IsVisible = false; confirm.Click += async (_, _) => await RollbackSyncBundleAsync(confirm);
            _syncConfirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = "Rollback first verifies that the current server DBC and SQL rows still equal the applied receipt. If anything changed afterward it refuses to overwrite that work. Otherwise it restores the exact SQL pre-image and verified DBC backup transactionally.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } };
            _syncConfirmation.IsVisible = true;
        }
        catch (Exception exception) { _syncStatus.Text = $"Rollback review failed safely: {exception.Message}"; }
    }

    private async Task RollbackSyncBundleAsync(Button button)
    {
        if (_session.DatabaseProfile is null) return; BeginOperation("Verifying and restoring the deployment pre-image…"); button.IsEnabled = false;
        try
        {
            var result = await new DbcSqlDeploymentBundleService().RollbackAsync(_syncReceipt.Text ?? string.Empty, _session.DatabaseProfile, _operation!.Token);
            _syncConfirmation.IsVisible = false; _syncStatus.Text = $"Rollback verified · {result.SqlRows:N0} SQL row(s) and the server DBC pre-image restored. Restart worldserver before runtime testing.";
        }
        catch (Exception exception) { _syncStatus.Text = $"Rollback refused or failed safely: {exception.Message}"; DesktopCrashLogger.Log("DBC/SQL synchronized rollback failed", exception); }
        finally { button.IsEnabled = true; EndOperation(); }
    }

    private async Task ExportModuleMigrationAsync()
    {
        try
        {
            var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the AzerothCore module root", AllowMultiple = false }); var root = folders.FirstOrDefault()?.TryGetLocalPath(); if (root is null) return;
            var path = new DbcSqlDeploymentBundleService().ExportModuleMigration(_syncBundle.Text ?? string.Empty, root); _syncStatus.Text = $"Exported the reviewed idempotent world migration without touching the live database:\n{path}";
        }
        catch (Exception exception) { _syncStatus.Text = $"Module migration export failed safely: {exception.Message}"; }
    }

    private async Task PickSyncFileAsync(TextBox target, string title, string label, params string[] patterns)
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(label) { Patterns = patterns }] }); var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) target.Text = path;
    }

    private async Task PickBundleFolderAsync()
    {
        var folders = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select an existing bundle or a parent for a new bundle", AllowMultiple = false }); var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
        _syncBundle.Text = File.Exists(Path.Combine(path, "deployment-plan.json")) ? path : Path.Combine(path, $"dbc-sql-deployment-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
    }

    private static void AddPickerRow(Grid grid, Control input, Control button, int row) { Grid.SetRow(input, row); grid.Children.Add(input); Grid.SetRow(button, row); Grid.SetColumn(button, 1); grid.Children.Add(button); }

    private async Task BrowseServerAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The server workspace is not attached to the main window.");
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the installed AzerothCore or TrinityCore server folder", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) _serverRoot.Text = path;
    }

    private async Task DetectAsync()
    {
        BeginOperation("Detecting the live server configuration and testing its world database…");
        var detected = false;
        try
        {
            await _session.DetectServerAndConnectAsync(_serverRoot.Text ?? string.Empty, _operation!.Token);
            _status.Text = "Server workspace detected and SQL connection verified. This session is now shared by Crucible features.";
            detected = true;
        }
        catch (OperationCanceledException) { _status.Text = "Server detection cancelled."; }
        catch (Exception exception) { _status.Text = $"Detection failed: {exception.Message}"; }
        finally { EndOperation(); }
        if (detected) await RefreshLifecycleAsync();
    }

    private async Task TestManualAsync()
    {
        BeginOperation("Testing the manual SQL connection read-only…");
        try
        {
            var profile = new DatabaseConnectionProfile(_host.Text ?? string.Empty, (uint)(_port.Value ?? 3306), _user.Text ?? string.Empty, _password.Text ?? string.Empty, _database.Text ?? string.Empty, MySqlSslMode.Preferred);
            await _session.TestManualDatabaseAsync(profile, _operation!.Token);
            _status.Text = "SQL connection verified and shared with Crucible features.";
        }
        catch (OperationCanceledException) { _status.Text = "Connection test cancelled."; }
        catch (Exception exception) { _status.Text = $"Connection failed: {exception.Message}"; }
        finally { EndOperation(); }
    }

    private async Task RefreshLifecycleAsync()
    {
        if (_session.Server is not { } server) { _runtimeStatus.Text = "Detect an installed server workspace first."; return; }
        BeginOperation("Checking authserver, worldserver, and database status…");
        try
        {
            var status = await _lifecycle.GetStatusAsync(server, _operation!.Token);
            ShowRuntimeStatus(status, "Status refreshed.");
            _status.Text = status.Detail;
        }
        catch (OperationCanceledException) { _runtimeStatus.Text = "Status check cancelled."; }
        catch (Exception exception) { _runtimeStatus.Text = $"Status check failed: {exception.Message}"; DesktopCrashLogger.Log("Server status check failed", exception); }
        finally { EndOperation(); }
    }

    private async Task RunLifecycleAsync(Func<ServerWorkspace, CancellationToken, Task<ServerLifecycleResult>> operation)
    {
        if (_session.Server is not { } server) { _runtimeStatus.Text = "Detect an installed server workspace first."; return; }
        BeginOperation("Running the requested server operation…");
        try
        {
            var result = await operation(server, _operation!.Token);
            ShowRuntimeStatus(result.Status, result.Detail);
            _status.Text = $"{result.Action} completed and was verified.";
            DesktopCrashLogger.Debug("SERVER", "lifecycle-success", ("action", result.Action), ("world", result.Status.WorldServerRunning), ("auth", result.Status.AuthServerRunning), ("database", result.Status.DatabaseRunning));
        }
        catch (OperationCanceledException) { _runtimeStatus.Text = "Server operation cancelled. No force-kill fallback was used."; }
        catch (Exception exception)
        {
            _runtimeStatus.Text = $"Server operation failed safely: {exception.Message}";
            _status.Text = "Server operation failed; inspect the status before retrying.";
            DesktopCrashLogger.Log("Server lifecycle operation failed", exception);
        }
        finally { EndOperation(); }
    }

    private void ShowRuntimeStatus(ServerRuntimeStatus status, string detail)
    {
        _runtimeStatus.Text = $"worldserver\t{(status.WorldServerRunning ? "RUNNING" : "STOPPED")}\nauthserver\t{(status.AuthServerRunning ? "RUNNING" : "STOPPED")}\ndatabase\t{(status.DatabaseRunning ? "RUNNING" : "STOPPED / UNKNOWN")}\n\n{detail}\n\n{status.Detail}";
    }

    private async Task CaptureLegacySnapshotAsync()
    {
        if (!_session.DatabaseTested || _session.DatabaseProfile is null)
        {
            _legacyStatus.Text = "Connect and verify the legacy world database first. The active shared SQL session is the capture source.";
            return;
        }
        var storage = Storage();
        var destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Capture a read-only legacy world snapshot",
            SuggestedFileName = $"{_session.DatabaseProfile.Database}.crucible-db-snapshot",
            FileTypeChoices = [new FilePickerFileType("Crucible database snapshot") { Patterns = ["*.crucible-db-snapshot"] }]
        });
        var path = destination?.TryGetLocalPath();
        if (path is null) return;
        BeginOperation("Capturing a read-only world database snapshot…");
        try
        {
            var progress = new Progress<LegacyDatabaseSnapshotProgress>(value =>
                _legacyStatus.Text = $"{value.Stage}{(value.Table is null ? string.Empty : $" · {value.Table}")} · {value.CompletedTables:N0}/{value.TotalTables:N0} tables · {value.Rows:N0} rows");
            var options = new LegacyDatabaseSnapshotOptions(Patterns(_legacyIncludes.Text), Patterns(_legacyExcludes.Text));
            var result = await new LegacyDatabaseSnapshotService().CaptureAsync(_session.DatabaseProfile, path, options, progress, _operation!.Token);
            _legacySnapshot.Text = result.Path;
            _legacyStatus.Text = $"Captured {result.Manifest.Tables.Count:N0} world-content tables and {result.Manifest.TotalRows:N0} rows.\nArtifact: {result.Path}\nSHA-256 content: {result.Manifest.ContentSha256}\nSensitive runtime tables excluded: {!result.Manifest.Policy.SensitiveStateIncluded}";
        }
        catch (OperationCanceledException) { _legacyStatus.Text = "Legacy database capture cancelled; no partial artifact was published."; }
        catch (Exception exception) { _legacyStatus.Text = $"Capture failed: {exception.Message}"; DesktopCrashLogger.Log("Legacy database capture failed", exception); }
        finally { EndOperation(); }
    }

    private async Task PickRecoveryInputAsync(TextBox target, string title, string pattern)
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) target.Text = path;
    }

    private async Task InspectSnapshotAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { _legacyStatus.Text = "Choose an existing snapshot first."; return; }
        BeginOperation("Verifying every table and hash in the snapshot…");
        try
        {
            var result = await new LegacyDatabaseSnapshotService().InspectAsync(path, true, _operation!.Token);
            _legacyStatus.Text = result.Manifest is null
                ? $"Invalid snapshot: {string.Join("; ", result.Findings)}"
                : $"{(result.Valid ? "VALID" : "INVALID")} snapshot · {result.Manifest.Tables.Count:N0} tables · {result.Manifest.TotalRows:N0} rows\nSource: {result.Manifest.Source.Database} · MySQL {result.Manifest.Source.ServerVersion}\n{(result.Findings.Count == 0 ? "All schema, row, byte-count, and SHA-256 checks passed." : string.Join(Environment.NewLine, result.Findings))}";
        }
        catch (OperationCanceledException) { _legacyStatus.Text = "Snapshot verification cancelled."; }
        catch (Exception exception) { _legacyStatus.Text = $"Snapshot verification failed: {exception.Message}"; DesktopCrashLogger.Log("Snapshot verification failed", exception); }
        finally { EndOperation(); }
    }

    private async Task BuildRecoveryAuditAsync()
    {
        var legacy = _legacySnapshot.Text;
        if (string.IsNullOrWhiteSpace(legacy) || !File.Exists(legacy)) { _legacyStatus.Text = "Choose or capture the edited legacy-world snapshot first."; return; }
        var baseline = string.IsNullOrWhiteSpace(_baselineSnapshot.Text) ? null : _baselineSnapshot.Text;
        if (baseline is not null && !File.Exists(baseline)) { _legacyStatus.Text = "The selected stock baseline does not exist."; return; }
        var destination = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the offline legacy change audit",
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(legacy)}.crucible-db-audit",
            FileTypeChoices = [new FilePickerFileType("Crucible database recovery audit") { Patterns = ["*.crucible-db-audit"] }]
        });
        var output = destination?.TryGetLocalPath();
        if (output is null) return;
        BeginOperation("Comparing immutable database snapshots offline…");
        try
        {
            var progress = new Progress<LegacyDatabaseAuditProgress>(value =>
                _legacyStatus.Text = $"{value.Stage}{(value.Table is null ? string.Empty : $" · {value.Table}")} · {value.CompletedTables:N0}/{value.TotalTables:N0} tables · {value.Rows:N0} change records");
            var options = new LegacyDatabaseAuditOptions(Patterns(_legacyIncludes.Text), Patterns(_legacyExcludes.Text));
            var result = await new LegacyDatabaseAuditService().AuditAsync(legacy, output, baseline, options, progress, _operation!.Token);
            _recoveryAudit.Text = result.Path;
            _legacyStatus.Text = $"Created {(result.Manifest.Mode == LegacyDatabaseAuditMode.BaselineCompared ? "baseline-attributed" : "unattributed")} audit with {result.Manifest.TotalChangeRecords:N0} change records and {result.Manifest.TotalChangedFields:N0} changed fields.\nArtifact: {result.Path}\nNo target database was modified; build a target comparison plan below when the audit is attributable.";
        }
        catch (OperationCanceledException) { _legacyStatus.Text = "Recovery audit cancelled; no partial artifact was published."; }
        catch (Exception exception) { _legacyStatus.Text = $"Recovery audit failed: {exception.Message}"; DesktopCrashLogger.Log("Legacy recovery audit failed", exception); }
        finally { EndOperation(); }
    }

    private async Task InspectAuditAsync()
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Verify a Crucible database recovery audit",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Crucible database recovery audit") { Patterns = ["*.crucible-db-audit"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        _recoveryAudit.Text = path;
        BeginOperation("Verifying the recovery audit…");
        try
        {
            var result = await new LegacyDatabaseAuditService().InspectAsync(path, true, _operation!.Token);
            _legacyStatus.Text = result.Manifest is null
                ? $"Invalid audit: {string.Join("; ", result.Findings)}"
                : $"{(result.Valid ? "VALID" : "INVALID")} audit · {result.Manifest.Mode} · {result.Manifest.TotalChangeRecords:N0} changes · {result.Manifest.TotalChangedFields:N0} fields\n{(result.Findings.Count == 0 ? "Every manifest, stream, count, and SHA-256 check passed." : string.Join(Environment.NewLine, result.Findings))}";
        }
        catch (OperationCanceledException) { _legacyStatus.Text = "Audit verification cancelled."; }
        catch (Exception exception) { _legacyStatus.Text = $"Audit verification failed: {exception.Message}"; DesktopCrashLogger.Log("Recovery audit verification failed", exception); }
        finally { EndOperation(); }
    }

    private async Task BuildDatabaseSyncPlanAsync()
    {
        if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _legacyStatus.Text = "Connect and verify the target database first."; return; }
        var audit = _recoveryAudit.Text; if (string.IsNullOrWhiteSpace(audit) || !File.Exists(audit)) { _legacyStatus.Text = "Choose a verified baseline-to-edited recovery audit first."; return; }
        uint? remapStart = null;
        if (!string.IsNullOrWhiteSpace(_recoveryRemapStart.Text))
        {
            if (!uint.TryParse(_recoveryRemapStart.Text.Trim(), out var parsed) || parsed == 0) { _legacyStatus.Text = "The optional first remapped ID must be a positive whole number."; return; }
            remapStart = parsed;
        }
        var destination = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save target-bound database synchronization plan", SuggestedFileName = $"{_session.DatabaseProfile.Database}.crucible-db-sync.json", FileTypeChoices = [new FilePickerFileType("Crucible database synchronization plan") { Patterns = ["*.crucible-db-sync.json", "*.json"] }] });
        var output = destination?.TryGetLocalPath(); if (output is null) return; if (File.Exists(output)) { _legacyStatus.Text = "That plan path already exists. Choose a new file so a reviewed plan is never silently replaced."; return; }
        BeginOperation("Comparing every selected audit row with the connected target…");
        try
        {
            var progress = new Progress<(string Stage, string? Table, int Completed, int Total)>(value => _legacyStatus.Text = $"{value.Stage}{(value.Table is null ? string.Empty : $" · {value.Table}")} · {value.Completed:N0}/{value.Total:N0} tables");
            var translation = string.IsNullOrWhiteSpace(_recoveryTranslation.Text) ? null : Path.GetFullPath(_recoveryTranslation.Text);
            var result = await new DatabaseSynchronizationService().BuildPlanAsync(audit, _session.DatabaseProfile, output, new(Patterns(_legacyIncludes.Text), _recoveryRemovals.IsChecked == true, AutoRemapCollisions: _recoveryAutoRemap.IsChecked == true, RemapStart: remapStart, IncludeDependencyClosure: _recoveryDependencyClosure.IsChecked == true, TranslationProfilePath: translation), progress, _operation!.Token);
            _recoveryPlan.Text = result.Path; ShowDatabaseSyncPlan(result.Plan);
        }
        catch (OperationCanceledException) { _legacyStatus.Text = "Target comparison cancelled; no partial plan was published."; }
        catch (Exception exception) { _legacyStatus.Text = $"Target comparison failed: {exception.Message}"; DesktopCrashLogger.Log("Database synchronization plan failed", exception); }
        finally { EndOperation(); }
    }

    private async Task BuildSchemaBridgeAsync()
    {
        if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _legacyStatus.Text = "Connect and verify the target database before generating its schema bridge."; return; }
        var audit = _recoveryAudit.Text; if (string.IsNullOrWhiteSpace(audit) || !File.Exists(audit)) { _legacyStatus.Text = "Choose a verified baseline-to-edited recovery audit first."; return; }
        var destination = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save editable target-schema bridge", SuggestedFileName = $"{_session.DatabaseProfile.Database}.crucible-db-bridge.json", FileTypeChoices = [new FilePickerFileType("Crucible database schema bridge") { Patterns = ["*.crucible-db-bridge.json", "*.json"] }] });
        var output = destination?.TryGetLocalPath(); if (output is null) return; if (File.Exists(output)) { _legacyStatus.Text = "That bridge path already exists. Choose a new path so reviewed mappings are never silently replaced."; return; }
        BeginOperation("Comparing audited source fields with the connected target schema…");
        try
        {
            var progress = new Progress<(string Stage, string? Table, int Completed, int Total)>(value => _legacyStatus.Text = $"{value.Stage}{(value.Table is null ? string.Empty : $" · {value.Table}")} · {value.Completed:N0}/{value.Total:N0} tables");
            var result = await new DatabaseSynchronizationService().BuildTranslationTemplateAsync(audit, _session.DatabaseProfile, output, Patterns(_legacyIncludes.Text), progress: progress, cancellationToken: _operation!.Token);
            _recoveryTranslation.Text = result.Path;
            var capabilities = await new DatabaseCapabilityService().InspectAsync(_session.DatabaseProfile, _operation!.Token); SetBridgeEditor(result.Profile, capabilities, result.Path);
            var unresolvedTables = result.Profile.Tables.Count(table => string.IsNullOrWhiteSpace(table.TargetTable));
            var unresolvedColumns = result.Profile.Tables.Sum(table => table.ColumnMappings.Count(mapping => string.IsNullOrWhiteSpace(mapping.TargetColumn)) + table.TargetDefaults.Count(value => value.Value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing));
            _legacyStatus.Text = $"Generated an editable, audit-bound schema bridge with {result.Profile.Tables.Count:N0} table rule(s) from {result.Operations:N0} changed row(s).\n{unresolvedTables:N0} table target(s) and {unresolvedColumns:N0} column/default value(s) require review. Blank mappings intentionally block planning; assign a target name, move a non-key source column into DroppedSourceColumns, or provide a typed target default.\nArtifact: {result.Path}";
        }
        catch (OperationCanceledException) { _legacyStatus.Text = "Schema bridge generation cancelled; no partial artifact was published."; }
        catch (Exception exception) { _legacyStatus.Text = $"Schema bridge generation failed safely: {exception.Message}"; DesktopCrashLogger.Log("Database schema bridge generation failed", exception); }
        finally { EndOperation(); }
    }

    private Control BuildSchemaBridgeEditor()
    {
        const string unresolved = "⟨unresolved — blocks planning⟩";
        var load = new Button { Content = "Load selected profile" }; load.Click += async (_, _) => await LoadSchemaBridgeEditorAsync(_recoveryTranslation.Text);
        var save = AccentButton("Save reviewed bridge"); save.Click += async (_, _) => await SaveSchemaBridgeEditorAsync();
        _bridgeTable.SelectionChanged += (_, _) =>
        {
            if (_bridgeUiChanging || _bridgeTable.SelectedItem is not BridgeTableChoice choice) return;
            CommitBridgeExpansion(); CommitBridgeLookup(); CommitBridgeRule(); _bridgeRuleIndex = choice.Index; _bridgeExpansionIndex = _bridgeLookupIndex = -1; RenderBridgeRule();
        };
        _bridgeTargetTable.SelectionChanged += (_, _) =>
        {
            if (_bridgeUiChanging || _loadedBridge is null || _bridgeRuleIndex < 0) return;
            CommitBridgeExpansion(); CommitBridgeLookup(); CommitBridgeRule(); var selected = Convert.ToString(_bridgeTargetTable.SelectedItem) ?? unresolved;
            var rules = _loadedBridge.Tables.ToArray(); rules[_bridgeRuleIndex] = rules[_bridgeRuleIndex] with { TargetTable = selected == unresolved ? string.Empty : selected, ColumnMappings = [], DroppedSourceColumns = [], TargetDefaults = [] };
            _loadedBridge = _loadedBridge with { Tables = rules }; RenderBridgeRule();
        };
        _bridgeSuppressPrimary.IsCheckedChanged += (_, _) =>
        {
            if (_bridgeUiChanging || _loadedBridge is null || _bridgeRuleIndex < 0) return;
            var rules = _loadedBridge.Tables.ToArray(); rules[_bridgeRuleIndex] = rules[_bridgeRuleIndex] with { SuppressPrimaryOutput = _bridgeSuppressPrimary.IsChecked == true };
            _loadedBridge = _loadedBridge with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion, Tables = rules }; RefreshBridgeStatus(); RefreshBridgeExpansionStatus();
        };
        var header = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 8, Children =
            {
                new TextBlock { Text = "Exact cross-core schema bridge", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Select a source table and target table. Every observed source column then maps by name, maps to a selected target column, or is explicitly dropped. Primary keys cannot be dropped. Required INSERT defaults stay Unknown until reviewed; unresolved values block the plan.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) },
                new WrapPanel { Children = { load, save } }, _bridgeStatus,
                new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { _bridgeTable, WithColumn(_bridgeTargetTable, 1) } }
            }
        };
        _bridgeColumns.ItemTemplate = new FuncDataTemplate<BridgeColumnChoice>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var targets = new[] { unresolved }.Concat(_bridgeCapabilities?.FindTable(CurrentBridgeTarget())?.Columns.Select(column => column.Name) ?? []).ToArray();
            var target = new ComboBox { ItemsSource = targets, SelectedItem = string.IsNullOrWhiteSpace(row.Target) ? unresolved : row.Target };
            var drop = new CheckBox { Content = row.IsKey ? "Primary key" : "Drop explicitly", IsChecked = row.Drop, IsEnabled = !row.IsKey };
            target.SelectionChanged += (_, _) => { var selected = Convert.ToString(target.SelectedItem) ?? unresolved; row.Target = selected == unresolved ? string.Empty : selected; if (row.Target.Length > 0) { row.Drop = false; drop.IsChecked = false; } RebuildBridgeDefaultRows(); RefreshBridgeStatus(); };
            drop.IsCheckedChanged += (_, _) => { row.Drop = drop.IsChecked == true; if (row.Drop) { row.Target = string.Empty; target.SelectedItem = unresolved; } RebuildBridgeDefaultRows(); RefreshBridgeStatus(); };
            return new Grid { Margin = new Thickness(8, 4), ColumnDefinitions = new("*,*,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = row.Source, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, WithColumn(target, 1), WithColumn(drop, 2) } };
        });
        _bridgeDefaults.ItemTemplate = new FuncDataTemplate<BridgeDefaultChoice>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var state = new ComboBox { ItemsSource = new[] { LegacyDatabaseAuditValueState.Unknown, LegacyDatabaseAuditValueState.Scalar, LegacyDatabaseAuditValueState.Null, LegacyDatabaseAuditValueState.Binary }, SelectedItem = row.State };
            var value = new TextBox { Text = row.Value, PlaceholderText = "Scalar text or base64 bytes" };
            state.SelectionChanged += (_, _) => { if (state.SelectedItem is LegacyDatabaseAuditValueState selected) row.State = selected; value.IsEnabled = row.State is LegacyDatabaseAuditValueState.Scalar or LegacyDatabaseAuditValueState.Binary; RefreshBridgeStatus(); };
            value.TextChanged += (_, _) => { row.Value = value.Text ?? string.Empty; RefreshBridgeStatus(); };
            value.IsEnabled = row.State is LegacyDatabaseAuditValueState.Scalar or LegacyDatabaseAuditValueState.Binary;
            return new Grid { Margin = new Thickness(8, 4), ColumnDefinitions = new("*,Auto,*"), ColumnSpacing = 8, Children = { new TextBlock { Text = row.Target, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, WithColumn(state, 1), WithColumn(value, 2) } };
        });
        var primary = new Grid
        {
            RowDefinitions = new("Auto,Auto,*,Auto,*"),
            Children =
            {
                header,
                WithRow(new TextBlock { Margin = new Thickness(10, 5), Text = "Observed source columns", FontWeight = FontWeight.SemiBold }, 1),
                WithRow(_bridgeColumns, 2),
                WithRow(new TextBlock { Margin = new Thickness(10, 5), Text = "Required target defaults for added rows", FontWeight = FontWeight.SemiBold }, 3),
                WithRow(_bridgeDefaults, 4)
            }
        };
        return new TabControl
        {
            Items =
            {
                new TabItem { Header = "Primary row mapping", Content = primary },
                new TabItem { Header = "Structural row expansions", Content = BuildStructuralExpansionEditor() },
                new TabItem { Header = "Named row lookups", Content = BuildBridgeLookupEditor() }
            }
        };
    }

    private Control BuildStructuralExpansionEditor()
    {
        const string unresolved = "⟨unresolved — blocks planning⟩";
        var add = AccentButton("Add structural output"); add.Click += (_, _) => AddBridgeExpansion();
        var remove = new Button { Content = "Remove selected output" }; remove.Click += (_, _) => RemoveBridgeExpansion();
        _bridgeExpansion.SelectionChanged += (_, _) =>
        {
            if (_bridgeUiChanging || _bridgeExpansion.SelectedItem is not BridgeExpansionChoice choice) return;
            CommitBridgeExpansion(); _bridgeExpansionIndex = choice.Index; RenderBridgeExpansion();
        };
        _bridgeExpansionName.TextChanged += (_, _) => { if (!_bridgeUiChanging) { CommitBridgeExpansion(); RefreshBridgeExpansionChoice(); } };
        _bridgeExpansionTargetTable.SelectionChanged += (_, _) =>
        {
            if (_bridgeUiChanging || CurrentBridgeExpansion() is null) return;
            var selected = Convert.ToString(_bridgeExpansionTargetTable.SelectedItem) ?? unresolved; var rules = _loadedBridge!.Tables.ToArray(); var rule = rules[_bridgeRuleIndex]; var expansions = (rule.Expansions ?? []).ToArray();
            expansions[_bridgeExpansionIndex] = expansions[_bridgeExpansionIndex] with { TargetTable = selected == unresolved ? string.Empty : selected, KeyBindings = [], FieldBindings = [] };
            rules[_bridgeRuleIndex] = rule with { Expansions = expansions }; _loadedBridge = _loadedBridge with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion, Tables = rules }; RenderBridgeExpansion();
        };
        _bridgeExpansionTargetKind.ItemsSource = new[] { LegacyDatabaseRowChangeKind.Added, LegacyDatabaseRowChangeKind.Modified, LegacyDatabaseRowChangeKind.Removed };
        _bridgeExpansionTargetKind.SelectionChanged += (_, _) => { if (!_bridgeUiChanging) { CommitBridgeExpansion(); RenderBridgeExpansionBindings(); } };
        foreach (var sourceKind in new[] { _bridgeExpansionAdded, _bridgeExpansionModified, _bridgeExpansionRemoved })
            sourceKind.IsCheckedChanged += (_, _) =>
            {
                if (_bridgeUiChanging) return;
                if (_bridgeExpansionAdded.IsChecked != true && _bridgeExpansionModified.IsChecked != true && _bridgeExpansionRemoved.IsChecked != true)
                { _bridgeUiChanging = true; sourceKind.IsChecked = true; _bridgeUiChanging = false; }
                CommitBridgeExpansion(); RefreshBridgeExpansionStatus();
            };
        _bridgeExpansionBindings.ItemTemplate = new FuncDataTemplate<BridgeExpansionBindingChoice>((row, _) => row is null ? new TextBlock() : BuildBridgeExpansionBindingRow(row));
        return new Grid
        {
            Margin = new Thickness(10), RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 8,
            Children =
            {
                new StackPanel { Spacing = 7, Children =
                {
                    new TextBlock { Text = "Reviewed one-to-many row conversion", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "A structural output converts one audited source row into an additional normalized target row. Select exactly which source change kinds produce it, its target operation kind, the complete target primary key, and every required before/after value. Each value comes from an audited source column or a typed constant; Crucible never infers joins or values.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) },
                    _bridgeSuppressPrimary,
                    new WrapPanel { Children = { add, remove } },
                    _bridgeExpansionStatus
                } },
                WithRow(new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { _bridgeExpansion, WithColumn(_bridgeExpansionName, 1) } }, 1),
                WithRow(new StackPanel { Spacing = 7, Children =
                {
                    new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { _bridgeExpansionTargetTable, WithColumn(_bridgeExpansionTargetKind, 1) } },
                    new WrapPanel { Children = { _bridgeExpansionAdded, _bridgeExpansionModified, _bridgeExpansionRemoved } }
                } }, 2),
                WithRow(_bridgeExpansionBindings, 3)
            }
        };
    }

    private Control BuildBridgeExpansionBindingRow(BridgeExpansionBindingChoice row)
    {
        var expansion = CurrentBridgeExpansion(); var targetKind = expansion?.TargetKind ?? LegacyDatabaseRowChangeKind.Added;
        var include = new CheckBox { Content = row.IsKey ? $"PRIMARY KEY · {row.Target}" : row.Required ? $"REQUIRED · {row.Target}" : row.Target, IsChecked = row.Included, IsEnabled = !row.IsKey && !row.Required };
        var values = new StackPanel { Spacing = 5 };
        if (row.IsKey) values.Children.Add(BuildBridgeExpansionValueEditor("Target key value", row.KeyValue, value => { row.KeyValue = value; RefreshBridgeExpansionStatus(); }));
        else
        {
            if (targetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Removed)
                values.Children.Add(BuildBridgeExpansionValueEditor("Expected target preimage", row.Before, value => { row.Before = value; RefreshBridgeExpansionStatus(); }));
            if (targetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Added)
                values.Children.Add(BuildBridgeExpansionValueEditor("Target postimage", row.After, value => { row.After = value; RefreshBridgeExpansionStatus(); }));
        }
        values.IsVisible = row.Included;
        include.IsCheckedChanged += (_, _) => { row.Included = include.IsChecked == true; values.IsVisible = row.Included; RefreshBridgeExpansionStatus(); };
        return new Border
        {
            Margin = new Thickness(4), Padding = new Thickness(8), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 6, Children = { include, values } }
        };
    }

    private Control BuildBridgeLookupEditor()
    {
        const string unresolved = "⟨unresolved — blocks planning⟩";
        var add = AccentButton("Add named lookup"); add.Click += (_, _) => AddBridgeLookup();
        var remove = new Button { Content = "Remove selected lookup" }; remove.Click += (_, _) => RemoveBridgeLookup();
        _bridgeLookupSource.ItemsSource = Enum.GetValues<DatabaseSyncLookupSource>();
        _bridgeLookupResultVersion.ItemsSource = new[] { DatabaseSyncExpansionValueSource.SourceBefore, DatabaseSyncExpansionValueSource.SourceAfter };
        _bridgeLookup.SelectionChanged += (_, _) =>
        {
            if (_bridgeUiChanging || _bridgeLookup.SelectedItem is not BridgeLookupChoice choice) return;
            CommitBridgeLookup(); _bridgeLookupIndex = choice.Index; RenderBridgeLookup();
        };
        _bridgeLookupName.TextChanged += (_, _) => { if (!_bridgeUiChanging) { CommitBridgeLookup(); RefreshBridgeLookupList(); } };
        _bridgeLookupSource.SelectionChanged += (_, _) =>
        {
            if (_bridgeUiChanging || CurrentBridgeLookup() is null || _bridgeLookupSource.SelectedItem is not DatabaseSyncLookupSource source) return;
            CommitBridgeLookup(); ReplaceBridgeLookup(CurrentBridgeLookup()! with { Source = source, Table = string.Empty, ResultColumn = string.Empty, Matches = [] }); RenderBridgeLookup();
        };
        _bridgeLookupTable.SelectionChanged += (_, _) =>
        {
            if (_bridgeUiChanging || CurrentBridgeLookup() is null) return;
            var selected = Convert.ToString(_bridgeLookupTable.SelectedItem) ?? unresolved;
            CommitBridgeLookup(); ReplaceBridgeLookup(CurrentBridgeLookup()! with { Table = selected == unresolved ? string.Empty : selected, ResultColumn = string.Empty, Matches = [] }); RenderBridgeLookup();
        };
        _bridgeLookupResultColumn.SelectionChanged += (_, _) => { if (!_bridgeUiChanging) { CommitBridgeLookup(); RefreshBridgeLookupStatus(); } };
        _bridgeLookupResultVersion.SelectionChanged += (_, _) => { if (!_bridgeUiChanging) { CommitBridgeLookup(); RefreshBridgeLookupStatus(); } };
        _bridgeLookupMatches.ItemTemplate = new FuncDataTemplate<BridgeLookupMatchChoice>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var include = new CheckBox { Content = row.LookupColumn, IsChecked = row.Included };
            var editor = BuildBridgeExpansionValueEditor("Exact match input", row.Input, value => { row.Input = value; RefreshBridgeLookupStatus(); }, allowLookup: false);
            editor.IsVisible = row.Included;
            include.IsCheckedChanged += (_, _) => { row.Included = include.IsChecked == true; editor.IsVisible = row.Included; RefreshBridgeLookupStatus(); };
            return new Border { Margin = new Thickness(4), Padding = new Thickness(8), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = new StackPanel { Spacing = 5, Children = { include, editor } } };
        });
        return new Grid
        {
            Margin = new Thickness(10), RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 8,
            Children =
            {
                new StackPanel { Spacing = 7, Children =
                {
                    new TextBlock { Text = "Exact named row lookups", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "A lookup may resolve one value from another audited changed row or from the bound live target database. Every match is typed and exact. Zero matches, multiple matches, missing columns, or a changed live result block the plan/apply; arbitrary SQL and recursive lookups are never accepted.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) },
                    new WrapPanel { Children = { add, remove } }, _bridgeLookupStatus
                } },
                WithRow(new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { _bridgeLookup, WithColumn(_bridgeLookupName, 1) } }, 1),
                WithRow(new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 8, Children = { _bridgeLookupSource, WithColumn(_bridgeLookupTable, 1), WithColumn(_bridgeLookupResultColumn, 2), WithColumn(_bridgeLookupResultVersion, 3) } }, 2),
                WithRow(_bridgeLookupMatches, 3)
            }
        };
    }

    private void AddBridgeLookup()
    {
        if (_loadedBridge is null || _bridgeRuleIndex < 0) { _bridgeLookupStatus.Text = "Load a bridge and select a source table first."; return; }
        CommitBridgeExpansion(); CommitBridgeLookup(); CommitBridgeRule(); var rules = _loadedBridge.Tables.ToArray(); var rule = rules[_bridgeRuleIndex]; var lookups = (rule.Lookups ?? []).ToList();
        var number = 1; string name; do name = $"Lookup {number++}"; while (lookups.Any(lookup => lookup.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        lookups.Add(new(name, DatabaseSyncLookupSource.AuditChanges, string.Empty, [], string.Empty)); rules[_bridgeRuleIndex] = rule with { Lookups = lookups };
        _loadedBridge = _loadedBridge with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion, Tables = rules }; _bridgeLookupIndex = lookups.Count - 1; RenderBridgeLookupList(); RenderBridgeExpansionBindings();
    }

    private void RemoveBridgeLookup()
    {
        if (_loadedBridge is null || _bridgeRuleIndex < 0 || _bridgeLookupIndex < 0) return;
        var rules = _loadedBridge.Tables.ToArray(); var rule = rules[_bridgeRuleIndex]; var lookups = (rule.Lookups ?? []).ToList(); if (_bridgeLookupIndex >= lookups.Count) return;
        var removed = lookups[_bridgeLookupIndex].Name; lookups.RemoveAt(_bridgeLookupIndex); rules[_bridgeRuleIndex] = rule with { Lookups = lookups };
        _loadedBridge = _loadedBridge with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion, Tables = rules }; _bridgeLookupIndex = lookups.Count == 0 ? -1 : Math.Min(_bridgeLookupIndex, lookups.Count - 1); RenderBridgeLookupList(); RenderBridgeExpansionBindings();
        _bridgeLookupStatus.Text = $"Removed '{removed}'. Any structural value still naming it will remain visibly unresolved until reassigned.";
    }

    private void RenderBridgeLookupList()
    {
        var lookups = CurrentBridgeRule()?.Lookups ?? []; if (_bridgeLookupIndex >= lookups.Count) _bridgeLookupIndex = lookups.Count == 0 ? -1 : 0;
        _bridgeUiChanging = true; _bridgeLookup.ItemsSource = lookups.Select((lookup, index) => new BridgeLookupChoice(index, $"{lookup.Name} · {lookup.Source} → {(string.IsNullOrWhiteSpace(lookup.Table) ? "UNRESOLVED" : lookup.Table)}.{lookup.ResultColumn}")).ToArray(); _bridgeLookup.SelectedIndex = _bridgeLookupIndex; _bridgeUiChanging = false; RenderBridgeLookup();
    }

    private void RefreshBridgeLookupList()
    {
        if (_loadedBridge is null) return; var selected = _bridgeLookupIndex; var lookups = CurrentBridgeRule()?.Lookups ?? [];
        _bridgeUiChanging = true; _bridgeLookup.ItemsSource = lookups.Select((lookup, index) => new BridgeLookupChoice(index, $"{lookup.Name} · {lookup.Source} → {(string.IsNullOrWhiteSpace(lookup.Table) ? "UNRESOLVED" : lookup.Table)}.{lookup.ResultColumn}")).ToArray(); _bridgeLookup.SelectedIndex = selected; _bridgeUiChanging = false;
    }

    private void RenderBridgeLookup()
    {
        const string unresolved = "⟨unresolved — blocks planning⟩"; var lookup = CurrentBridgeLookup();
        _bridgeUiChanging = true; _bridgeLookupName.Text = lookup?.Name ?? string.Empty; _bridgeLookupSource.SelectedItem = lookup?.Source ?? DatabaseSyncLookupSource.AuditChanges;
        var tables = lookup?.Source == DatabaseSyncLookupSource.TargetDatabase
            ? _bridgeCapabilities?.Tables.Keys.Order(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>()
            : _loadedBridge?.Tables.Select(rule => rule.SourceTable).Order(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>();
        _bridgeLookupTable.ItemsSource = new[] { unresolved }.Concat(tables).ToArray(); _bridgeLookupTable.SelectedItem = lookup is null || string.IsNullOrWhiteSpace(lookup.Table) ? unresolved : lookup.Table;
        var columns = LookupColumns(lookup).ToArray(); _bridgeLookupResultColumn.ItemsSource = new[] { unresolved }.Concat(columns).ToArray(); _bridgeLookupResultColumn.SelectedItem = lookup is null || string.IsNullOrWhiteSpace(lookup.ResultColumn) ? unresolved : lookup.ResultColumn;
        _bridgeLookupResultVersion.SelectedItem = lookup?.ResultVersion ?? DatabaseSyncExpansionValueSource.SourceAfter; _bridgeLookupResultVersion.IsVisible = lookup?.Source == DatabaseSyncLookupSource.AuditChanges; _bridgeUiChanging = false;
        var matches = (lookup?.Matches ?? []).ToDictionary(match => match.LookupColumn, StringComparer.OrdinalIgnoreCase);
        _bridgeLookupRows = columns.Select(column => new BridgeLookupMatchChoice { LookupColumn = column, Included = matches.ContainsKey(column), Input = matches.GetValueOrDefault(column)?.Input }).ToList(); _bridgeLookupMatches.ItemsSource = _bridgeLookupRows; RefreshBridgeLookupStatus();
    }

    private IEnumerable<string> LookupColumns(DatabaseSyncRowLookup? lookup)
    {
        if (lookup is null || string.IsNullOrWhiteSpace(lookup.Table)) return [];
        if (lookup.Source == DatabaseSyncLookupSource.TargetDatabase) return _bridgeCapabilities?.FindTable(lookup.Table)?.Columns.Select(column => column.Name) ?? [];
        return _loadedBridge?.Tables.FirstOrDefault(rule => rule.SourceTable.Equals(lookup.Table, StringComparison.OrdinalIgnoreCase))?.ObservedSourceColumns ?? [];
    }

    private void CommitBridgeLookup()
    {
        var lookup = CurrentBridgeLookup(); if (_loadedBridge is null || lookup is null) return; const string unresolved = "⟨unresolved — blocks planning⟩";
        var source = _bridgeLookupSource.SelectedItem is DatabaseSyncLookupSource selectedSource ? selectedSource : lookup.Source; var table = Convert.ToString(_bridgeLookupTable.SelectedItem) ?? string.Empty; var result = Convert.ToString(_bridgeLookupResultColumn.SelectedItem) ?? string.Empty;
        var matches = _bridgeLookupRows.Where(row => row.Included).Select(row => new DatabaseSyncLookupMatch(row.LookupColumn, row.Input ?? new(DatabaseSyncExpansionValueSource.SourceAfter, string.Empty))).ToArray();
        ReplaceBridgeLookup(lookup with { Name = string.IsNullOrWhiteSpace(_bridgeLookupName.Text) ? lookup.Name : _bridgeLookupName.Text.Trim(), Source = source, Table = table == unresolved ? string.Empty : table, ResultColumn = result == unresolved ? string.Empty : result, ResultVersion = _bridgeLookupResultVersion.SelectedItem is DatabaseSyncExpansionValueSource version ? version : DatabaseSyncExpansionValueSource.SourceAfter, Matches = matches });
    }

    private void ReplaceBridgeLookup(DatabaseSyncRowLookup lookup)
    {
        if (_loadedBridge is null || _bridgeRuleIndex < 0 || _bridgeLookupIndex < 0) return; var rules = _loadedBridge.Tables.ToArray(); var rule = rules[_bridgeRuleIndex]; var lookups = (rule.Lookups ?? []).ToArray(); if (_bridgeLookupIndex >= lookups.Length) return;
        lookups[_bridgeLookupIndex] = lookup; rules[_bridgeRuleIndex] = rule with { Lookups = lookups }; _loadedBridge = _loadedBridge with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion, Tables = rules };
    }

    private DatabaseSyncRowLookup? CurrentBridgeLookup()
    {
        var lookups = CurrentBridgeRule()?.Lookups ?? []; return _bridgeLookupIndex >= 0 && _bridgeLookupIndex < lookups.Count ? lookups[_bridgeLookupIndex] : null;
    }

    private void RefreshBridgeLookupStatus()
    {
        var rule = CurrentBridgeRule(); var lookup = CurrentBridgeLookup(); if (rule is null) { _bridgeLookupStatus.Text = "Load a bridge and select a source table first."; return; }
        if (lookup is null) { _bridgeLookupStatus.Text = $"{rule.SourceTable} has no named lookups. Add one only when a structural output requires a value from another exact row."; return; }
        var matches = _bridgeLookupRows.Count(row => row.Included); var unresolvedMatches = _bridgeLookupRows.Count(row => row.Included && ExpansionValueUnresolved(row.Input, allowLookup: false));
        _bridgeLookupStatus.Text = $"{rule.SourceTable} · {(rule.Lookups?.Count ?? 0):N0} lookup(s) · '{lookup.Name}' reads {lookup.Source} {(string.IsNullOrWhiteSpace(lookup.Table) ? "UNRESOLVED" : lookup.Table)}.{(string.IsNullOrWhiteSpace(lookup.ResultColumn) ? "UNRESOLVED" : lookup.ResultColumn)} with {matches:N0} exact match(s), {unresolvedMatches:N0} unresolved input(s). Zero or multiple matching rows block planning.";
    }

    private Control BuildBridgeExpansionValueEditor(string label, DatabaseSyncExpansionValue? current, Action<DatabaseSyncExpansionValue?> changed, bool allowLookup = true)
    {
        var rule = CurrentBridgeRule(); var observed = rule?.ObservedSourceColumns ?? [];
        var unresolved = new BridgeValueOption(null, string.Empty, "⟨unresolved — blocks planning⟩");
        var constant = new BridgeValueOption(DatabaseSyncExpansionValueSource.Constant, string.Empty, "Typed constant");
        var options = new[] { unresolved }
            .Concat(observed.Select(column => new BridgeValueOption(DatabaseSyncExpansionValueSource.SourceBefore, column, $"Source before · {column}")))
            .Concat(observed.Select(column => new BridgeValueOption(DatabaseSyncExpansionValueSource.SourceAfter, column, $"Source after · {column}")))
            .Concat(allowLookup ? (rule?.Lookups ?? []).Select(lookup => new BridgeValueOption(DatabaseSyncExpansionValueSource.Lookup, string.Empty, $"Named lookup · {lookup.Name}", lookup.Name)) : [])
            .Append(constant).ToArray();
        var picker = new ComboBox { ItemsSource = options };
        var state = new ComboBox { ItemsSource = new[] { LegacyDatabaseAuditValueState.Unknown, LegacyDatabaseAuditValueState.Scalar, LegacyDatabaseAuditValueState.Null, LegacyDatabaseAuditValueState.Binary } };
        var value = new TextBox { PlaceholderText = "Scalar text or base64 bytes" };
        var transformKind = new ComboBox { ItemsSource = Enum.GetValues<DatabaseSyncValueTransformKind>() };
        var transformValue = new TextBox { PlaceholderText = "Transform operand" };
        DatabaseSyncExpansionValue? selected = current;
        picker.SelectedItem = current is null ? unresolved : options.FirstOrDefault(option => option.Source == current.Source && (current.Source == DatabaseSyncExpansionValueSource.Constant || current.Source == DatabaseSyncExpansionValueSource.Lookup ? string.Equals(option.LookupName, current.LookupName, StringComparison.OrdinalIgnoreCase) : option.SourceColumn.Equals(current.SourceColumn, StringComparison.OrdinalIgnoreCase))) ?? unresolved;
        state.SelectedItem = current?.Constant?.State ?? LegacyDatabaseAuditValueState.Unknown; value.Text = current?.Constant?.Value ?? string.Empty;
        transformKind.SelectedItem = current?.Transform?.Kind ?? DatabaseSyncValueTransformKind.None; transformValue.Text = TransformEditorText(current?.Transform);
        void RefreshEditors()
        {
            var isConstant = selected?.Source == DatabaseSyncExpansionValueSource.Constant; state.IsVisible = value.IsVisible = isConstant;
            value.IsEnabled = isConstant && state.SelectedItem is LegacyDatabaseAuditValueState.Scalar or LegacyDatabaseAuditValueState.Binary;
            var hasValue = selected is not null; transformKind.IsVisible = hasValue; transformValue.IsVisible = hasValue && transformKind.SelectedItem is DatabaseSyncValueTransformKind kind && kind != DatabaseSyncValueTransformKind.None;
            transformValue.PlaceholderText = transformKind.SelectedItem switch { DatabaseSyncValueTransformKind.NumericAdd or DatabaseSyncValueTransformKind.NumericMultiply => "Invariant decimal operand", DatabaseSyncValueTransformKind.StringPrefix or DatabaseSyncValueTransformKind.StringSuffix => "Text", DatabaseSyncValueTransformKind.ExactMap => "old=new; other=replacement", DatabaseSyncValueTransformKind.NullFallback => "Scalar fallback", _ => "Transform operand" };
        }
        picker.SelectionChanged += (_, _) =>
        {
            var transform = selected?.Transform;
            if (picker.SelectedItem is not BridgeValueOption option || option.Source is null) selected = null;
            else if (option.Source == DatabaseSyncExpansionValueSource.Constant)
                selected = new(DatabaseSyncExpansionValueSource.Constant, string.Empty, selected?.Constant ?? LegacyDatabaseAuditValue.Unknown, Transform: transform);
            else if (option.Source == DatabaseSyncExpansionValueSource.Lookup)
                selected = new(DatabaseSyncExpansionValueSource.Lookup, string.Empty, LookupName: option.LookupName, Transform: transform);
            else selected = new(option.Source.Value, option.SourceColumn, Transform: transform);
            RefreshEditors(); changed(selected);
        };
        state.SelectionChanged += (_, _) =>
        {
            if (selected?.Source != DatabaseSyncExpansionValueSource.Constant || state.SelectedItem is not LegacyDatabaseAuditValueState selectedState) return;
            var typed = selectedState switch { LegacyDatabaseAuditValueState.Null => LegacyDatabaseAuditValue.Null, LegacyDatabaseAuditValueState.Scalar => new(selectedState, value.Text ?? string.Empty), LegacyDatabaseAuditValueState.Binary => new(selectedState, value.Text ?? string.Empty), _ => LegacyDatabaseAuditValue.Unknown };
            selected = selected with { Constant = typed }; value.IsEnabled = selectedState is LegacyDatabaseAuditValueState.Scalar or LegacyDatabaseAuditValueState.Binary; changed(selected);
        };
        value.TextChanged += (_, _) =>
        {
            if (selected?.Source != DatabaseSyncExpansionValueSource.Constant || state.SelectedItem is not LegacyDatabaseAuditValueState selectedState || selectedState is not (LegacyDatabaseAuditValueState.Scalar or LegacyDatabaseAuditValueState.Binary)) return;
            selected = selected with { Constant = new(selectedState, value.Text ?? string.Empty) }; changed(selected);
        };
        transformKind.SelectionChanged += (_, _) =>
        {
            if (selected is null || transformKind.SelectedItem is not DatabaseSyncValueTransformKind kind) return;
            selected = selected with { Transform = TransformFromEditor(kind, transformValue.Text ?? string.Empty) }; RefreshEditors(); changed(selected);
        };
        transformValue.TextChanged += (_, _) =>
        {
            if (selected is null || transformKind.SelectedItem is not DatabaseSyncValueTransformKind kind || kind == DatabaseSyncValueTransformKind.None) return;
            selected = selected with { Transform = TransformFromEditor(kind, transformValue.Text ?? string.Empty) }; changed(selected);
        };
        RefreshEditors();
        return new StackPanel
        {
            Spacing = 4, Children =
            {
                new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.Parse("#99A5B8")) }, picker,
                new WrapPanel { Children = { state, value } },
                new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 5, Children = { transformKind, WithColumn(transformValue, 1) } }
            }
        };
    }

    private static DatabaseSyncValueTransform? TransformFromEditor(DatabaseSyncValueTransformKind kind, string text) => kind switch
    {
        DatabaseSyncValueTransformKind.None => null,
        DatabaseSyncValueTransformKind.ExactMap => new(kind, Mappings: text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries)).Where(parts => parts.Length == 2).Select(parts => new DatabaseSyncValueMap(new(LegacyDatabaseAuditValueState.Scalar, parts[0]), new(LegacyDatabaseAuditValueState.Scalar, parts[1]))).ToArray()),
        DatabaseSyncValueTransformKind.NullFallback => new(kind, Fallback: new(LegacyDatabaseAuditValueState.Scalar, text)),
        _ => new(kind, text)
    };

    private static string TransformEditorText(DatabaseSyncValueTransform? transform) => transform?.Kind switch
    {
        null or DatabaseSyncValueTransformKind.None => string.Empty,
        DatabaseSyncValueTransformKind.ExactMap => string.Join("; ", (transform.Mappings ?? []).Select(mapping => $"{mapping.Match.Value}={mapping.Result.Value}")),
        DatabaseSyncValueTransformKind.NullFallback => transform.Fallback?.Value ?? string.Empty,
        _ => transform.Operand
    };

    private static bool ExpansionValueUnresolved(DatabaseSyncExpansionValue? value, bool allowLookup = true)
    {
        if (value is null) return true;
        var sourceUnresolved = value.Source switch
        {
            DatabaseSyncExpansionValueSource.Constant => value.Constant is null || value.Constant.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing,
            DatabaseSyncExpansionValueSource.Lookup => !allowLookup || string.IsNullOrWhiteSpace(value.LookupName),
            _ => string.IsNullOrWhiteSpace(value.SourceColumn)
        };
        if (sourceUnresolved || value.Transform is null or { Kind: DatabaseSyncValueTransformKind.None }) return sourceUnresolved;
        return value.Transform.Kind switch
        {
            DatabaseSyncValueTransformKind.NumericAdd or DatabaseSyncValueTransformKind.NumericMultiply => !decimal.TryParse(value.Transform.Operand, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            DatabaseSyncValueTransformKind.ExactMap => (value.Transform.Mappings?.Count ?? 0) == 0,
            DatabaseSyncValueTransformKind.NullFallback => value.Transform.Fallback is null || value.Transform.Fallback.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing,
            _ => false
        };
    }

    private void AddBridgeExpansion()
    {
        if (_loadedBridge is null || _bridgeRuleIndex < 0) { _bridgeExpansionStatus.Text = "Load a bridge and select a source table first."; return; }
        CommitBridgeExpansion(); CommitBridgeRule(); var rules = _loadedBridge.Tables.ToArray(); var rule = rules[_bridgeRuleIndex]; var expansions = (rule.Expansions ?? []).ToList();
        var number = 1; string name; do name = $"Structural output {number++}"; while (expansions.Any(expansion => expansion.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        expansions.Add(new(name, string.Empty, [LegacyDatabaseRowChangeKind.Added], LegacyDatabaseRowChangeKind.Added, [], []));
        rules[_bridgeRuleIndex] = rule with { Expansions = expansions }; _loadedBridge = _loadedBridge with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion, Tables = rules }; _bridgeExpansionIndex = expansions.Count - 1; RenderBridgeExpansionList();
    }

    private void RemoveBridgeExpansion()
    {
        if (_loadedBridge is null || _bridgeRuleIndex < 0 || _bridgeExpansionIndex < 0) return;
        var rules = _loadedBridge.Tables.ToArray(); var rule = rules[_bridgeRuleIndex]; var expansions = (rule.Expansions ?? []).ToList(); if (_bridgeExpansionIndex >= expansions.Count) return;
        expansions.RemoveAt(_bridgeExpansionIndex); rules[_bridgeRuleIndex] = rule with { Expansions = expansions }; _loadedBridge = _loadedBridge with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion, Tables = rules };
        _bridgeExpansionIndex = expansions.Count == 0 ? -1 : Math.Min(_bridgeExpansionIndex, expansions.Count - 1); RenderBridgeExpansionList();
    }

    private void RenderBridgeExpansionList()
    {
        var expansions = CurrentBridgeRule()?.Expansions ?? []; if (_bridgeExpansionIndex >= expansions.Count) _bridgeExpansionIndex = expansions.Count == 0 ? -1 : 0;
        _bridgeUiChanging = true; _bridgeExpansion.ItemsSource = expansions.Select((expansion, index) => new BridgeExpansionChoice(index, $"{expansion.Name} → {(string.IsNullOrWhiteSpace(expansion.TargetTable) ? "UNRESOLVED" : expansion.TargetTable)}")).ToArray(); _bridgeExpansion.SelectedIndex = _bridgeExpansionIndex; _bridgeUiChanging = false;
        RenderBridgeExpansion();
    }

    private void RefreshBridgeExpansionChoice()
    {
        if (_loadedBridge is null || _bridgeExpansionIndex < 0) return; var selected = _bridgeExpansionIndex; var expansions = CurrentBridgeRule()?.Expansions ?? [];
        _bridgeUiChanging = true; _bridgeExpansion.ItemsSource = expansions.Select((expansion, index) => new BridgeExpansionChoice(index, $"{expansion.Name} → {(string.IsNullOrWhiteSpace(expansion.TargetTable) ? "UNRESOLVED" : expansion.TargetTable)}")).ToArray(); _bridgeExpansion.SelectedIndex = selected; _bridgeUiChanging = false;
    }

    private void RenderBridgeExpansion()
    {
        const string unresolved = "⟨unresolved — blocks planning⟩"; var expansion = CurrentBridgeExpansion();
        _bridgeUiChanging = true; _bridgeSuppressPrimary.IsChecked = CurrentBridgeRule()?.SuppressPrimaryOutput == true;
        _bridgeExpansionTargetTable.ItemsSource = new[] { unresolved }.Concat(_bridgeCapabilities?.Tables.Keys.Order(StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>()).ToArray();
        _bridgeExpansionName.Text = expansion?.Name ?? string.Empty; _bridgeExpansionTargetTable.SelectedItem = expansion is null || string.IsNullOrWhiteSpace(expansion.TargetTable) ? unresolved : expansion.TargetTable;
        _bridgeExpansionTargetKind.SelectedItem = expansion?.TargetKind ?? LegacyDatabaseRowChangeKind.Added;
        _bridgeExpansionAdded.IsChecked = expansion?.SourceKinds.Contains(LegacyDatabaseRowChangeKind.Added) == true; _bridgeExpansionModified.IsChecked = expansion?.SourceKinds.Contains(LegacyDatabaseRowChangeKind.Modified) == true; _bridgeExpansionRemoved.IsChecked = expansion?.SourceKinds.Contains(LegacyDatabaseRowChangeKind.Removed) == true;
        _bridgeUiChanging = false; RenderBridgeExpansionBindings();
    }

    private void RenderBridgeExpansionBindings()
    {
        var expansion = CurrentBridgeExpansion(); var target = expansion is null ? null : _bridgeCapabilities?.FindTable(expansion.TargetTable);
        if (expansion is null || target is null) { _bridgeExpansionRows = []; _bridgeExpansionBindings.ItemsSource = null; RefreshBridgeExpansionStatus(); return; }
        var keys = expansion.KeyBindings.ToDictionary(binding => binding.TargetColumn, StringComparer.OrdinalIgnoreCase); var fields = expansion.FieldBindings.ToDictionary(binding => binding.TargetColumn, StringComparer.OrdinalIgnoreCase);
        var rows = new List<BridgeExpansionBindingChoice>();
        foreach (var column in target.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal))
            rows.Add(new() { Target = column.Name, IsKey = true, Required = true, Included = true, KeyValue = keys.GetValueOrDefault(column.Name)?.Value });
        foreach (var column in target.Columns.Where(column => !column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) && !column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal))
        {
            fields.TryGetValue(column.Name, out var existing); var required = expansion.TargetKind == LegacyDatabaseRowChangeKind.Removed || expansion.TargetKind == LegacyDatabaseRowChangeKind.Added && !column.Nullable && column.DefaultValue is null && !column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
            rows.Add(new() { Target = column.Name, Required = required, Included = required || existing is not null, Before = existing?.Before, After = existing?.After });
        }
        _bridgeExpansionRows = rows; _bridgeExpansionBindings.ItemsSource = rows; RefreshBridgeExpansionStatus();
    }

    private void CommitBridgeExpansion()
    {
        var expansion = CurrentBridgeExpansion(); if (_loadedBridge is null || expansion is null) return;
        var sourceKinds = new List<LegacyDatabaseRowChangeKind>(); if (_bridgeExpansionAdded.IsChecked == true) sourceKinds.Add(LegacyDatabaseRowChangeKind.Added); if (_bridgeExpansionModified.IsChecked == true) sourceKinds.Add(LegacyDatabaseRowChangeKind.Modified); if (_bridgeExpansionRemoved.IsChecked == true) sourceKinds.Add(LegacyDatabaseRowChangeKind.Removed);
        var targetKind = _bridgeExpansionTargetKind.SelectedItem is LegacyDatabaseRowChangeKind selectedKind ? selectedKind : LegacyDatabaseRowChangeKind.Added;
        var keyBindings = _bridgeExpansionRows.Where(row => row.IsKey).Select(row => new DatabaseSyncExpansionKeyBinding(row.Target, row.KeyValue ?? new(DatabaseSyncExpansionValueSource.SourceAfter, string.Empty))).ToArray();
        var fieldBindings = _bridgeExpansionRows.Where(row => !row.IsKey && row.Included).Select(row => new DatabaseSyncExpansionFieldBinding(row.Target,
            targetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Removed ? row.Before : null,
            targetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Added ? row.After : null)).ToArray();
        var rules = _loadedBridge.Tables.ToArray(); var rule = rules[_bridgeRuleIndex]; var expansions = (rule.Expansions ?? []).ToArray();
        expansions[_bridgeExpansionIndex] = expansion with { Name = string.IsNullOrWhiteSpace(_bridgeExpansionName.Text) ? expansion.Name : _bridgeExpansionName.Text.Trim(), SourceKinds = sourceKinds, TargetKind = targetKind, KeyBindings = keyBindings, FieldBindings = fieldBindings };
        rules[_bridgeRuleIndex] = rule with { Expansions = expansions, SuppressPrimaryOutput = _bridgeSuppressPrimary.IsChecked == true }; _loadedBridge = _loadedBridge with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion, Tables = rules };
    }

    private DatabaseSyncTableTranslation? CurrentBridgeRule() => _loadedBridge is not null && _bridgeRuleIndex >= 0 && _bridgeRuleIndex < _loadedBridge.Tables.Count ? _loadedBridge.Tables[_bridgeRuleIndex] : null;
    private DatabaseSyncRowExpansion? CurrentBridgeExpansion()
    {
        var expansions = CurrentBridgeRule()?.Expansions ?? []; return _bridgeExpansionIndex >= 0 && _bridgeExpansionIndex < expansions.Count ? expansions[_bridgeExpansionIndex] : null;
    }

    private void RefreshBridgeExpansionStatus()
    {
        var rule = CurrentBridgeRule(); var expansion = CurrentBridgeExpansion();
        if (rule is null) { _bridgeExpansionStatus.Text = "Load a bridge and select a source table first."; return; }
        if (expansion is null) { _bridgeExpansionStatus.Text = rule.SuppressPrimaryOutput ? $"{rule.SourceTable} suppresses its primary row but has no structural output; planning is blocked until an output is added or suppression is cleared." : $"{rule.SourceTable} has no structural outputs. Add one only when one audited row must produce another normalized target row."; return; }
        var unresolvedKeys = _bridgeExpansionRows.Count(row => row.IsKey && ExpansionValueUnresolved(row.KeyValue)); var targetKind = _bridgeExpansionTargetKind.SelectedItem is LegacyDatabaseRowChangeKind kind ? kind : expansion.TargetKind;
        var unresolvedFields = _bridgeExpansionRows.Count(row => !row.IsKey && row.Included && ((targetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Removed && ExpansionValueUnresolved(row.Before)) || (targetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Added && ExpansionValueUnresolved(row.After))));
        var missingKinds = _bridgeExpansionAdded.IsChecked != true && _bridgeExpansionModified.IsChecked != true && _bridgeExpansionRemoved.IsChecked != true;
        _bridgeExpansionStatus.Text = $"{rule.SourceTable} · {(rule.Expansions?.Count ?? 0):N0} structural output(s) · '{expansion.Name}' → {(string.IsNullOrWhiteSpace(expansion.TargetTable) ? "UNRESOLVED" : expansion.TargetTable)} {targetKind}. {unresolvedKeys:N0} unresolved key binding(s), {unresolvedFields:N0} unresolved field binding(s){(missingKinds ? ", no source change kind selected" : string.Empty)}. Values may use audited columns, typed constants, named exact lookups, and bounded transforms; every unresolved value blocks planning.";
    }

    private async Task PickAndLoadSchemaBridgeAsync()
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select a target-schema bridge profile", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Crucible database schema bridge") { Patterns = ["*.crucible-db-bridge.json", "*.json"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return; _recoveryTranslation.Text = path; await LoadSchemaBridgeEditorAsync(path);
    }

    private async Task LoadSchemaBridgeEditorAsync(string? path)
    {
        if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _bridgeStatus.Text = "Connect and verify the target database before loading its bridge editor."; return; }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { _bridgeStatus.Text = "Choose or generate an existing schema bridge profile first."; return; }
        BeginOperation("Loading and verifying the schema bridge target…");
        try
        {
            var service = new DatabaseSyncTranslationService(); var profile = await service.LoadAsync(path, _operation!.Token); var capabilities = await new DatabaseCapabilityService().InspectAsync(_session.DatabaseProfile, _operation.Token);
            if (!profile.TargetSchemaSha256.Equals(DatabaseSyncTranslationService.HashTargetSchema(capabilities), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This bridge belongs to a different or older target schema. Generate a new bridge so mappings cannot silently drift.");
            _ = service.Translate([], profile, profile.SourceAuditSha256, capabilities); SetBridgeEditor(profile, capabilities, path);
        }
        catch (Exception exception) { _loadedBridge = null; _bridgeCapabilities = null; _bridgeRuleIndex = _bridgeExpansionIndex = _bridgeLookupIndex = -1; _bridgeColumns.ItemsSource = null; _bridgeDefaults.ItemsSource = null; _bridgeExpansion.ItemsSource = null; _bridgeExpansionBindings.ItemsSource = null; _bridgeLookup.ItemsSource = null; _bridgeLookupMatches.ItemsSource = null; _bridgeStatus.Text = $"Bridge load failed safely: {exception.Message}"; DesktopCrashLogger.Log("Database schema bridge load failed", exception); }
        finally { EndOperation(); }
    }

    private void SetBridgeEditor(DatabaseSyncTranslationProfile profile, DatabaseCapabilities capabilities, string path)
    {
        _loadedBridge = profile with { FormatVersion = DatabaseSyncTranslationService.ProfileFormatVersion }; _bridgeCapabilities = capabilities; _bridgeRuleIndex = profile.Tables.Count == 0 ? -1 : 0; _bridgeExpansionIndex = _bridgeLookupIndex = -1; _recoveryTranslation.Text = Path.GetFullPath(path);
        _bridgeUiChanging = true; _bridgeTable.ItemsSource = _loadedBridge.Tables.Select((rule, index) => new BridgeTableChoice(index, $"{rule.SourceTable} → {(string.IsNullOrWhiteSpace(rule.TargetTable) ? "UNRESOLVED" : rule.TargetTable)}")).ToArray(); _bridgeTable.SelectedIndex = _bridgeRuleIndex; _bridgeUiChanging = false; RenderBridgeRule();
    }

    private async Task SaveSchemaBridgeEditorAsync()
    {
        if (_loadedBridge is null || string.IsNullOrWhiteSpace(_recoveryTranslation.Text)) { _bridgeStatus.Text = "Load or generate a bridge before saving."; return; }
        try
        {
            CommitBridgeExpansion(); CommitBridgeLookup(); CommitBridgeRule(); await DatabaseSyncTranslationService.WriteAsync(_recoveryTranslation.Text, _loadedBridge, overwrite: true);
            _bridgeUiChanging = true; _bridgeTable.ItemsSource = _loadedBridge.Tables.Select((rule, index) => new BridgeTableChoice(index, $"{rule.SourceTable} → {(string.IsNullOrWhiteSpace(rule.TargetTable) ? "UNRESOLVED" : rule.TargetTable)}")).ToArray(); _bridgeTable.SelectedIndex = _bridgeRuleIndex; _bridgeUiChanging = false;
            RefreshBridgeStatus("Saved atomically. ");
        }
        catch (Exception exception) { _bridgeStatus.Text = $"Bridge save failed safely: {exception.Message}"; DesktopCrashLogger.Log("Database schema bridge save failed", exception); }
    }

    private void RenderBridgeRule()
    {
        const string unresolved = "⟨unresolved — blocks planning⟩";
        if (_loadedBridge is null || _bridgeCapabilities is null || _bridgeRuleIndex < 0 || _bridgeRuleIndex >= _loadedBridge.Tables.Count) { _bridgeColumns.ItemsSource = null; _bridgeDefaults.ItemsSource = null; RefreshBridgeStatus(); return; }
        var rule = _loadedBridge.Tables[_bridgeRuleIndex]; var target = _bridgeCapabilities.FindTable(rule.TargetTable);
        _bridgeUiChanging = true; _bridgeTargetTable.ItemsSource = new[] { unresolved }.Concat(_bridgeCapabilities.Tables.Keys.Order(StringComparer.OrdinalIgnoreCase)).ToArray(); _bridgeTargetTable.SelectedItem = target?.Name ?? unresolved; _bridgeSuppressPrimary.IsChecked = rule.SuppressPrimaryOutput; _bridgeUiChanging = false;
        var mappings = rule.ColumnMappings.ToDictionary(mapping => mapping.SourceColumn, StringComparer.OrdinalIgnoreCase); var drops = rule.DroppedSourceColumns.ToHashSet(StringComparer.OrdinalIgnoreCase); var keys = (rule.SourcePrimaryKeyColumns ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observed = rule.ObservedSourceColumns ?? rule.ColumnMappings.Select(mapping => mapping.SourceColumn).Concat(rule.DroppedSourceColumns).Concat(rule.SourcePrimaryKeyColumns ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        _bridgeColumnRows = observed.Select(source => new BridgeColumnChoice { Source = source, IsKey = keys.Contains(source), Drop = drops.Contains(source), Target = mappings.TryGetValue(source, out var mapping) ? mapping.TargetColumn : target?.Find(source)?.Name ?? string.Empty }).ToList();
        var defaults = rule.TargetDefaults.ToDictionary(value => value.TargetColumn, StringComparer.OrdinalIgnoreCase); var required = new HashSet<string>(defaults.Keys, StringComparer.OrdinalIgnoreCase);
        if (rule.RequiresInsertDefaults && target is not null)
        {
            var mapped = _bridgeColumnRows.Where(row => !row.Drop && row.Target.Length > 0).Select(row => row.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var column in target.Columns.Where(column => !mapped.Contains(column.Name) && !column.Nullable && column.DefaultValue is null && !column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) && !column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase))) required.Add(column.Name);
        }
        _bridgeDefaultRows = required.Order(StringComparer.OrdinalIgnoreCase).Select(column =>
        {
            var value = defaults.GetValueOrDefault(column)?.Value ?? LegacyDatabaseAuditValue.Unknown;
            return new BridgeDefaultChoice { Target = column, State = value.State, Value = value.Value ?? string.Empty };
        }).ToList();
        _bridgeColumns.ItemsSource = _bridgeColumnRows; _bridgeDefaults.ItemsSource = _bridgeDefaultRows; _bridgeExpansionIndex = (rule.Expansions?.Count ?? 0) == 0 ? -1 : Math.Clamp(_bridgeExpansionIndex, 0, rule.Expansions!.Count - 1); _bridgeLookupIndex = (rule.Lookups?.Count ?? 0) == 0 ? -1 : Math.Clamp(_bridgeLookupIndex, 0, rule.Lookups!.Count - 1); RenderBridgeExpansionList(); RenderBridgeLookupList(); RefreshBridgeStatus();
    }

    private void RebuildBridgeDefaultRows()
    {
        if (_loadedBridge is null || _bridgeCapabilities is null || _bridgeRuleIndex < 0 || _bridgeRuleIndex >= _loadedBridge.Tables.Count) return;
        var rule = _loadedBridge.Tables[_bridgeRuleIndex]; var target = _bridgeCapabilities.FindTable(rule.TargetTable); if (target is null) { _bridgeDefaultRows = []; _bridgeDefaults.ItemsSource = _bridgeDefaultRows; return; }
        var previous = _bridgeDefaultRows.ToDictionary(row => row.Target, StringComparer.OrdinalIgnoreCase); var mapped = _bridgeColumnRows.Where(row => !row.Drop && row.Target.Length > 0).Select(row => row.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = rule.RequiresInsertDefaults
            ? target.Columns.Where(column => !mapped.Contains(column.Name) && !column.Nullable && column.DefaultValue is null && !column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) && !column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : rule.TargetDefaults.Select(value => value.TargetColumn).Where(column => !mapped.Contains(column)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        _bridgeDefaultRows = required.Select(column => previous.TryGetValue(column, out var row) ? row : new BridgeDefaultChoice { Target = column }).ToList(); _bridgeDefaults.ItemsSource = _bridgeDefaultRows;
    }

    private void CommitBridgeRule()
    {
        if (_loadedBridge is null || _bridgeRuleIndex < 0 || _bridgeRuleIndex >= _loadedBridge.Tables.Count) return;
        var rules = _loadedBridge.Tables.ToArray(); var rule = rules[_bridgeRuleIndex];
        var mappings = _bridgeColumnRows.Where(row => !row.Drop && (row.Target.Length == 0 || !row.Target.Equals(row.Source, StringComparison.OrdinalIgnoreCase))).Select(row => new DatabaseSyncColumnTranslation(row.Source, row.Target)).ToArray();
        var drops = _bridgeColumnRows.Where(row => row.Drop).Select(row => row.Source).ToArray();
        var defaults = _bridgeDefaultRows.Select(row => new DatabaseSyncTargetDefault(row.Target, row.State switch
        {
            LegacyDatabaseAuditValueState.Null => LegacyDatabaseAuditValue.Null,
            LegacyDatabaseAuditValueState.Scalar => new(LegacyDatabaseAuditValueState.Scalar, row.Value),
            LegacyDatabaseAuditValueState.Binary => new(LegacyDatabaseAuditValueState.Binary, row.Value),
            _ => LegacyDatabaseAuditValue.Unknown
        })).ToArray();
        rules[_bridgeRuleIndex] = rule with { ColumnMappings = mappings, DroppedSourceColumns = drops, TargetDefaults = defaults }; _loadedBridge = _loadedBridge with { Tables = rules };
    }

    private string CurrentBridgeTarget() => _loadedBridge is not null && _bridgeRuleIndex >= 0 && _bridgeRuleIndex < _loadedBridge.Tables.Count ? _loadedBridge.Tables[_bridgeRuleIndex].TargetTable : string.Empty;

    private void RefreshBridgeStatus(string prefix = "")
    {
        if (_loadedBridge is null) { _bridgeStatus.Text = prefix + "Generate or load a schema bridge to edit it here."; return; }
        var tableGaps = _loadedBridge.Tables.Count(rule => !rule.SuppressPrimaryOutput && string.IsNullOrWhiteSpace(rule.TargetTable));
        var structuralGaps = _loadedBridge.Tables.Count(rule => rule.SuppressPrimaryOutput && (rule.Expansions?.Count ?? 0) == 0);
        var primarySuppressed = CurrentBridgeRule()?.SuppressPrimaryOutput == true;
        var currentColumnGaps = primarySuppressed ? 0 : _bridgeColumnRows.Count(row => !row.Drop && string.IsNullOrWhiteSpace(row.Target)); var currentDefaultGaps = primarySuppressed ? 0 : _bridgeDefaultRows.Count(row => row.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing);
        var expansionCount = _loadedBridge.Tables.Sum(rule => rule.Expansions?.Count ?? 0); var lookupCount = _loadedBridge.Tables.Sum(rule => rule.Lookups?.Count ?? 0);
        _bridgeStatus.Text = prefix + $"{_loadedBridge.Name} · {_loadedBridge.Tables.Count:N0} table rule(s) · {expansionCount:N0} structural output(s) · {lookupCount:N0} named lookup(s) · {tableGaps:N0} unresolved primary table(s) · {structuralGaps:N0} suppressed table(s) without an output. Current table: {currentColumnGaps:N0} unresolved column(s), {currentDefaultGaps:N0} unresolved default(s). Unknown/blank values block planning.";
    }

    private async Task LoadDatabaseSyncPlanAsync()
    {
        var path = _recoveryPlan.Text; if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { _legacyStatus.Text = "Choose an existing synchronization plan first."; return; }
        BeginOperation("Verifying synchronization plan hashes…");
        try { ShowDatabaseSyncPlan(await new DatabaseSynchronizationService().LoadPlanAsync(path, _operation!.Token)); }
        catch (Exception exception) { _loadedRecoveryPlan = null; _recoveryOperations.ItemsSource = null; _legacyStatus.Text = $"Plan verification failed: {exception.Message}"; DesktopCrashLogger.Log("Database synchronization plan verification failed", exception); }
        finally { EndOperation(); }
    }

    private void ShowDatabaseSyncPlan(DatabaseSyncPlan plan)
    {
        _loadedRecoveryPlan = plan; _recoveryOperations.ItemsSource = plan.Operations;
        var dependencyInclusions = plan.DependencyInclusions ?? [];
        var schemaTranslations = plan.SchemaTranslations ?? [];
        var lookupEvidence = plan.LookupEvidence ?? [];
        var dependencyLines = dependencyInclusions.Take(500).Select(inclusion => $"{inclusion.IncludedIdentity} ← {inclusion.Relation} ({inclusion.SelectedEndpoint} = {inclusion.IncludedEndpoint} = {inclusion.MatchedValue}) from {inclusion.SelectedIdentity}").ToList();
        if (dependencyInclusions.Count > dependencyLines.Count) dependencyLines.Add($"… {dependencyInclusions.Count - dependencyLines.Count:N0} more causal edges remain in the hash-bound plan artifact and CLI inspection output.");
        var translationLines = schemaTranslations.Take(500).Select(item => $"{item.Action}: {item.SourceTable}.{item.SourceColumn} → {item.TargetTable}.{item.TargetColumn} · {item.Operations:N0} operation(s)").ToList();
        if (schemaTranslations.Count > translationLines.Count) translationLines.Add($"… {schemaTranslations.Count - translationLines.Count:N0} more translation rules remain in the hash-bound plan artifact and CLI inspection output.");
        var lookupLines = lookupEvidence.Take(500).Select(item => $"{item.SourceDisplayIdentity} · {item.LookupName} · {item.Source} {item.LookupTable} ({string.Join(", ", item.Match.Select(part => $"{part.Column}={part.Value.Value}"))}) → {item.ResultColumn}={item.Result.Value} · matched {item.MatchedIdentity}").ToList();
        if (lookupEvidence.Count > lookupLines.Count) lookupLines.Add($"… {lookupEvidence.Count - lookupLines.Count:N0} more exact lookup results remain in the hash-bound plan artifact and CLI inspection output.");
        _recoveryRemapSummary.IsVisible = plan.IdRemaps.Count > 0 || dependencyInclusions.Count > 0 || schemaTranslations.Count > 0 || lookupEvidence.Count > 0;
        _recoveryRemapSummary.Text = string.Join(Environment.NewLine + Environment.NewLine, new[]
        {
            plan.IdRemaps.Count == 0 ? string.Empty : "ID remaps — review before apply:\n" + string.Join(Environment.NewLine, plan.IdRemaps.Select(remap => $"{remap.Table}.{remap.Column}: {remap.SourceId} → {remap.TargetId} · {remap.RewrittenReferences:N0} recognized reference(s) rewritten")),
            dependencyInclusions.Count == 0 ? string.Empty : "Dependency additions — exact causal edges:\n" + string.Join(Environment.NewLine, dependencyLines),
            schemaTranslations.Count == 0 ? string.Empty : $"Schema bridge — {plan.TranslationProfileName} · {plan.TranslationProfileSha256}:\n" + string.Join(Environment.NewLine, translationLines),
            lookupEvidence.Count == 0 ? string.Empty : "Exact named row lookups — rechecked under locks before apply:\n" + string.Join(Environment.NewLine, lookupLines)
        }.Where(section => section.Length > 0));
        _legacyStatus.Text = $"Verified target plan for {plan.Target.User}@{plan.Target.Host}:{plan.Target.Port}/{plan.Target.Database}.\n{plan.Ready:N0} ready · {plan.AlreadyApplied:N0} already applied · {plan.Conflicts:N0} conflicts · {plan.Blocked:N0} blocked · {plan.IdRemaps.Count:N0} ID remaps · {(plan.DependencyInclusions?.Count ?? 0):N0} dependency additions · {schemaTranslations.Count:N0} translation rules · {lookupEvidence.Count:N0} exact lookups · {plan.Operations.Count:N0} total.\nTarget schema: {plan.TargetSchemaSha256 ?? "legacy plan without schema binding"}. {(plan.RemovalsIncluded ? "Explicit removals are included." : "Removals are excluded.")}";
    }

    private async Task ExportDatabaseSyncPreviewAsync()
    {
        if (_loadedRecoveryPlan is null) { await LoadDatabaseSyncPlanAsync(); if (_loadedRecoveryPlan is null) return; }
        var destination = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export non-committing synchronization SQL preview", SuggestedFileName = "database-sync-preview.sql", FileTypeChoices = [new FilePickerFileType("SQL preview") { Patterns = ["*.sql"] }] });
        var path = destination?.TryGetLocalPath(); if (path is null) return; if (File.Exists(path)) { _legacyStatus.Text = "That SQL preview already exists. Choose a new path so it is not silently replaced."; return; }
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { await File.WriteAllTextAsync(temporary, new DatabaseSynchronizationService().PreviewSql(_loadedRecoveryPlan)); File.Move(temporary, path); _legacyStatus.Text = $"Exported a non-committing, stale-safe SQL review to {path}. Apply through Crucible to retain row locks and a rollback receipt."; }
        catch (Exception exception) { _legacyStatus.Text = $"SQL preview export failed: {exception.Message}"; DesktopCrashLogger.Log("Database synchronization SQL preview failed", exception); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task PickDatabaseSyncReceiptOutputAsync()
    {
        var destination = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Choose a new database synchronization receipt", SuggestedFileName = "database-sync-receipt.json", FileTypeChoices = [new FilePickerFileType("Crucible database synchronization receipt") { Patterns = ["*.json"] }] });
        var path = destination?.TryGetLocalPath(); if (path is not null) _recoveryReceipt.Text = path;
    }

    private async Task ReviewDatabaseSyncApplyAsync()
    {
        if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _legacyStatus.Text = "Connect and verify the exact target database first."; return; }
        if (_loadedRecoveryPlan is null) { await LoadDatabaseSyncPlanAsync(); if (_loadedRecoveryPlan is null) return; }
        if (_loadedRecoveryPlan.Conflicts != 0 || _loadedRecoveryPlan.Blocked != 0) { _legacyStatus.Text = $"Apply is blocked by {_loadedRecoveryPlan.Conflicts:N0} conflict(s) and {_loadedRecoveryPlan.Blocked:N0} unsupported operation(s). Narrow or repair the plan first."; return; }
        if (string.IsNullOrWhiteSpace(_recoveryReceipt.Text)) await PickDatabaseSyncReceiptOutputAsync(); var receipt = _recoveryReceipt.Text;
        if (string.IsNullOrWhiteSpace(receipt)) return; if (File.Exists(receipt)) { _legacyStatus.Text = "The receipt output already exists. Choose a new path so rollback evidence is never overwritten."; return; }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _recoveryConfirmation.IsVisible = false; var confirm = AccentButton("Commit exact synchronized rows");
        confirm.Click += async (_, _) => await ApplyDatabaseSyncAsync(confirm, receipt);
        _recoveryConfirmation.Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = $"Apply {_loadedRecoveryPlan.Ready:N0} ready operation(s) to {_loadedRecoveryPlan.Target.Database}? Crucible will lock and recheck every primary-keyed preimage, abort the complete transaction on any stale row, and publish the exact rollback receipt only after commit.", TextWrapping = TextWrapping.Wrap }, new WrapPanel { Children = { cancel, confirm } } } }; _recoveryConfirmation.IsVisible = true;
    }

    private async Task ApplyDatabaseSyncAsync(Button button, string receipt)
    {
        if (_session.DatabaseProfile is null || string.IsNullOrWhiteSpace(_recoveryPlan.Text)) return; BeginOperation("Locking, revalidating, and applying synchronized rows…");
        try { button.IsEnabled = false; var result = await new DatabaseSynchronizationService().ApplyAsync(_recoveryPlan.Text, _session.DatabaseProfile, receipt, cancellationToken: _operation!.Token); _recoveryConfirmation.IsVisible = false; _legacyStatus.Text = $"Committed {result.Applied:N0} exact row operation(s); {result.AlreadyApplied:N0} were already applied.\nRollback receipt: {result.ReceiptPath}"; }
        catch (Exception exception) { _legacyStatus.Text = $"Synchronization apply failed without a partial commit: {exception.Message}"; DesktopCrashLogger.Log("Database synchronization apply failed", exception); }
        finally { button.IsEnabled = true; EndOperation(); }
    }

    private void ReviewDatabaseSyncRollback()
    {
        if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _legacyStatus.Text = "Connect and verify the receipt's exact target database first."; return; }
        var receipt = _recoveryReceipt.Text; if (string.IsNullOrWhiteSpace(receipt) || !File.Exists(receipt)) { _legacyStatus.Text = "Choose an existing synchronization receipt first."; return; }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _recoveryConfirmation.IsVisible = false; var confirm = new Button { Content = "Rollback exact synchronized rows" }; confirm.Click += async (_, _) => await RollbackDatabaseSyncAsync(confirm, receipt);
        _recoveryConfirmation.Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Rollback this receipt? Crucible reverses operations in dependency-safe order, locks and verifies every current postimage, and aborts the complete rollback if any synchronized row changed afterward.", TextWrapping = TextWrapping.Wrap }, new WrapPanel { Children = { cancel, confirm } } } }; _recoveryConfirmation.IsVisible = true;
    }

    private async Task RollbackDatabaseSyncAsync(Button button, string receipt)
    {
        if (_session.DatabaseProfile is null) return; BeginOperation("Revalidating postimages and rolling back synchronized rows…");
        try { button.IsEnabled = false; var result = await new DatabaseSynchronizationService().RollbackAsync(receipt, _session.DatabaseProfile, _operation!.Token); _recoveryConfirmation.IsVisible = false; _legacyStatus.Text = $"Rolled back {result.Applied:N0} exact row operation(s); {result.AlreadyApplied:N0} were already restored. The receipt is marked rolled back."; }
        catch (Exception exception) { _legacyStatus.Text = $"Synchronization rollback failed without a partial commit: {exception.Message}"; DesktopCrashLogger.Log("Database synchronization rollback failed", exception); }
        finally { button.IsEnabled = true; EndOperation(); }
    }

    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The server workspace is not attached to the main window.");
    private static string[] Patterns(string? text) => (text ?? string.Empty).Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void BeginOperation(string text)
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _detect.IsEnabled = _test.IsEnabled = false; foreach (var button in _lifecycleButtons) button.IsEnabled = false; _status.Text = text;
    }

    private void EndOperation() { _detect.IsEnabled = _test.IsEnabled = true; foreach (var button in _lifecycleButtons) button.IsEnabled = true; }

    private void SessionChanged(object? sender, EventArgs e) => PopulateFromSession();

    private void PopulateFromSession()
    {
        _serverRoot.Text = _session.Server?.RootPath ?? _session.Settings.ServerRootPath;
        var profile = _session.SuggestedProfile(_password.Text ?? string.Empty); _host.Text = profile.Host; _port.Value = profile.Port; _user.Text = profile.User; _database.Text = profile.Database;
        if (_session.DatabaseProfile is { } detected && !string.IsNullOrEmpty(detected.Password)) _password.Text = detected.Password;
        if (_session.Server is not { } server)
        {
            _summary.Text = _session.LastError is null ? "No server workspace has been detected in this session." : $"Last failure\n\n{_session.LastError}";
            return;
        }
        var capabilities = _session.DatabaseCapabilities;
        _summary.Text = $"Core family\t{server.CoreFamily}\nServer root\t{server.RootPath}\nConfiguration\t{server.ConfigLocation}\nLayout\t{(server.UsesWsl ? $"Windows + WSL ({server.WslDistribution})" : "Native/local")}\nServer DBCs\t{(string.IsNullOrWhiteSpace(server.DbcPath) ? "Not found" : server.DbcPath)}\nWorld database\t{server.WorldDatabase.Database} on {server.WorldDatabase.Host}:{server.WorldDatabase.Port}\nDatabase user\t{server.WorldDatabase.User}\nConnection\t{(capabilities is null ? "Not tested" : $"Verified · MySQL {capabilities.ServerVersion}")}\nTransport\t{_session.DatabaseTransportDescription}\nRecognized tables\t{capabilities?.Tables.Count ?? 0:N0}\nDBC overlay tables\t{capabilities?.DbcOverlayTables.Count ?? 0:N0}";
    }

    public void Dispose() { _session.Changed -= SessionChanged; _operation?.Cancel(); _operation?.Dispose(); _lifecycle.Dispose(); }

    private static Grid Form(params (string Label, Control Input)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", rows.Length))), RowSpacing = 7, ColumnSpacing = 10 };
        for (var index = 0; index < rows.Length; index++) { var label = new TextBlock { Text = rows[index].Label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(label, index); grid.Children.Add(label); Grid.SetRow(rows[index].Input, index); Grid.SetColumn(rows[index].Input, 1); grid.Children.Add(rows[index].Input); }
        return grid;
    }

    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
