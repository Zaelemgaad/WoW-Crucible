using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WoWCrucible.Core;
using WoWCrucible.Desktop;
using WoWCrucible.Desktop.Controls;

internal static class WorkspaceShellTestSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        TestNavigation();
        var grouped = WorkspaceNavigation.Groups.SelectMany(group => group.Commands).ToArray();
        var commands = CrucibleCommandCatalog.All.Where(command => command.Id.StartsWith("workspace.")).Select(command => command.Id).ToArray();
        Require(grouped.Distinct().Count() == grouped.Length && commands.Order().SequenceEqual(grouped.Order()), "Every workspace must appear once in the tool browser.");
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        TestSplitPanes();
        TestWindow();
        Console.WriteLine("PASS editor shell: complete navigation, tab selection/back/close/reopen/order, retained editors, pane layout round-trip and responsive placement.");
    }

    private static void TestNavigation()
    {
        var state = new WorkspaceTabState();
        state.Open("items", "Items"); state.Open("maps", "Maps"); state.Open("items", "Items");
        Require(state.Tabs.Count == 4, "Revisiting an editor duplicated its tab.");
        state.Back(); Require(state.SelectedId == "maps", "Back did not restore the previous open tab.");
        state.Close("maps"); Require(state.SelectedId == "home", "Closing the active tab did not select the remaining history.");
        state.Open("maps", "Maps"); state.Close("items"); state.Back();
        Require(state.SelectedId != "items", "Closed tab remained in Back history.");
        state.Open("items", "Items"); state.Move("items", -1);
        Require(state.Tabs[2].Id == "items", "Tab reordering failed.");
        state.Close("home"); state.Move("home", 1);
        Require(state.Tabs[0].Id == "home", "Pinned Home tab moved or closed.");
        for (var i = 0; i < 100; i++) { state.Select("items"); state.Select("maps"); }
        state.Close("items"); state.Close("maps"); state.Back();
        Require(state.Tabs.Any(tab => tab.Id == state.SelectedId), "Repeated navigation left an invalid selection.");
    }

    private static void TestSplitPanes()
    {
        var first = new TextBox { Text = "Unsaved content" };
        var second = new TextBox { Text = "Preview state" };
        var split = new ResponsiveSplitGrid(first, second, 3, 2);
        Arrange(split, 1200, 400);
        split.ColumnDefinitions[0].Width = new GridLength(7, GridUnitType.Star);
        split.ColumnDefinitions[2].Width = new GridLength(3, GridUnitType.Star);
        Arrange(split, 1200, 400);
        var saved = split.CaptureLayout();
        Require(Math.Abs(saved.WideFraction - .7) < .001, "Resized split was not captured.");
        Arrange(split, 400, 900);
        Require(Grid.GetRow(second) == 2, "Narrow layout failed to stack panes.");
        split.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        split.RowDefinitions[2].Height = new GridLength(3, GridUnitType.Star);
        Arrange(split, 400, 900);
        saved = split.CaptureLayout();
        Require(Math.Abs(saved.TallFraction - .25) < .001, "Tall layout did not retain its own split.");
        Arrange(split, 1200, 400);
        Require(Math.Abs(split.CaptureLayout().WideFraction - .7) < .001, "Changing orientation lost the wide split.");
        saved.Reversed = true; saved.FocusedPane = 1;
        saved.Arrangement = "wide";
        split.RestoreLayout(JsonSerializer.Deserialize<SplitPaneLayout>(JsonSerializer.Serialize(saved)));
        Arrange(split, 1200, 400);
        Require(Grid.GetColumn(first) == 2 && first.IsVisible && !second.IsVisible, "Persisted pane order/focus was not restored.");
        Arrange(split, 400, 900);
        Require(split.ColumnDefinitions.Count == 3, "Explicit arrangement was overridden by window size.");
        split.ResetLayout(); Arrange(split, 1200, 400);
        Require(first.IsVisible && second.IsVisible && Math.Abs(split.CaptureLayout().WideFraction - .6) < .001, "Reset did not restore the original split.");
        Require(first.Text == "Unsaved content" && second.Text == "Preview state", "Changing layout lost editor content.");
        split.RestoreLayout(new() { WideFraction = double.NaN, TallFraction = -1, FocusedPane = 900 });
        Require(double.IsFinite(split.CaptureLayout().WideFraction), "Invalid persisted dimensions were accepted.");
    }

    private static void TestWindow()
    {
        // Empty settings deliberately avoid the user's live server and client.
        var settings = new DesktopSettings();
        var window = new MainWindow(settings);
        try
        {
            var root = window.FindControl<Grid>("RootLayout")!;
            var picker = CoverageStorageProvider.Create();
            typeof(TopLevel).GetField("_storageProvider", Private)!.SetValue(window, picker);
            ((Task)typeof(MainWindow).GetMethod("OpenGameFilesAsync", Private)!.Invoke(window, [false])!).GetAwaiter().GetResult();
            var openOptions = ((CoverageStorageProvider)picker).LastOpenOptions!;
            Require(openOptions.Title == "Open" && openOptions.FileTypeFilter!.First().Patterns!.Contains("*"), "Open must let the file router identify any selected file.");
            var host = window.FindControl<Border>("FeatureWorkspaceHost")!;
            var browser = window.FindControl<Border>("NavigationPane")!;
            ArrangeWindow(window, 1440, 900);
            Capture(root, 1440, 900, "shell-start.png");
            var tabState = (WorkspaceTabState)typeof(MainWindow).GetField("_workspaceTabs", Private)!.GetValue(window)!;
            var draft = new TextBox { Text = "Unapplied record edits" };
            var first = new UserControl { Content = draft };
            var second = new UserControl { Content = new TextBlock { Text = "Other editor" } };
            Invoke(window, "OpenFeatureWorkspace", first, "Test record editor");
            var firstId = tabState.SelectedId;
            Invoke(window, "OpenFeatureWorkspace", second, "Test world editor");
            Invoke(window, "SelectWorkspaceTab", firstId);
            Require(ReferenceEquals(host.Child, first) && browser.IsVisible, "Switching editors lost the control or hid navigation.");
            Invoke(window, "CloseWorkspaceTab", firstId);
            Invoke(window, "OpenFeatureWorkspace", first, "Test record editor");
            Require(tabState.SelectedId == firstId && draft.Text == "Unapplied record edits", "Reopening discarded the editor session.");
            Invoke(window, "SelectWorkspaceTab", "home");
            Require(tabState.SelectedId == "home" && window.FindControl<WorkspaceStartPage>("StartPage")!.IsVisible && !window.FindControl<Border>("MainHeader")!.IsVisible, "Home inherited the table toolbar.");
            Invoke(window, "SelectWorkspaceTab", "tables");
            Require(tabState.SelectedId == "tables" && window.FindControl<StackPanel>("TableStartPanel")!.IsVisible, "Empty table workspace opened the wrong start surface.");
            Invoke(window, "SelectWorkspaceTab", "home");
            foreach (var (width, height) in new[] { (1440, 900), (1024, 768), (800, 600) })
            {
                ArrangeWindow(window, width, height);
                Require(browser.Bounds.Width > 100 && root.Bounds.Width <= width + 1, "Shell overflow at a supported window size.");
                foreach (var right in new[] { false, true })
                {
                    settings.EditorLayout.BrowserOnRight = right;
                    Invoke(window, "ApplyShellPaneState"); ArrangeWindow(window, width, height);
                    Require(Grid.GetColumn(browser) == (right ? 4 : 0), "Browser failed to dock on requested edge.");
                    Invoke(window, "SelectWorkspaceTab", firstId); ArrangeWindow(window, width, height);
                    Require(host.Bounds.Width > 350 && host.Bounds.Right <= root.Bounds.Width + 1, "Editor has no usable bounded content area.");
                    Require(right ? host.Bounds.Right <= browser.Bounds.X + 1 : browser.Bounds.Right <= host.Bounds.X + 1, "Editor overlaps the tool browser.");
                }
                settings.EditorLayout.BrowserOnRight = false;
                Invoke(window, "SelectWorkspaceTab", "home"); ArrangeWindow(window, width, height);
                var strip = window.FindControl<WorkspaceTabStrip>("WorkspaceTabs")!;
                var buttons = (StackPanel)typeof(WorkspaceTabStrip).GetField("_tabs", Private)!.GetValue(strip)!;
                Require(buttons.Children.All(child => child.Bounds.Width > 20 && child.Bounds.Height > 20), "Workspace tabs did not lay out their labels and close controls.");
                Capture(root, width, height, $"shell-home-{width}.png");
            }
            Invoke(window, "SelectWorkspaceTab", firstId);
            var inspectorMenu = (MenuItem)typeof(MainWindow).GetField("_inspectorMenu", Private)!.GetValue(window)!;
            Require(!inspectorMenu.IsVisible, "Table inspector command leaked into a feature workspace.");
            ArrangeWindow(window, 1440, 900);
            var regularHeight = host.Bounds.Height;
            Invoke(window, "ToggleEditorFocus");
            ArrangeWindow(window, 1440, 900);
            Require(!browser.IsVisible, "Focus mode did not hide navigation.");
            Require(!window.FindControl<WorkspaceRuntimeStrip>("RuntimeStrip")!.IsVisible && !window.FindControl<Grid>("WorkspaceBar")!.IsVisible, "Focus mode retained shell bars.");
            Require(host.Bounds.Height > regularHeight, "Focus mode hid controls without returning editor space.");
            Invoke(window, "ToggleEditorFocus");
            Require(browser.IsVisible && draft.Text == "Unapplied record edits", "Focus mode failed to restore panels/content.");
            settings.EditorLayout.TabsVisible = false;
            Invoke(window, "ApplyShellPaneState");
            Require(window.FindControl<Button>("RestoreChromeButton")!.IsVisible, "Hiding tabs removed the recovery control.");
            Invoke(window, "RestoreChromeClick", null!, new Avalonia.Interactivity.RoutedEventArgs());
            Require(settings.EditorLayout.TabsVisible, "Recovery control did not restore layout access.");
            var roundTrip = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(settings))!;
            Require(roundTrip.EditorLayout.BrowserVisible && !roundTrip.EditorLayout.BrowserOnRight, "Shell layout settings did not round-trip.");
            foreach (var (name, open) in new (string, Action)[]
            {
                ("items", window.OpenItemWorkbench),
                ("textures", () => window.OpenTextureWorkspace()),
                ("maps", () => window.OpenMapWorkspace())
            })
            {
                open();
                ArrangeWindow(window, 1440, 900);
                var localBars = host.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("workspace-toolbar") && c.IsVisible).ToArray();
                settings.EditorLayout.ToolbarsVisible = false;
                Invoke(window, "ApplyShellPaneState"); ArrangeWindow(window, 1440, 900);
                Require(localBars.All(c => !c.IsVisible), name + " retained its editor toolbar");
                Require(!window.FindControl<Border>("FeatureToolbarHost")!.IsVisible, name + " retained shared toolbar space");
                settings.EditorLayout.ToolbarsVisible = true;
                Invoke(window, "ApplyShellPaneState"); ArrangeWindow(window, 1440, 900);
                Require(localBars.All(c => c.IsVisible), name + " did not restore its toolbar");
                foreach (var (width, height) in new[] { (1440, 900), (1024, 768) })
                {
                    ArrangeWindow(window, width, height);
                    Require(host.Child is not null && host.Bounds.Width > 700, $"{name} editor did not occupy the content area.");
                    Capture(root, width, height, $"shell-{name}-{width}.png");
                }
            }
            window.OpenClassStudio();
            var studio = host.Child;
            var studioTab = tabState.SelectedId;
            Require(studio is ClassStudioView && tabState.Tabs.Single(tab => tab.Id == studioTab).Title == "Class Studio", "Class Studio did not open inside the editor shell.");
            Invoke(window, "CloseWorkspaceTab", studioTab);
            window.OpenClassStudio();
            Require(ReferenceEquals(studio, host.Child) && tabState.SelectedId == studioTab, "Reopening Class Studio duplicated its session or tab.");
            var configured = (HashSet<ResponsiveSplitGrid>)typeof(MainWindow).GetField("_configuredSplits", Private)!.GetValue(window)!;
            Require(configured.Count >= 3, "Shared pane persistence was not attached to real editors.");
            window.OpenAssetComparison(Path.Combine(AppContext.BaseDirectory, "missing-asset-corpus"));
            var assets = (AssetComparisonView)host.Child!;
            var assetId = tabState.SelectedId;
            Invoke(window, "SelectWorkspaceTab", "home");
            Require(!(bool)typeof(AssetComparisonView).GetField("_active", Private)!.GetValue(assets)!, "Leaving assets did not suspend background work.");
            Invoke(window, "SelectWorkspaceTab", assetId);
            Require((bool)typeof(AssetComparisonView).GetField("_active", Private)!.GetValue(assets)!, "Restoring the asset tab left the editor suspended.");
            TestDocumentIsolation(window, first, firstId, tabState);
        }
        finally { window.Close(); }
    }

    private static void TestDocumentIsolation(MainWindow window, UserControl editor, string editorId, WorkspaceTabState tabs)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ShellFixture.dbc");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("WDBC")); writer.Write(1); writer.Write(2); writer.Write(8); writer.Write(1);
            writer.Write(900001); writer.Write(7); writer.Write((byte)0);
        }
        var before = File.ReadAllBytes(path);
        var load = window.LoadPathAsync(path);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!load.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Require(load.IsCompleted, "Fixture data table did not load.");
        load.GetAwaiter().GetResult();
        var documents = (List<DbcDocumentSession>)typeof(MainWindow).GetField("_documents", Private)!.GetValue(window)!;
        Require(documents.Count == 1 && tabs.SelectedId == "tables", "Loading a table did not activate its workspace.");
        Require(((MenuItem)typeof(MainWindow).GetField("_inspectorMenu", Private)!.GetValue(window)!).IsVisible, "Table inspector command was missing from a loaded table.");
        Invoke(window, "OpenFeatureWorkspace", editor, "Test record editor");
        foreach (var key in new[] { Key.S, Key.Z, Key.Y })
        {
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control };
            Invoke(window, "OnKeyDown", input);
            Require(!input.Handled, $"Hidden table captured {key} from another editor.");
        }
        Invoke(window, "OnKeyDown", new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.W, KeyModifiers = KeyModifiers.Control });
        Require(tabs.Tabs.All(tab => tab.Id != editorId) && documents.Count == 1, "Closing an editor tab closed the hidden DBC document.");
        Require(before.SequenceEqual(File.ReadAllBytes(path)), "Navigating editors modified the source fixture.");
    }

    private static void Arrange(Control control, int width, int height)
    {
        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs();
    }

    private static void ArrangeWindow(Window window, int width, int height)
    {
        window.Width = width; window.Height = height; window.ApplyTemplate();
        Arrange(window, width, height);
        // Render and measure the actual root without showing or focusing a desktop window.
        var root = window.FindControl<Grid>("RootLayout")!;
        root.Width = width; root.Height = height;
        // A never-shown Window does not schedule the normal visible-tree layout pass.
        // Invalidate the complete visual tree before explicitly measuring its root.
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var child in root.GetVisualDescendants().OfType<Control>().Reverse()) child.InvalidateMeasure();
            root.InvalidateMeasure();
            Arrange(root, width, height);
            root.UpdateLayout();
        }
    }

    private static void Capture(Control root, int width, int height, string filename)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.local/editor-shell-tests"));
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(root); bitmap.Save(Path.Combine(directory, filename), new PngBitmapEncoderOptions());
    }

    private static void Invoke(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
