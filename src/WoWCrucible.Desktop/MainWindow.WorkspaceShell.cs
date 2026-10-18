using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

public partial class MainWindow
{
    private sealed record WorkspacePage(Control View, string Title, string? Command);
    private readonly WorkspaceTabState _workspaceTabs = new();
    private readonly Dictionary<string, WorkspacePage> _workspacePages = [];
    private int _nextWorkspacePageId;
    private string? _openingCommand;
    private bool _focusEditor;
    private readonly HashSet<ResponsiveSplitGrid> _configuredSplits = [];
    private readonly MenuItem _editorPaneLayoutMenu = new() { Header = "Editor panes", IsEnabled = false };
    private MenuItem? _inspectorMenu, _toolbarMenu, _statusMenu;
    private readonly HashSet<Control> _hiddenWorkspaceToolbars = [];
    private readonly List<(MenuItem Item, string Label, Func<bool> Visible)> _chromeMenus = [];

    private void InitializeWorkspaceShell()
    {
        ToolBrowser.CommandRequested += async (_, id) => await ExecuteRuntimeCommandAsync(id);
        StartPage.CommandRequested += async (_, id) => await ExecuteRuntimeCommandAsync(id);
        WorkspaceTabs.Selected += (_, id) => SelectWorkspaceTab(id);
        WorkspaceTabs.Closed += (_, id) => CloseWorkspaceTab(id);
        WorkspaceTabs.Moved += (_, move) => { _workspaceTabs.Move(move.Id, move.Delta); WorkspaceTabs.Refresh(_workspaceTabs); };
        NavigationSplitter.AddHandler(PointerReleasedEvent, (_, _) => SaveShellPaneSizes(), RoutingStrategies.Tunnel);
        InspectorSplitter.AddHandler(PointerReleasedEvent, (_, _) => SaveShellPaneSizes(), RoutingStrategies.Tunnel);
        NavigationSplitter.KeyUp += (_, _) => SaveShellPaneSizes();
        InspectorSplitter.KeyUp += (_, _) => SaveShellPaneSizes();
        var layout = new MenuFlyout();
        Add("Show / hide tool browser", () => ToggleNavigationPaneClick(null, new RoutedEventArgs()));
        _inspectorMenu = Add("Show / hide selected table record", () => ToggleInspectorPaneClick(null, new RoutedEventArgs()));
        Add("Move tool browser to left", () => DockBrowser(false));
        Add("Move tool browser to right", () => DockBrowser(true));
        Add("Editor only / restore controls (F6)", ToggleEditorFocus);
        var settings = _workspaceSession.Settings.EditorLayout;
        Chrome("application menu", () => settings.MenuVisible, value => settings.MenuVisible = value);
        Chrome("server status bar", () => settings.RuntimeVisible, value => settings.RuntimeVisible = value);
        Chrome("workspace tabs", () => settings.TabsVisible, value => settings.TabsVisible = value);
        _toolbarMenu = Chrome("editor toolbars", () => settings.ToolbarsVisible, value => settings.ToolbarsVisible = value);
        _statusMenu = Chrome("bottom status bar", () => settings.StatusVisible, value => settings.StatusVisible = value);
        layout.Items.Add(_editorPaneLayoutMenu);
        Add("Restore default layout", ResetEditorLayout);
        LayoutButton.Flyout = layout;
        WorkspaceTabs.Refresh(_workspaceTabs);

        MenuItem Add(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            layout.Items.Add(item);
            return item;
        }
        MenuItem Chrome(string label, Func<bool> visible, Action<bool> set)
        {
            var item = Add("Hide " + label, () => { set(!visible()); ApplyShellPaneState(); _workspaceSession.Settings.Save(); });
            _chromeMenus.Add((item, label, visible));
            return item;
        }
    }

    private void DockBrowser(bool right)
    {
        SaveShellPaneSizes();
        _workspaceSession.Settings.EditorLayout.BrowserOnRight = right;
        ApplyShellPaneState();
        _workspaceSession.Settings.Save();
    }

    private void ToggleEditorFocus()
    {
        _focusEditor = !_focusEditor;
        ApplyShellPaneState();
    }

    private void RestoreChromeClick(object? sender, RoutedEventArgs e)
    {
        _focusEditor = false;
        _workspaceSession.Settings.EditorLayout.TabsVisible = true;
        ApplyShellPaneState();
        _workspaceSession.Settings.Save();
    }

