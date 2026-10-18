using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WoWCrucible.Core;
using WoWCrucible.Desktop;
using WoWCrucible.Desktop.Controls;
using static Program;

internal static class UiStressTestSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Run(int seed, int steps)
    {
        if (steps < 1 || steps > 100000) throw new ArgumentOutOfRangeException(nameof(steps));
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        var directory = Path.GetFullPath(".local/ui-stress"); Directory.CreateDirectory(directory);
        using var trace = new StreamWriter(Path.Combine(directory, $"actions-{seed}.jsonl")) { AutoFlush = true };
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var report = new StressReport { Seed = seed, Requested = steps, StartedUtc = DateTimeOffset.UtcNow };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            report.Error = e.Exception.ToString(); e.Handled = true; stop.Cancel();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { report.Error = e.Exception.ToString(); stop.Cancel(); };
        Dispatcher.UIThread.Post(async () =>
        {
            try { await ExerciseAsync(seed, steps, trace, report); report.Passed = report.Error is null; }
            catch (Exception error) { report.Error = error.ToString(); }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (!report.Passed && report.Error is null) report.Error = "The stress run timed out.";
        report.CompletedUtc = DateTimeOffset.UtcNow;
        var path = Path.Combine(directory, $"report-{seed}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        Console.WriteLine($"{(report.Passed ? "PASS" : "FAIL")} UI stress seed {seed}: {report.Completed}/{steps} actions, {report.EmbeddedClicks} embedded clicks; report: {path}");
        Require(report.Passed, report.Error ?? "Stress test did not complete.");
    }

    private static async Task ExerciseAsync(int seed, int steps, StreamWriter trace, StressReport report)
    {
        // This executable's portable Config/Cache directories are separate from the installed app.
        var settings = new DesktopSettings();
        var window = new MainWindow(settings);
        // Never-shown windows normally disable composition and retain every dirty visual.
        // Run their real compositor so stress measurements include normal frame retirement.
        var renderer = typeof(TopLevel).GetProperty("Renderer", Private)!.GetValue(window)!;
        renderer.GetType().GetMethod("Start")!.Invoke(renderer, null);
        var random = new Random(seed);
        var host = window.FindControl<Border>("FeatureWorkspaceHost")!;
        var root = window.FindControl<Grid>("RootLayout")!;
        var tabs = Field<WorkspaceTabState>(window, "_workspaceTabs");
        var strip = window.FindControl<WorkspaceTabStrip>("WorkspaceTabs")!;
        var open = new (string Name, Action Action)[]
        {
            ("items", window.OpenItemWorkbench), ("creatures", window.OpenCreatureWorkspace),
            ("objects", window.OpenGameObjectWorkspace), ("quests", window.OpenQuestWorkspace),
            ("behaviors", window.OpenBehaviorWorkspace), ("pets", window.OpenPetWorkspace),
            ("pet curves", window.OpenPetLevelCurveWorkspace), ("pet abilities", window.OpenPetAbilityGraphWorkspace),
            ("textures", () => window.OpenTextureWorkspace()), ("maps", () => window.OpenMapWorkspace()),
            ("class studio", window.OpenClassStudio)
        };
        var identities = new Dictionary<string, Control>();
        var timings = new List<double>();
        try
        {
            foreach (var editor in open)
            {
                trace.WriteLine(JsonSerializer.Serialize(new { phase = "warmup", editor.Name }));
                editor.Action(); Arrange(); identities[editor.Name] = host.Child!;
                await Task.Delay(1);
            }
            var studio = (ClassStudioView)identities["class studio"];
            await studio.Ready.WaitAsync(TimeSpan.FromSeconds(30));
            var browser = studio.Browser!;
            await browser.ExecuteScriptAsync("window.stressErrors=[]; addEventListener('error',e=>stressErrors.push(e.message)); addEventListener('unhandledrejection',e=>stressErrors.push(String(e.reason))); StudioBridge.ready.then(()=>window.stressReady=true);");
            for (var i = 0; i < 300 && await browser.ExecuteScriptAsync("window.stressReady===true") != "true"; i++) await Task.Delay(10);
            Require(await browser.ExecuteScriptAsync("window.stressReady===true") == "true", "Embedded editor did not restore.");
            report.Samples.Add(Sample(0));
            for (var index = 0; index < steps; index++)
            {
                var kind = random.Next(10); var choice = random.Next(open.Length);
                trace.WriteLine(JsonSerializer.Serialize(new { index, kind, choice, selected = tabs.SelectedId }));
                var clock = Stopwatch.StartNew();
                switch (kind)
                {
                    case 0:
                    case 1:
                        open[choice].Action();
                        Require(ReferenceEquals(host.Child, identities[open[choice].Name]), "Reopening replaced a retained editor.");
                        break;
                    case 2:
                        var buttons = Field<StackPanel>(strip, "_tabs").Children.OfType<Border>().ToArray();
                        var panel = (StackPanel)buttons[choice % buttons.Length].Child!;
                        Click((Button)panel.Children[0]);
                        break;
                    case 3:
                        var candidates = Field<StackPanel>(strip, "_tabs").Children.OfType<Border>()
                            .Select(b => (StackPanel)b.Child!).Where(p => p.Children.Count > 1).ToArray();
                        if (candidates.Length > 0) Click((Button)candidates[choice % candidates.Length].Children[1]);
                        open[choice].Action();
                        break;
                    case 4:
                        var sizes = new[] { (800, 600), (1024, 768), (1440, 900), (1920, 1080) };
                        var size = sizes[choice % sizes.Length]; Arrange(size.Item1, size.Item2);
                        break;
                    case 5:
                        var menu = (MenuFlyout)window.FindControl<Button>("LayoutButton")!.Flyout!;
                        var commands = menu.Items.OfType<MenuItem>().Where(item => item.IsEnabled && item.Items.Count == 0).ToArray();
                        commands[choice % commands.Length].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        break;
                    case 6:
                        var split = host.Child?.GetVisualDescendants().OfType<ResponsiveSplitGrid>().FirstOrDefault();
                        if (split is not null)
                        {
                            var splitCommands = split.CreateLayoutMenuItems(); splitCommands[choice % splitCommands.Count].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        }
                        break;
                    case 7:
                        var controls = host.Child?.GetVisualDescendants().OfType<TabControl>().Where(t => t.Items.Count > 1).ToArray() ?? [];
                        foreach (var tab in controls) tab.SelectedIndex = (tab.SelectedIndex + 1) % tab.Items.Count;
                        break;
                    case 8:
                        window.OpenCommandPalette();
                        window.FindControl<TextBox>("CommandPaletteSearch")!.Text = new[] { "spell", "", "texture", "no such tool", "class" }[choice % 5];
                        Invoke(window, "CloseCommandPalette");
                        break;
                    case 9:
                        window.OpenClassStudio(); Arrange();
                        var clicks = await browser.ExecuteScriptAsync("(() => { const ids=['spellbookBtn','closeSpellbook','showHelp','dismissHint','freeModeBtn','freeModeBtn','splitViewBtn','splitViewBtn','editModeBtn','editModeBtn']; for(const id of ids)document.getElementById(id).click(); return ids.length; })()");
                        report.EmbeddedClicks += JsonSerializer.Deserialize<int>(clicks);
                        break;
                }
                Arrange();
                // Alternate tight bursts and a real dispatcher turn to expose delayed callbacks after detach.
                if (index % 7 == 0) await Task.Delay(1);
                Require(tabs.Tabs.Any(t => t.Id == tabs.SelectedId), "Selection points to a closed workspace.");
                Require(tabs.Tabs.Select(t => t.Id).Distinct().Count() == tabs.Tabs.Count, "Duplicate workspace tabs accumulated.");
                Require(tabs.Tabs.Count <= open.Length + 2, "Reopening leaked workspace tabs.");
                Require(ReferenceEquals(browser, studio.Browser), "Navigating recreated the embedded editor.");
                timings.Add(clock.Elapsed.TotalMilliseconds);
                report.Completed = index + 1;
                report.Kinds[kind] = report.Kinds.GetValueOrDefault(kind) + 1;
                if ((index + 1) % 250 == 0)
                {
                    report.Samples.Add(Sample(index + 1));
                    Require(await browser.ExecuteScriptAsync("JSON.stringify(stressErrors)") == "\"[]\"", "Embedded JavaScript failed: " + await browser.ExecuteScriptAsync("JSON.stringify(stressErrors)"));
                    Console.WriteLine($"seed {seed}: {index + 1}/{steps}");
                }
            }
            trace.WriteLine("{\"phase\":\"document tools\"}");
            var fixture = Path.GetFullPath(".local/ui-stress/fixture"); Directory.CreateDirectory(fixture);
            WriteSpellFixture(fixture);
            var file = Path.Combine(fixture, "Spell.dbc"); var original = File.ReadAllBytes(file);
            await window.LoadPathAsync(file);
            var documentPages = new Dictionary<string, Control>();
            foreach (var command in new[] { "OpenDbcExportClick", "OpenDbcImportClick" })
            {
                OpenTransfer(command);
                var page = host.Child!; var count = tabs.Tabs.Count;
                documentPages.Add(command, page);
                Field<ComboBox>(page, "_format").SelectedIndex = 2;
                for (var repeat = 0; repeat < 20; repeat++)
                {
                    window.OpenItemWorkbench(); OpenTransfer(command);
                    await Task.Delay(1);
                    Require(ReferenceEquals(page, host.Child) && tabs.Tabs.Count == count, command + " recreated its editor and accumulated tabs on repeat opening.");
                    Require(Field<ComboBox>(page, "_format").SelectedIndex == 2, "Reopening reset document tool choices.");
                }
                Invoke(window, "CloseFeatureWorkspace"); OpenTransfer(command);
                Require(ReferenceEquals(page, host.Child), "Back navigation retired a live document editor.");
            }
            trace.WriteLine("{\"phase\":\"preview bursts\"}");
            var sourceDocument = Field<DbcDocumentSession>(documentPages["OpenDbcExportClick"], "_document");
            var importFile = Path.Combine(fixture, "rows.jsonl");
            DbcRowExportService.Export(sourceDocument.File, sourceDocument.Schema, importFile,
                new(DbcRowExportFormat.JsonLines, sourceDocument.Schema.Columns.Select(c => c.Name).ToArray(), null, false, true));
            Field<TextBox>(documentPages["OpenDbcImportClick"], "_input").Text = importFile;
            foreach (var page in documentPages.Values)
            {
                var previews = Enumerable.Range(0, 30).Select(_ => (Task)page.GetType().GetMethod("PreviewAsync", Private)!.Invoke(page, null)!).ToArray();
                await Task.WhenAll(previews);
                var status = Field<TextBlock>(page, "_status").Text!;
                Require(status.StartsWith("Previewing ") || status.StartsWith("Preview ready "), "Rapid preview clicks failed or left stale status: " + status);
            }
            var importPage = documentPages["OpenDbcImportClick"];
            var pendingPreview = (Task)importPage.GetType().GetMethod("PreviewAsync", Private)!.Invoke(importPage, null)!;
            Field<CheckBox>(importPage, "_append").IsChecked = true;
            await pendingPreview;
            Require(Field<object?>(importPage, "_plan") is null, "Changing import options while previewing restored a stale apply plan.");
            var secondFixture = Path.Combine(fixture, "second"); Directory.CreateDirectory(secondFixture); WriteSpellFixture(secondFixture);
            var secondFile = Path.Combine(secondFixture, "Spell.dbc");
            await window.LoadPathAsync(secondFile);
            var secondPages = new Dictionary<string, Control>();
            foreach (var command in documentPages.Keys)
            {
                OpenTransfer(command); secondPages.Add(command, host.Child!);
                Require(!ReferenceEquals(host.Child, documentPages[command]), "Different documents shared an import/export editor.");
                Require(Field<ComboBox>(host.Child!, "_format").SelectedIndex == 0, "Options leaked across documents.");
            }
            // Apply the first document's no-op import while another table is active.
            // A retained tool tab must never replace the currently selected table's data.
            await (Task)importPage.GetType().GetMethod("PreviewAsync", Private)!.Invoke(importPage, null)!;
            var activeTable = window.FindControl<VirtualDbcView>("DbcView")!;
            var secondDocumentFile = activeTable.File;
            Invoke(importPage, "ApplyPlan");
            Require(ReferenceEquals(activeTable.File, secondDocumentFile), "Applying a retained import redirected the active table to another document.");
            await window.LoadPathAsync(file);
            foreach (var command in documentPages.Keys)
            {
                OpenTransfer(command);
                Require(ReferenceEquals(host.Child, documentPages[command]), "Returning to the first document lost its editor.");
            }
            var closedIds = tabs.Tabs.Where(t => t.Title.StartsWith("Import ") || t.Title.StartsWith("Export ")).Select(t => t.Id).ToArray();
            await (Task)typeof(MainWindow).GetMethod("CloseCurrentDocumentAsync", Private)!.Invoke(window, null)!;
            Arrange();
            foreach (var command in secondPages.Keys)
            {
                OpenTransfer(command);
                Require(ReferenceEquals(host.Child, secondPages[command]), "Closing one document retired the other document's editor.");
            }
            Require(closedIds.Count(id => tabs.Tabs.Any(t => t.Id == id)) == 2, "Closing the document left its import/export tabs alive.");
            Require(Field<System.Collections.IDictionary>(window, "_dbcExportViews").Count == 1 && Field<System.Collections.IDictionary>(window, "_dbcImportViews").Count == 1, "Closed document editors remained cached.");
            await window.LoadPathAsync(file);
            foreach (var command in documentPages.Keys)
            {
                OpenTransfer(command);
                Require(!ReferenceEquals(host.Child, documentPages[command]), "Reopening a closed document revived its disposed editor.");
                Require(!closedIds.Contains(tabs.SelectedId), "A new workspace reused an earlier tab identity.");
            }
            foreach (var editor in open)
            {
                editor.Action(); Arrange();
                Require(ReferenceEquals(host.Child, identities[editor.Name]), "Document cleanup replaced an unrelated editor.");
            }
            // Close the owning document while a preview is still queued, then close the last table.
            OpenTransfer("OpenDbcExportClick");
            var closingPage = host.Child!;
            var closingPreview = (Task)closingPage.GetType().GetMethod("PreviewAsync", Private)!.Invoke(closingPage, null)!;
            await (Task)typeof(MainWindow).GetMethod("CloseCurrentDocumentAsync", Private)!.Invoke(window, null)!;
            await closingPreview;
            await (Task)typeof(MainWindow).GetMethod("CloseCurrentDocumentAsync", Private)!.Invoke(window, null)!;
            Arrange();
            Require(Field<System.Collections.IDictionary>(window, "_dbcExportViews").Count == 0 && Field<System.Collections.IDictionary>(window, "_dbcImportViews").Count == 0, "Closing every table retained document editors.");
            Require(!tabs.Tabs.Any(t => t.Title.StartsWith("Import ") || t.Title.StartsWith("Export ")), "Closed document tools remain in navigation.");
            Require(original.SequenceEqual(File.ReadAllBytes(file)) && original.SequenceEqual(File.ReadAllBytes(secondFile)), "Navigating document tools changed a fixture source.");
            report.DocumentToolsPassed = true;

            void OpenTransfer(string command)
            {
                typeof(MainWindow).GetMethod(command, Private)!.Invoke(window, [null, new RoutedEventArgs()]); Arrange();
            }
            await studio.FlushAsync(); await Task.Delay(250);
            Require(await browser.ExecuteScriptAsync("JSON.stringify(stressErrors)") == "\"[]\"", "Embedded JavaScript errors remained.");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            report.Samples.Add(Sample(steps));
            timings.Sort(); report.P95ActionMs = timings[(int)((timings.Count - 1) * .95)]; report.MaxActionMs = timings[^1];
            window.Close(); await Task.Delay(100);
            Require(studio.Browser is null, "Closing the app left its embedded controller alive.");
        }
        finally { window.Close(); }

        void Arrange(int width = 0, int height = 0)
        {
            if (width > 0) { window.Width = width; window.Height = height; }
            else if (double.IsNaN(window.Width)) { window.Width = 1440; window.Height = 900; }
            window.ApplyTemplate();
            var size = new Size(window.Width, window.Height);
            window.Measure(size); window.Arrange(new Rect(size));
            root.Width = size.Width; root.Height = size.Height;
            // A never-shown test window needs explicit layout after replacing its content.
            foreach (var child in root.GetVisualDescendants().OfType<Control>().Reverse()) child.InvalidateMeasure();
            root.InvalidateMeasure(); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        }
    }

    private static void Click(Button button) { if (button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    private static void Invoke(object target, string method) => target.GetType().GetMethod(method, Private)!.Invoke(target, null);
    private static object Sample(int step)
    {
        using var process = Process.GetCurrentProcess();
        return new { step, managedBytes = GC.GetTotalMemory(false), privateBytes = process.PrivateMemorySize64,
            handles = process.HandleCount, gdi = GetGuiResources(process.Handle, 0), user = GetGuiResources(process.Handle, 1) };
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    private sealed class StressReport
    {
        public int Seed { get; init; }
        public int Requested { get; init; }
        public int Completed { get; set; }
        public int EmbeddedClicks { get; set; }
        public bool Passed { get; set; }
        public bool DocumentToolsPassed { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset CompletedUtc { get; set; }
        public double P95ActionMs { get; set; }
        public double MaxActionMs { get; set; }
        public Dictionary<int, int> Kinds { get; } = [];
        public List<object> Samples { get; } = [];
    }
}
