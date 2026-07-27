using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

internal sealed class WorkspaceRuntimeStrip : UserControl, IDisposable
{
    private readonly Button _workspace = new() { Content = "WORKSPACE · not configured", HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Button _featureBack = new() { Content = "←", IsVisible = false };
    private readonly TextBlock _featureTitle = new()
    {
        IsVisible = false,
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly ContentControl _featureToolbar = new() { IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _featureContext;
    private readonly TextBlock _sqlLight = Light();
    private readonly TextBlock _authLight = Light();
    private readonly TextBlock _worldLight = Light();
    private readonly Button _sql;
    private readonly Button _auth;
    private readonly Button _world;
    private readonly Button _refresh = new() { Content = "↻" };
    private readonly TextBlock _message = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#8995A9")), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DesktopWorkspaceSession? _session;
    private ServerRuntimeStatus? _status;
    private CancellationTokenSource? _operation;
    private bool _refreshing;

    public event EventHandler? WorkspaceRequested;
    public event EventHandler? FeatureBackRequested;
    public event EventHandler<string>? CommandRequested;

    public WorkspaceRuntimeStrip()
    {
        _sql = ComponentButton("SQL", _sqlLight);
        _auth = ComponentButton("AUTH", _authLight);
        _world = ComponentButton("WORLD", _worldLight);
        AutomationProperties.SetName(_workspace, "Open workspace setup");
        AutomationProperties.SetName(_sql, "Toggle SQL server");
        AutomationProperties.SetName(_auth, "Toggle authentication server");
        AutomationProperties.SetName(_world, "Toggle world server");
        AutomationProperties.SetName(_refresh, "Refresh server status");
        AutomationProperties.SetName(_featureBack, "Return to the previous Crucible workspace");
        _workspace.Click += (_, _) => WorkspaceRequested?.Invoke(this, EventArgs.Empty);
        _featureBack.Click += (_, _) => FeatureBackRequested?.Invoke(this, EventArgs.Empty);
        _sql.Click += async (_, _) => await ToggleAsync(Component.Sql);
        _auth.Click += async (_, _) => await ToggleAsync(Component.Auth);
        _world.Click += async (_, _) => await ToggleAsync(Component.World);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _timer.Tick += async (_, _) => await RefreshAsync();
        _featureContext = new Grid
        {
            ColumnDefinitions = new("Auto,Auto,*"),
            ColumnSpacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _featureBack, AtColumn(_featureTitle, 1), AtColumn(_featureToolbar, 2) }
        };
        var center = new Grid
        {
            ColumnDefinitions = new("*,Auto"),
            ColumnSpacing = 8,
            Children =
            {
                _featureContext,
                AtColumn(_message, 1)
            }
        };
        var runtime = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _sql, _auth, _world, _refresh }
        };
        var context = new Grid
        {
            ColumnDefinitions = new("Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 2),
            Children = { _workspace, AtColumn(center, 1), AtColumn(runtime, 2) }
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#090D13")),
            BorderBrush = new SolidColorBrush(Color.Parse("#273044")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                RowDefinitions = new("Auto,Auto"),
                Children =
                {
                    BuildApplicationMenu(),
                    AtRow(context, 1)
                }
            }
        };
    }

    private Menu BuildApplicationMenu()
    {
        return new Menu
        {
            VerticalAlignment = VerticalAlignment.Center,
            Items =
            {
                DomainMenu("CRUCIBLE",
                    ("Workspace setup", "workspace.setup"),
                    ("Projects & shared IDs", "workspace.projects"),
                    ("DBC tables", "workspace.dbc"),
                    ("All commands…", "ui.commands")),
                DomainMenu("CREATE",
                    ("Items & sets", "workspace.items"),
                    ("Cut / unobtainable items", "workspace.item-acquisition"),
                    ("Creatures & NPCs", "workspace.creatures"),
                    ("Quests", "workspace.quests"),
                    ("Gameobjects", "workspace.gameobjects"),
                    ("Pets & companions", "workspace.pets"),
                    ("Behaviors & dialogue", "workspace.behaviors")),
                DomainMenu("CLIENT",
                    ("DBC layers & promotion", "workspace.dbc-layers"),
                    ("Schemas & validation", "workspace.dbd"),
                    ("Client cache tables", "workspace.cache"),
                    ("MPQ patches & archives", "workspace.mpq"),
                    ("Merge MPQ patches", "workspace.mpq-merge"),
                    ("Client workshop", "workspace.client")),
                DomainMenu("SERVER",
                    ("Connection & controls", "workspace.server"),
                    ("SQL Studio", "workspace.sql"),
                    ("Saved SQL favorites", "workspace.sql-favorites")),
                DomainMenu("WORLD & ASSETS",
                    ("Assets, models & compare", "workspace.assets"),
                    ("Texture lab", "workspace.textures"),
                    ("Maps & world", "workspace.maps"),
                    ("Lighting & skyboxes", "workspace.lighting"),
                    ("Modern asset conversion", "workspace.conversion")),
                DomainMenu("HELP",
                    ("Field help & offline wiki", "workspace.knowledge"),
                    ("CLI guide", "workspace.cli-guide"),
                    ("Legacy-tool coverage", "workspace.tools"),
                    ("Logs", "action.logs"),
                    ("Toggle Devbug Mode", "action.devbug"))
            }
        };
    }

    private MenuItem DomainMenu(string header, params (string Label, string Command)[] entries)
    {
        var menu = new MenuItem { Header = header };
        foreach (var (label, command) in entries)
        {
            if (!command.Equals("ui.commands", StringComparison.Ordinal) &&
                !CrucibleCommandCatalog.All.Any(candidate => candidate.Id.Equals(command, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Application menu entry '{label}' references unknown command '{command}'.");
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => CommandRequested?.Invoke(this, command);
            menu.Items.Add(item);
        }
        return menu;
    }

    public void ShowFeature(string title, Control? toolbar)
    {
        _featureBack.IsVisible = true;
        _featureTitle.Text = title;
        _featureTitle.IsVisible = true;
        _featureToolbar.Content = toolbar;
        _featureToolbar.IsVisible = toolbar is not null;
        _message.IsVisible = false;
    }

    public void HideFeature()
    {
        _featureBack.IsVisible = false;
        _featureTitle.IsVisible = false;
        _featureTitle.Text = string.Empty;
        _featureToolbar.Content = null;
        _featureToolbar.IsVisible = false;
        _message.IsVisible = true;
    }

    public void Attach(DesktopWorkspaceSession session)
    {
        if (ReferenceEquals(_session, session)) return;
        if (_session is not null) _session.Changed -= SessionChanged;
        _session = session;
        _session.Changed += SessionChanged;
        ShowWorkspace();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task ToggleAsync(Component component)
    {
        if (_session?.Server is not { } server) { WorkspaceRequested?.Invoke(this, EventArgs.Empty); return; }
        if (_refreshing) return;
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        SetBusy(true);
        try
        {
            var current = _status ?? await _session.Lifecycle.GetStatusAsync(server, _operation.Token);
            var result = component switch
            {
                Component.Sql when current.DatabaseRunning => await _session.Lifecycle.StopDatabaseAsync(server, _operation.Token),
                Component.Sql => await _session.Lifecycle.StartDatabaseAsync(server, _operation.Token),
                Component.Auth when current.AuthServerRunning => await _session.Lifecycle.StopAuthAsync(server, _operation.Token),
                Component.Auth => await _session.Lifecycle.StartAuthAsync(server, _operation.Token),
                Component.World when current.WorldServerRunning => await _session.Lifecycle.StopWorldAsync(server, _operation.Token),
                _ => await _session.Lifecycle.StartWorldAsync(server, _operation.Token)
            };
            _status = result.Status; ShowStatus(result.Status);
            _message.Text = $"{result.Action} ready";
            _message.Foreground = new SolidColorBrush(Color.Parse("#54D68B"));
            ToolTip.SetTip(_message, result.Detail);
            DesktopCrashLogger.Debug("SERVER", "runtime-strip-action", ("action", result.Action), ("detail", result.Detail));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("Runtime strip operation failed", exception);
            ToolTip.SetTip(ComponentButton(component), exception.Message);
            _message.Text = $"{component.ToString().ToUpperInvariant()} failed · hover for details";
            _message.Foreground = new SolidColorBrush(Color.Parse("#E15A64"));
            ToolTip.SetTip(_message, exception.Message);
        }
        finally { SetBusy(false); await RefreshAsync(); }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _session?.Server is not { } server) { ShowWorkspace(); ShowStatus(null); return; }
        _refreshing = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            _status = await _session.Lifecycle.GetStatusAsync(server, timeout.Token);
            ShowStatus(_status);
            if (string.IsNullOrWhiteSpace(_message.Text)) _message.Text = "Click a component to start or stop it";
        }
        catch (Exception exception)
        {
            _status = null; ShowStatus(null);
            DesktopCrashLogger.Debug("SERVER", "runtime-strip-status-unavailable", ("error", exception.Message));
        }
        finally { _refreshing = false; }
    }

    private void SessionChanged(object? sender, EventArgs e)
    {
        ShowWorkspace();
        _ = RefreshAsync();
    }

    private void ShowWorkspace()
    {
        var name = _session?.Settings.WorkspaceName;
        _workspace.Content = string.IsNullOrWhiteSpace(name) ? "WORKSPACE · set up" : $"WORKSPACE · {name}";
        ToolTip.SetTip(_workspace, string.IsNullOrWhiteSpace(_session?.Settings.WorkspaceRootPath)
            ? "Choose one top-level WoW workspace folder and let Crucible remember the install."
            : _session.Settings.WorkspaceRootPath);
    }

    private void ShowStatus(ServerRuntimeStatus? status)
    {
        Paint(_sqlLight, status?.DatabaseRunning);
        Paint(_authLight, status?.AuthServerRunning);
        Paint(_worldLight, status?.WorldServerRunning);
        _sql.IsEnabled = _auth.IsEnabled = _world.IsEnabled = _session?.Server is not null;
        var detail = status?.Detail ?? "No configured/running server status is available.";
        ToolTip.SetTip(_sql, $"SQL · click to {(status?.DatabaseRunning == true ? "stop" : "start")}\n{detail}");
        ToolTip.SetTip(_auth, $"AUTH · click to {(status?.AuthServerRunning == true ? "stop" : "start")}\n{detail}");
        ToolTip.SetTip(_world, $"WORLD · click to {(status?.WorldServerRunning == true ? "gracefully stop" : "start")}\n{detail}");
    }

    private void SetBusy(bool busy) => _sql.IsEnabled = _auth.IsEnabled = _world.IsEnabled = _refresh.IsEnabled = !busy;
    private Button ComponentButton(Component component) => component switch { Component.Sql => _sql, Component.Auth => _auth, _ => _world };
    private static Button ComponentButton(string label, TextBlock light) => new()
    {
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { light, new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeight.SemiBold } } }
    };
    private static TextBlock Light() => new() { Text = "●", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
    private static T AtColumn<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }
    private static T AtRow<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
    private static void Paint(TextBlock light, bool? running) => light.Foreground = new SolidColorBrush(Color.Parse(running switch { true => "#54D68B", false => "#E15A64", null => "#677287" }));
    private enum Component { Sql, Auth, World }

    public void Dispose()
    {
        _timer.Stop();
        if (_session is not null) _session.Changed -= SessionChanged;
        _operation?.Cancel(); _operation?.Dispose();
    }
}