    private void ResetEditorLayout()
    {
        _focusEditor = false;
        var layout = _workspaceSession.Settings.EditorLayout;
        layout.BrowserVisible = true;
        layout.MenuVisible = layout.RuntimeVisible = layout.TabsVisible = layout.ToolbarsVisible = layout.StatusVisible = true;
        layout.BrowserOnRight = false;
        layout.BrowserWidth = 260;
        layout.InspectorWidth = 340;
        _workspaceSession.Settings.DbcRowEditorOpen = true;
        foreach (var split in _configuredSplits) split.ResetLayout();
        layout.Splits.Clear();
        ApplyShellPaneState();
        _workspaceSession.Settings.Save();
    }

    private void SaveShellPaneSizes()
    {
        var layout = _workspaceSession.Settings.EditorLayout;
        if (NavigationPane.IsVisible) layout.BrowserWidth = NavigationPane.Bounds.Width;
        if (InspectorPane.IsVisible) layout.InspectorWidth = InspectorPane.Bounds.Width;
        _workspaceSession.Settings.Save();
    }

    private void ConfigureSplitLayouts(Control workspace, string title)
    {
        var index = 0;
        var splits = WalkWorkspaceControls(workspace, includeItems: true).Select(entry => entry.Control).OfType<ResponsiveSplitGrid>().ToArray();
        _editorPaneLayoutMenu.Items.Clear();
        _editorPaneLayoutMenu.IsEnabled = splits.Length > 0;
        _editorPaneLayoutMenu.IsVisible = splits.Length > 0;
        foreach (var split in splits)
        {
            if (splits.Length == 1)
                foreach (var item in split.CreateLayoutMenuItems()) _editorPaneLayoutMenu.Items.Add(item);
            else
                _editorPaneLayoutMenu.Items.Add(new MenuItem { Header = index == 0 ? "Main panes" : $"Nested panes {index}", ItemsSource = split.CreateLayoutMenuItems() });
            var key = $"{workspace.GetType().Name}/{title}/{index++}";
            if (!_configuredSplits.Add(split)) continue;
            var settings = _workspaceSession.Settings;
            split.RestoreLayout(settings.EditorLayout.Splits.GetValueOrDefault(key));
            split.LayoutChanged += (_, _) =>
            {
                settings.EditorLayout.Splits[key] = split.CaptureLayout();
                settings.Save();
            };
        }
    }

    private void SelectWorkspaceTab(string id)
    {
        if (!CommitPendingDbcEdits()) return;
        _workspaceTabs.Select(id);
        ShowSelectedWorkspace();
    }

    private void CloseWorkspaceTab(string id)
    {
        if (!CommitPendingDbcEdits()) return;
        _workspaceTabs.Close(id);
        ShowSelectedWorkspace();
    }

    private void RemoveFeatureWorkspace(Control workspace)
    {
        var id = _workspacePages.FirstOrDefault(pair => ReferenceEquals(pair.Value.View, workspace)).Key;
        if (id is null) return;
        _workspaceTabs.Close(id);
        _workspacePages.Remove(id);
        foreach (var entry in WalkWorkspaceControls(workspace, includeItems: true)) _hiddenWorkspaceToolbars.Remove(entry.Control);
        foreach (var split in WalkWorkspaceControls(workspace, includeItems: true).Select(entry => entry.Control).OfType<ResponsiveSplitGrid>())
            _configuredSplits.Remove(split);
        if (ReferenceEquals(FeatureWorkspaceHost.Child, workspace))
        {
            FeatureWorkspaceHost.Child = null;
            FeatureToolbarHost.Child = null;
            _editorPaneLayoutMenu.Items.Clear();
        }
    }

    private void ShowSelectedWorkspace()
    {
        var id = _workspaceTabs.SelectedId;
        if (id == "home") { ShowHome(); return; }
        if (id == "tables") { OpenEditorWorkspaceClick(null, new RoutedEventArgs()); return; }
        var page = _workspacePages[id];
        if (FeatureWorkspaceHost.Child is AssetComparisonView previous && !ReferenceEquals(previous, page.View)) previous.Suspend();
        FeatureWorkspaceHost.Child = page.View;
        if (page.View is AssetComparisonView assets) assets.Resume();
        FeatureWorkspaceHost.IsVisible = true;
        MainHeader.IsVisible = EditorWorkspace.IsVisible = MainStatusBar.IsVisible = false;
        _featureTitle = page.Title;
        FeatureToolbarHost.Child = (page.View as IFeatureWorkspaceToolbar)?.FeatureToolbar;
        FeatureToolbarHost.IsVisible = FeatureToolbarHost.Child is not null;
        SuppressLegacyFeatureNavigation(page.View);
        ConfigureSplitLayouts(page.View, page.Title);
        ApplyShellPaneState();
        ToolBrowser.SelectCommand(page.Command);
        WorkspaceTabs.Refresh(_workspaceTabs);
        Title = $"WoW Crucible — {page.Title}";
    }
}
