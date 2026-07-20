using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MySqlConnector;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ServerSqlWorkspaceView : UserControl, IDisposable
{
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
    private readonly TextBox _recoveryPlan = new() { PlaceholderText = "Target-bound synchronization plan" };
    private readonly TextBox _recoveryReceipt = new() { PlaceholderText = "Apply receipt for exact rollback" };
    private readonly CheckBox _recoveryRemovals = new() { Content = "Include explicitly reviewed removals" };
    private readonly CheckBox _recoveryAutoRemap = new() { Content = "Remap occupied added IDs" };
    private readonly CheckBox _recoveryDependencyClosure = new() { Content = "Include exact changed-row dependency closure", IsChecked = true };
    private readonly TextBox _recoveryRemapStart = new() { PlaceholderText = "Optional first remapped ID" };
    private readonly TextBlock _recoveryRemapSummary = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D5B56A")), IsVisible = false };
    private readonly ListBox _recoveryOperations = new();
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
                    new TextBlock { Text = "A plan requires a verified matching-core baseline audit. Crucible compares every selected primary-keyed row with the live target, excludes removals by default, and binds the result to this exact host/user/database. Dependency closure follows only exact declared or named core relationship values through other changed audit rows; every causal inclusion remains visible. Occupied added IDs remain conflicts unless remapping is explicitly enabled.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#9AA5B7")) },
                    auditPath,
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
        return new ResponsiveSplitGrid(configuration, new Border { BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = _recoveryOperations }, 3, 2, 1.7);
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
            var result = await new DatabaseSynchronizationService().BuildPlanAsync(audit, _session.DatabaseProfile, output, new(Patterns(_legacyIncludes.Text), _recoveryRemovals.IsChecked == true, AutoRemapCollisions: _recoveryAutoRemap.IsChecked == true, RemapStart: remapStart, IncludeDependencyClosure: _recoveryDependencyClosure.IsChecked == true), progress, _operation!.Token);
            _recoveryPlan.Text = result.Path; ShowDatabaseSyncPlan(result.Plan);
        }
        catch (OperationCanceledException) { _legacyStatus.Text = "Target comparison cancelled; no partial plan was published."; }
        catch (Exception exception) { _legacyStatus.Text = $"Target comparison failed: {exception.Message}"; DesktopCrashLogger.Log("Database synchronization plan failed", exception); }
        finally { EndOperation(); }
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
        var dependencyLines = dependencyInclusions.Take(500).Select(inclusion => $"{inclusion.IncludedIdentity} ← {inclusion.Relation} ({inclusion.SelectedEndpoint} = {inclusion.IncludedEndpoint} = {inclusion.MatchedValue}) from {inclusion.SelectedIdentity}").ToList();
        if (dependencyInclusions.Count > dependencyLines.Count) dependencyLines.Add($"… {dependencyInclusions.Count - dependencyLines.Count:N0} more causal edges remain in the hash-bound plan artifact and CLI inspection output.");
        _recoveryRemapSummary.IsVisible = plan.IdRemaps.Count > 0 || dependencyInclusions.Count > 0;
        _recoveryRemapSummary.Text = string.Join(Environment.NewLine + Environment.NewLine, new[]
        {
            plan.IdRemaps.Count == 0 ? string.Empty : "ID remaps — review before apply:\n" + string.Join(Environment.NewLine, plan.IdRemaps.Select(remap => $"{remap.Table}.{remap.Column}: {remap.SourceId} → {remap.TargetId} · {remap.RewrittenReferences:N0} recognized reference(s) rewritten")),
            dependencyInclusions.Count == 0 ? string.Empty : "Dependency additions — exact causal edges:\n" + string.Join(Environment.NewLine, dependencyLines)
        }.Where(section => section.Length > 0));
        _legacyStatus.Text = $"Verified target plan for {plan.Target.User}@{plan.Target.Host}:{plan.Target.Port}/{plan.Target.Database}.\n{plan.Ready:N0} ready · {plan.AlreadyApplied:N0} already applied · {plan.Conflicts:N0} conflicts · {plan.Blocked:N0} blocked · {plan.IdRemaps.Count:N0} ID remaps · {(plan.DependencyInclusions?.Count ?? 0):N0} dependency additions · {plan.Operations.Count:N0} total.\n{(plan.RemovalsIncluded ? "Explicit removals are included." : "Removals are excluded.")}";
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
        _summary.Text = $"Core family\t{server.CoreFamily}\nServer root\t{server.RootPath}\nConfiguration\t{server.ConfigLocation}\nLayout\t{(server.UsesWsl ? $"Windows + WSL ({server.WslDistribution})" : "Native/local")}\nServer DBCs\t{(string.IsNullOrWhiteSpace(server.DbcPath) ? "Not found" : server.DbcPath)}\nWorld database\t{server.WorldDatabase.Database} on {server.WorldDatabase.Host}:{server.WorldDatabase.Port}\nDatabase user\t{server.WorldDatabase.User}\nConnection\t{(capabilities is null ? "Not tested" : $"Verified · MySQL {capabilities.ServerVersion}")}\nRecognized tables\t{capabilities?.Tables.Count ?? 0:N0}\nDBC overlay tables\t{capabilities?.DbcOverlayTables.Count ?? 0:N0}";
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
