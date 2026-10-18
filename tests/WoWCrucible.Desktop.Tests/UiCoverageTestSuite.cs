using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WoWCrucible.Desktop;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;
using static Program;

internal static class UiCoverageTestSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Dictionary<string, Entry> Entries = [];
    private static readonly List<string> Errors = [];
    private static Entry? _active;
    private static readonly Avalonia.Platform.Storage.IStorageProvider Storage = CoverageStorageProvider.Create();
    private static int PickerCalls => ((CoverageStorageProvider)Storage).Calls;
    private static readonly string Output = Path.GetFullPath(".local/ui-coverage");

    private static bool _embeddedOnly;
    public static void Run(bool activate, bool embeddedOnly)
    {
        _embeddedOnly = embeddedOnly;
        Directory.CreateDirectory(Output);
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var completed = false;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Errors.Add(e.Exception.ToString());
            if (_active is not null) { _active.Status = "failed"; _active.Evidence = e.Exception.ToString(); }
            e.Handled = true; // Capture as a failed run, never as a passing swallowed error.
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Errors.Add(e.Exception.ToString());
        Dispatcher.UIThread.Post(async () =>
        {
            try { await ExerciseAsync(activate); completed = true; }
            catch (Exception e) { Errors.Add(e.ToString()); }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (!completed) Errors.Add("Coverage walk did not complete.");
        Save();
        Console.WriteLine($"Coverage: {Entries.Count} cases; {Entries.Values.Count(e => e.Status == "activated")} activated; {Errors.Count} errors. {Output}");
        Require(Errors.Count == 0, string.Join("\n", Errors.Distinct().Take(10)));
    }

    private static async Task ExerciseAsync(bool activate)
    {
        Require(Path.GetFullPath(CruciblePaths.ApplicationDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), "Coverage settings must stay in the test executable directory.");
        Directory.CreateDirectory(CruciblePaths.SettingsDirectory);
        File.WriteAllText(Path.Combine(CruciblePaths.SettingsDirectory, "class-studio.json"), "{}");
        var fixture = Path.Combine(Output, "fixtures"); Directory.CreateDirectory(fixture); WriteSpellFixture(fixture);
        File.Copy(Path.Combine(fixture, "Spell.dbc"), Path.Combine(Output, "fixture-original.dbc"), true);
        var window = new MainWindow(new DesktopSettings { BackupChoiceRemembered = true });
        typeof(TopLevel).GetField("_storageProvider", Private)!.SetValue(window, Storage);
        var renderer = typeof(TopLevel).GetProperty("Renderer", Private)!.GetValue(window)!;
        renderer.GetType().GetMethod("Start")!.Invoke(renderer, null);
        var host = window.FindControl<Border>("FeatureWorkspaceHost")!;
        var routes = (IReadOnlyDictionary<string, Func<Task>>)typeof(MainWindow).GetMethod("BuildCommandRoutes", Private)!.Invoke(window, null)!;
        var visited = new HashSet<Control>();
        try
        {
            Arrange(window);
            if (!_embeddedOnly)
            {
            await Inspect(window, "shell", window, activate);
            foreach (var route in routes.Where(r => r.Key.StartsWith("workspace.")))
            {
                Console.WriteLine(route.Key);
                var entry = Add("navigation", route.Key, "command", "open");
                await Act(entry, async () => { await route.Value().WaitAsync(TimeSpan.FromSeconds(45)); Arrange(window); });
                if (host.IsVisible && host.Child is { } page && visited.Add(page))
                    await Inspect(page, page.GetType().Name, window, activate);
                Save();
            }
            foreach (var open in new Action[] { window.OpenPetLevelCurveWorkspace, window.OpenPetAbilityGraphWorkspace })
            {
                open(); Arrange(window);
                if (host.Child is { } page && visited.Add(page)) await Inspect(page, page.GetType().Name, window, activate);
            }
            await window.LoadPathAsync(Path.Combine(fixture, "Spell.dbc"));
            await Inspect(window, "shell-loaded", window, activate);
            foreach (var command in new[] { "action.export-rows", "action.import-rows", "action.dbc-staging", "action.spell" })
            {
                var entry = Add("navigation", command, "command", "open");
                await Act(entry, async () => { await routes[command](); Arrange(window); });
                if (host.Child is { } page && visited.Add(page)) await Inspect(page, page.GetType().Name, window, activate);
            }
            if (activate) await LoadedFixtures(window, fixture);
            }
            if (activate)
            {
                window.OpenClassStudio(); Arrange(window);
                var studio = (ClassStudioView)host.Child!;
                await studio.Ready.WaitAsync(TimeSpan.FromSeconds(30));
                var browser = studio.Browser!;
                await browser.ExecuteScriptAsync("window.coverageServer=" + JsonSerializer.Serialize(fixture) + ";");
                await browser.ExecuteScriptAsync(File.ReadAllText("tests/WoWCrucible.Desktop.Tests/ui-coverage.js"));
                for (var wait = 0; wait < 2400; wait++)
                {
                    var json = await browser.ExecuteScriptAsync("window.coverageResult || null");
                    if (json != "null")
                    {
                        using var result = JsonDocument.Parse(json);
                        foreach (var item in result.RootElement.GetProperty("Cases").EnumerateArray())
                        {
                            var entry = item.Deserialize<Entry>()!; Entries[entry.Id] = entry;
                        }
                        Errors.AddRange(result.RootElement.GetProperty("Errors").EnumerateArray().Select(e => e.GetString()!));
                        break;
                    }
                    if (wait == 2399) throw new TimeoutException("Embedded coverage did not finish.");
                    await Task.Delay(50);
                }
                await studio.FlushAsync();
            }
            await Task.Delay(300);
        }
        finally
        {
            window.Close();
            if (window.FindControl<Grid>("DialogOverlayHost")!.IsVisible)
            {
                var discard = Walk(window.FindControl<ContentControl>("DialogContent")!).OfType<Button>().FirstOrDefault(b => Label(b) == "Discard");
                discard?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            await Task.Delay(150);
        }
    }

    private static async Task LoadedFixtures(MainWindow window, string fixture)
    {
        var host = window.FindControl<Border>("FeatureWorkspaceHost")!;
        window.OpenTextureWorkspace(); Arrange(window);
        var texture = (TextureWorkspaceView)host.Child!;
        var blp = Path.Combine(fixture, "coverage.blp");
        BlpTextureService.EncodeBlp2(new RgbaTexture(8, 8, Enumerable.Repeat((byte)200, 8 * 8 * 4).ToArray()), blp, new(BlpOutputFormat.Dxt5, true, BlpOutputQuality.Balanced), overwrite: true);
        var before = File.ReadAllBytes(blp);
        await texture.OpenAsync(blp);
        Require(Field<object?>(texture, "_info") is not null, "BLP coverage fixture did not load.");
        await Inspect(texture, "TextureWorkspaceView-loaded", window, true);
        Require(before.SequenceEqual(File.ReadAllBytes(blp)), "Texture controls changed the source file without Save.");

        var project = Path.Combine(fixture, "project"); Directory.CreateDirectory(project);
        window.OpenProjectWorkspace(); Arrange(window);
        var projectView = host.Child!;
        Field<TextBox>(projectView, "_root").Text = project;
        Field<TextBox>(projectView, "_name").Text = "UI coverage fixture";
        await ClickNamed(projectView, File.Exists(Path.Combine(project, "project.crucible.json")) ? "Open project" : "Create project");
        Require(File.Exists(Path.Combine(project, "project.crucible.json")), "Project UI did not create/open its fixture.");
        await window.LoadPathAsync(Path.Combine(fixture, "Spell.dbc"));
        typeof(MainWindow).GetMethod("OpenDbcStagingClick", Private)!.Invoke(window, [null, new RoutedEventArgs()]); Arrange(window);
        var staging = host.Child!;
        Field<TextBox>(staging, "_project").Text = project;
        Field<CheckBox>(staging, "_replace").IsChecked = true;
        await StagingClick("_create");
        Require(File.Exists(Field<TextBox>(staging, "_workspace").Text), "Staging UI did not create its local SQLite fixture: " + Field<TextBlock>(staging, "_status").Text);
        await StagingClick("_query"); await StagingClick("_refresh");
        var document = Field<DbcDocumentSession>(staging, "_document");
        var column = document.Schema.Columns[1];
        Field<TextBox>(staging, "_sql").Text = "UPDATE working SET \"" + column.Name.Replace("\"", "\"\"") + "\" = 1";
        await StagingClick("_previewMutation");
        Field<CheckBox>(staging, "_confirmMutation").IsChecked = true;
        await StagingClick("_applyMutation"); await StagingClick("_refresh"); await StagingClick("_previewPublish");
        Field<CheckBox>(staging, "_confirmPublish").IsChecked = true;
        await StagingClick("_applyPublish");
        Require(document.File.IsDirty, "Staging publication did not mark the fixture document dirty.");
        Require(File.ReadAllBytes(Path.Combine(fixture, "Spell.dbc")).SequenceEqual(File.ReadAllBytes(Path.Combine(Output, "fixture-original.dbc"))), "Staging publication wrote the source file without Save.");

        async Task StagingClick(string field)
        {
            var button = Field<Button>(staging, field);
            Require(button.IsEffectivelyEnabled, "Fixture did not enable " + field);
            var entry = Add("DbcStagingWorkspaceView", field, "Button", "activate");
            await Act(entry, async () =>
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (var i = 0; i < 500 && !Field<Button>(staging, "_create").IsEnabled; i++) await Task.Delay(10);
                Require(Field<Button>(staging, "_create").IsEnabled, "Staging operation did not finish: " + field);
                var expected = field switch
                {
                    "_create" => "Staged ", "_query" => "Returned ", "_refresh" => "changed cells",
                    "_previewMutation" => "Preview only:", "_applyMutation" => "Applied the reviewed mutation",
                    "_previewPublish" => "Publication preview built", "_applyPublish" => "to the open DBC",
                    _ => throw new InvalidOperationException("Missing staging assertion: " + field)
                };
                Require(Field<TextBlock>(staging, "_status").Text?.Contains(expected) == true, "Staging operation failed: " + Field<TextBlock>(staging, "_status").Text);
            });
            if (entry.Status == "activated") entry.Evidence = "Temporary project fixture: " + Field<TextBlock>(staging, "_status").Text;
        }
        async Task ClickNamed(Control root, string name)
        {
            var button = Walk(root).OfType<Button>().Single(b => Label(b) == name);
            Reveal(root, button); Arrange(window);
            await Act(Add(root.GetType().Name, name, "Button", "activate"), () => { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask; });
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private static async Task Inspect(Control root, string scope, MainWindow window, bool activate)
    {
        // Expand every declared tab so controls built on selection enter the inventory.
        var tabsSeen = new HashSet<TabControl>();
        for (var pass = 0; pass < 5; pass++)
        {
            var tabs = Walk(root).OfType<TabControl>().Where(t => tabsSeen.Add(t)).ToArray();
            if (tabs.Length == 0) break;
            foreach (var tab in tabs)
            {
                var initial = tab.SelectedIndex;
                for (var index = 0; index < tab.Items.Count; index++)
                {
                    var entry = Add(scope, Label(tab), "tab", $"{index}: {Label(tab.Items[index])}");
                    var selected = index;
                    await Act(entry, () => { tab.SelectedIndex = selected; Arrange(window); return Task.CompletedTask; });
                    Inventory();
                }
                tab.SelectedIndex = initial;
            }
        }
        Inventory();
        if (activate)
        {
            var occurrences = new Dictionary<string, int>();
            foreach (var control in Walk(root).ToArray())
            {
                if (!Interactive(control)) continue;
                var kind = control.GetType().Name;
                var label = FieldName(root, control) ?? Label(control);
                var key = kind + "/" + label;
                var ordinal = occurrences.GetValueOrDefault(key); occurrences[key] = ordinal + 1;
                label += ordinal > 0 ? $" [{ordinal + 1}]" : "";
                var entry = Add(scope, label, kind, "activate");
                if (root != window && window.FindControl<Border>("FeatureWorkspaceHost")!.Child != root)
                    typeof(MainWindow).GetMethod("OpenFeatureWorkspace", Private)!.Invoke(window, [root, scope]);
                Reveal(root, control); Arrange(window);
                if (!control.IsEffectivelyEnabled) { entry.Evidence = "Disabled in this fixture state; needs enabling data/context."; continue; }
                if (control.GetLogicalAncestors().Any(a => a.GetType().Namespace?.Contains("WindowsIntegration") == true) || label.Contains("Devbug"))
                { entry.Evidence = "Changes Windows integration or opens a diagnostic console; requires an exclusive desktop scenario."; continue; }
                switch (control)
                {
                    case ComboBox combo:
                        var initial = combo.SelectedIndex;
                        var choices = combo.Items.Cast<object?>().ToArray();
                        for (var i = 0; i < choices.Length; i++)
                        {
                            var option = Add(scope, label, kind, $"select {i}: {Label(choices[i])}");
                            var index = i;
                            await Act(option, () => { combo.SelectedIndex = -1; combo.SelectedIndex = index; return Task.CompletedTask; });
                        }
                        combo.SelectedIndex = initial;
                        if (choices.Length > 0) { entry.Status = "activated"; entry.Activations++; entry.Evidence = "Enumerated selections; see individual option cases."; }
                        else entry.Evidence = "No choices populated; needs loaded data.";
                        break;
                    case TabControl tab:
                        entry.Status = "activated"; entry.Activations++; entry.Evidence = "Enumerated tabs; see individual tab cases."; break;
                    case ToggleButton toggle:
                        await Act(entry, () => { var original = toggle.IsChecked; typeof(Button).GetMethod("OnClick", Private)!.Invoke(toggle, null); typeof(Button).GetMethod("OnClick", Private)!.Invoke(toggle, null); toggle.IsChecked = original; return Task.CompletedTask; }); break;
                    case TextBox text when !text.IsReadOnly:
                        await Act(entry, () => { var original = text.Text; text.Text = (original ?? "") + " "; text.Text = original; return Task.CompletedTask; }); break;
                    case TextBox:
                        entry.Evidence = "Read-only output; no input activation applicable."; break;
                    case NumericUpDown number:
                        await Act(entry, () => { var original = number.Value; var value = original ?? 0; number.Value = Math.Clamp(value + number.Increment, number.Minimum, number.Maximum); number.Value = original; return Task.CompletedTask; }); break;
                    case Slider slider:
                        await Act(entry, () => { var original = slider.Value; slider.Value = slider.Minimum; slider.Value = slider.Maximum; slider.Value = original; return Task.CompletedTask; }); break;
                    case Expander expander:
                        await Act(entry, () => { var original = expander.IsExpanded; expander.IsExpanded = true; expander.IsExpanded = false; expander.IsExpanded = original; return Task.CompletedTask; }); break;
                    case ListBox list:
                        if (list.Items.Count == 0) { entry.Evidence = "No rows populated; needs a loaded-data scenario."; break; }
                        await Act(entry, () => { var original = list.SelectedIndex; list.SelectedIndex = -1; list.SelectedIndex = 0; list.SelectedIndex = original; return Task.CompletedTask; }); break;
                    case Button button:
                        if (!SafeButton(scope, Label(button))) { entry.Evidence = "Requires a reviewed data/output, system integration, navigation, or deployment scenario; not blindly clicked."; break; }
                        var calls = PickerCalls;
                        await Act(entry, async () => { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(30); Arrange(window); });
                        if (PickerCalls > calls) entry.Evidence = "Real button handler called picker; test provider returned cancellation. OS dialog and successful file selection not covered.";
                        break;
                    case MenuItem menu:
                        var caption = Label(menu);
                        if (menu.Items.Count > 0) { entry.Evidence = "Menu container; individual child commands are inventoried separately."; break; }
                        if (!SafeMenu(caption)) { entry.Evidence = "Context action requires target selection, system integration, or a reviewed scenario."; break; }
                        await Act(entry, () => { menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Arrange(window); return Task.CompletedTask; });
                        break;
                    case GridSplitter:
                        entry.Evidence = "Pointer-driven resize requires hit-testing; layout changes are covered separately."; break;
                }
            }
            Inventory();
        }

        void Inventory()
        {
            var occurrences = new Dictionary<string, int>();
            foreach (var control in Walk(root))
            {
                var kind = control.GetType().Name;
                if (!Interactive(control)) continue;
                var label = FieldName(root, control) ?? Label(control);
                var duplicateKey = kind + "/" + label;
                var ordinal = occurrences.GetValueOrDefault(duplicateKey); occurrences[duplicateKey] = ordinal + 1;
                label += ordinal > 0 ? $" [{ordinal + 1}]" : "";
                var entry = Add(scope, label, kind, "activate");
                if (entry.Status == "untested" && entry.Evidence == "No activation recorded.") entry.Evidence = control.IsEffectivelyEnabled ? "Dynamic control inventoried outside the activation snapshot; needs a separate state scenario." : "Disabled in this fixture state.";
                if (control is ComboBox choices)
                    for (var i = 0; i < choices.Items.Count; i++)
                    {
                        var option = Add(scope, label, kind, $"select {i}: {Label(choices.Items[i])}");
                        if (option.Evidence == "No activation recorded.") option.Evidence = "Option inventoried but not selected in this run.";
                    }
            }
        }
    }

    private static bool Reveal(Control root, Control target)
    {
        if (ReferenceEquals(root, target)) return true;
        IEnumerable<Control> children = root switch
        {
            Panel panel => panel.Children,
            ItemsControl items => items.Items.OfType<Control>(),
            Decorator { Child: { } child } => [child],
            ContentControl { Content: Control child } => [child],
            _ => []
        };
        foreach (var child in children)
        {
            if (!Reveal(child, target)) continue;
            if (root is TabControl tab) tab.SelectedItem = child;
            if (root is Expander expander) expander.IsExpanded = true;
            return true;
        }
        return false;
    }

    private static bool Interactive(Control c) => c is Button or ToggleSwitch or TextBox or ComboBox or ListBox or TabControl or Slider or NumericUpDown or Expander or MenuItem or GridSplitter;

    private static readonly HashSet<string> NavigationLabels = WorkspaceNavigation.Groups.SelectMany(g => g.Commands).Select(WorkspaceNavigation.Label).ToHashSet();
    private static bool SafeMenu(string label) => NavigationLabels.Contains(label) || label is "Open DBC / DB2…" or "Save current table as…" or "Client & server setup" or "Previous workspace · Alt+Left" or "Find a command · Ctrl+K" or "Move tab left" or "Move tab right" or "Show both panes" or "Focus first pane" or "Focus second pane" or "Swap pane positions" or "Arrange side by side" or "Arrange top to bottom" or "Arrange automatically" or "Restore default split" or "Restore default layout" or "Focus editor / restore panels (F6)" or "Move tool browser to right" or "Move tool browser to left" or "Show / hide table inspector" or "Show / hide tool browser";

    private static bool SafeButton(string scope, string label)
    {
        if (scope.StartsWith("shell")) return NavigationLabels.Contains(label) || label is "Open DBC / DB2" or "Open DBC / DB2…" or "Save As…" or "Undo" or "Redo" or "Clear filter" or "Previous match" or "Next match" or "GoToIdButton" or "Close replace" or "Close · Esc";
        // Reviewed picker-only actions cancel before mutation. Ambiguous Open/Build/Apply
        // labels deliberately need their own fixture rather than a blanket click policy.
        if (label.StartsWith("Browse") || label.StartsWith("Choose") || label is "Output…" or "Schema…" or "DBC…" or "Assets…" or "Listfile…" or "Receipt…" or "Base skin…" or "Character M2…" or "External listfile…") return true;
        if (label is "Open BLP…" or "Source image…" or "Validate BLP…" or "Validate folder recursively…" or "Add BLP / image layers…" or "Load BLP / image mask…" or "Save selected mip as PNG…" or "Save edited PNG…" or "Save edited BLP2…" or "Add current RGBA draft" or "Apply selected layer settings" or "Render material stack" or "Use result in RGBA editor" or "Run compression proof on result" or "Preview exact channel transform" or "Analyze current draft with selected encoding" or "Fill using current tool" or "Invert alpha" or "Use loaded BLP" or "Preview rows" or "Export atomically" or "Build import preview" or "Review apply to open table") return true;
        if (label is "Add files" or "Add folder tree" or "Add source MPQs" or "Open MPQ" or "Open CASC storage" or "Load manifest" or "Save manifest" or "Bind client Wow.exe" or "Update existing small MPQ" or "Add selected path as locale variant…" or "Extract selected files/folders" or "Extract current folder recursively" or "Extract selected" or "Extract all visible" or "Merge into new MPQ…" or "Library…" or "Root asset…" or "Target index…" or "Client…" or "Patch…") return true;
        if (label is "Open map file…" or "Preview extracted WMO…" or "Create empty terrain WDT…" or "Preview selected object" or "Show live placement preview" or "Preview coordinated transform" or "Preview add / clone" or "Review delete" or "Preview height offset" or "Preview vertex brush" or "Preview layer texture" or "Preview new texture layer" or "Preview alpha paint" or "Write edited ADT…" or "Write brushed ADT…" or "Write texture-edited ADT…" or "Write structurally edited ADT…" or "Write alpha-painted ADT…" or "Select all present" or "Select all missing WDT tiles") return true;
        return label is "Cancel" or "Cancel operation" or "Cancel scan" or "Clear" or "Clear list" or "Clear selection" or "Select all" or "Select none" or "Fit view" or "Reset view" or "Undo stroke / operation" or "Reset decoded mip" or "▶" or "<" or ">" or "Defaults" or "Body only" or "All" or "Clear marker" or "Undo placement gesture" or "Redo placement gesture" or "Clear explicit choices" or "Remove selected" or "Move earlier" or "Move later" or "Remove" or "Move toward top" or "Move toward bottom" or "New row" or "New blank quest" or "Add vendor item" or "Add loot row" or "Select read-only preset" or "Select content-editor preset" or "Clear privilege selection" or "New column draft" or "Add structural output" or "Remove selected output" or "Add named lookup" or "Remove selected lookup" or "Keep preview only" or "Clear listfile" or "Root" or "↑ Up" or "Open selected folder";
    }

    private static async Task Act(Entry entry, Func<Task> action)
    {
        _active = entry;
        try
        {
            await action(); await Task.Delay(10);
            if (entry.Status != "failed") { entry.Status = "activated"; entry.Evidence = "Handler/selection activated; no exception observed during dispatcher settle."; entry.Activations++; }
        }
        catch (Exception e) { entry.Status = "failed"; entry.Evidence = e.ToString(); Errors.Add(entry.Id + ": " + e); }
        finally { _active = null; }
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        var seen = new HashSet<Control>(); var pending = new Stack<Control>(); pending.Push(root);
        while (pending.TryPop(out var control))
        {
            if (!seen.Add(control)) continue;
            yield return control;
            IEnumerable<Control> children = control switch
            {
                Panel panel => panel.Children,
                ItemsControl items => items.Items.OfType<Control>(),
                Decorator { Child: { } child } => [child],
                ContentControl { Content: Control child } => [child],
                _ => []
            };
            foreach (var child in children.Reverse()) pending.Push(child);
            if (control.ContextMenu is { } menu) pending.Push(menu);
            if (control is Button { Flyout: MenuFlyout flyout }) foreach (var item in flyout.Items.OfType<Control>()) pending.Push(item);
        }
    }

    private static string? FieldName(Control root, Control control) => root.GetType().GetFields(Private)
        .FirstOrDefault(field => typeof(Control).IsAssignableFrom(field.FieldType) && ReferenceEquals(field.GetValue(root), control))?.Name;
    private static string Label(object? value) => value switch
    {
        null => "(empty)",
        Control c when !string.IsNullOrWhiteSpace(c.Name) => c.Name,
        Control c when !string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)) => AutomationProperties.GetName(c)!,
        MenuItem c => Label(c.Header),
        HeaderedContentControl c => Label(c.Header),
        HeaderedItemsControl c => Label(c.Header),
        ContentControl c => Label(c.Content),
        TextBox c => c.PlaceholderText ?? "text",
        TextBlock c => c.Text ?? "text",
        Control c => c.GetType().Name,
        _ => value.ToString() ?? "(empty)"
    };
    private static Entry Add(string scope, string control, string kind, string option)
    {
        var id = $"{scope}/{kind}/{control}/{option}";
        if (!Entries.TryGetValue(id, out var entry)) Entries[id] = entry = new() { Id = id, Scope = scope, Control = control, Kind = kind, Option = option };
        return entry;
    }
    private static void Arrange(Window window)
    {
        window.Width = 1440; window.Height = 900; window.ApplyTemplate();
        var size = new Size(1440, 900); window.Measure(size); window.Arrange(new Rect(size));
        var root = window.FindControl<Grid>("RootLayout")!; root.Width = 1440; root.Height = 900;
        foreach (var child in root.GetVisualDescendants().OfType<Control>().Reverse()) child.InvalidateMeasure();
        root.InvalidateMeasure(); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
    }
    private static void Save() => File.WriteAllText(Path.Combine(Output, _embeddedOnly ? "runtime-embedded.json" : "runtime.json"), JsonSerializer.Serialize(new { UpdatedUtc = DateTimeOffset.UtcNow, Errors, Cases = Entries.Values }, new JsonSerializerOptions { WriteIndented = true }));
    private sealed class Entry
    {
        public string Id { get; init; } = "";
        public string Scope { get; init; } = "";
        public string Control { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Option { get; init; } = "";
        public string Status { get; set; } = "untested";
        public string Evidence { get; set; } = "No activation recorded.";
        public int Activations { get; set; }
    }
}
