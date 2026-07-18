using Avalonia;
using Avalonia.Controls;
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
        return new ScrollViewer
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
                    new Border { Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Color.Parse("#293347")), BorderThickness = new Thickness(1), Child = _legacyStatus }
                }
            }
        };
    }

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
            _legacyStatus.Text = $"Created {(result.Manifest.Mode == LegacyDatabaseAuditMode.BaselineCompared ? "baseline-attributed" : "unattributed")} audit with {result.Manifest.TotalChangeRecords:N0} change records and {result.Manifest.TotalChangedFields:N0} changed fields.\nArtifact: {result.Path}\nPromotion remains review-only; no target database was modified.";
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
}
