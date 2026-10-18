using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using WoWCrucible.Core;
using WoWCrucible.Desktop;
using WoWCrucible.Desktop.Controls;
using static Program;

internal static class EmbeddedStudioTestSuite
{
    public static void Run(bool coa)
    {
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Exception? failure = null;
        var completed = false;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await CheckAsync(coa); completed = true; }
            catch (Exception error) { failure = error; }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (failure is not null) throw new InvalidOperationException("Embedded Class Studio test failed.", failure);
        Require(completed, "Embedded Class Studio test timed out.");
    }

    private static async Task CheckAsync(bool coa)
    {
        var root = Path.GetFullPath(".local/embedded-studio-test");
        Directory.CreateDirectory(root);
        WriteSpellFixture(root);
        var statePath = Path.Combine(root, "state.json");
        File.WriteAllText(statePath, JsonSerializer.Serialize(new Dictionary<string, string> { ["twc.test.saved"] = "123" }));
        var schemas = DbcSchemaCatalog.CreateBuiltIn12340();
        using var workspace = new DesktopWorkspaceSession(new DesktopSettings { CoreDbcPath = root });
        string? export = null;
        var session = new ClassStudioSession(f => schemas.ResolveColumns(f.LogicalTableName, f.FieldCount),
            _ => Task.FromResult<string?>(root), (name, text) => { export = name + ":" + text; return Task.FromResult(true); }, workspace, statePath);
        using var view = new ClassStudioView(Path.GetFullPath("src/WoWCrucible.Desktop/bin/Debug/net10.0/ClassStudio"), session, Path.Combine(root, "WebView"));
        var rootPanel = new Grid { Children = { view } };
        var window = new Window { Width = 1400, Height = 900, ShowActivated = false, ShowInTaskbar = false, Content = rootPanel };
        try
        {
            // A real HWND and message loop, without showing or focusing a test window.
            window.ApplyTemplate(); Arrange();
            await view.Ready.WaitAsync(TimeSpan.FromSeconds(25));
            var importedPath = Path.Combine(root, "open-talents.json");
            await File.WriteAllTextAsync(importedPath, "{\"classId\":\"open-file-test\",\"name\":\"File open fixture\",\"trees\":[{\"n\":\"Imported tree\",\"t\":[]}]}");
            await view.ImportFileAsync(importedPath);
            var browser = view.Browser!;
            var restored = await Evaluate("StudioBridge.ready.then(() => ({connected:StudioBridge.connected,saved:localStorage.getItem('twc.test.saved')}))");
            Require(restored.GetProperty("connected").GetBoolean() && restored.GetProperty("saved").GetString() == "123", "Existing desktop state was not restored.");
            Require((await Evaluate("StudioBridge.api('settings')")).GetProperty("serverPath").GetString() == root, "Desktop workspace settings were not shared.");
            Require((await Evaluate("StudioBridge.api('pick?kind=server',{})")).GetProperty("path").GetString() == root, "Native folder picker bridge failed.");
            await Evaluate("StudioBridge.api('load'," + JsonSerializer.Serialize(new { serverPath = root, clientOrder = "CoA" }) + ")");
            Require((await Evaluate("StudioBridge.api('row?layer=server&table=Spell&id=900001')")).GetProperty("ID").GetInt32() == 900001, "Native full row request failed.");
            await Evaluate("StudioBridge.download('spells.csv','ID,ManaCost\\n900001,321')");
            Require(export == "spells.csv:ID,ManaCost\n900001,321", "Native CSV export failed.");
            var rejected = await Evaluate("StudioBridge.api('state',{foreign:'bad'}).then(()=>false,()=>true)");
            Require(rejected.GetBoolean(), "Invalid native state was accepted.");
            Require((await Evaluate("JSON.parse(localStorage.getItem('twc.class.open-file-test') || 'null') !== null")).GetBoolean(), "Native talent-file opening did not import its contents.");
            var layoutResult = await Evaluate(File.ReadAllText("tests/WoWCrucible.Desktop.Tests/workspace-layout-regression.js"));
            Console.WriteLine("PASS editor layout: " + layoutResult.GetRawText());
            await Evaluate("(() => { document.querySelector('#freeModeBtn').click(); window.retentionProbe=47; return true; })()");
            rootPanel.Children.Clear(); rootPanel.Children.Add(new TextBlock { Text = "Other workspace" });
            await Task.Delay(300);
            rootPanel.Children.Clear(); rootPanel.Children.Add(view);
            Arrange();
            await Task.Delay(100);
            Require(ReferenceEquals(browser, view.Browser) && (await Evaluate("window.retentionProbe")).GetInt32() == 47, "Tab switching recreated the page.");
            view.SetOccluded(true);
            await Task.Delay(20);
            Require(ReferenceEquals(browser, view.Browser), "A native overlay discarded the editor instance.");
            view.SetOccluded(false);
            await Evaluate("(() => { localStorage.setItem('twc.test.last','456'); StudioBridge.changed(); return true; })()");
            await view.FlushAsync();
            Require(JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(statePath))!["twc.test.last"] == "456", "Closing inside the autosave delay lost the last edit.");
            browser.Navigate("https://example.org/");
            await Task.Delay(100);
            Require(browser.Source == "https://class-studio.crucible.invalid/index.html", "Untrusted navigation was not blocked.");
            if (coa)
            {
                var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText("src/WoWCrucible.Desktop/bin/Debug/net10.0/Config/desktop.json"))!;
                var request = JsonSerializer.Serialize(new { serverPath = settings.CoreDbcPath, clientPath = settings.ClientDataPath, corePath = settings.CoreSourcePath, clientOrder = "CoA" });
                var coverage = await Evaluate("StudioBridge.api('load'," + request + ").then(s=>s.tables.map(t=>({layer:t.layer,name:t.name,rows:t.rows.length})))");
                Require(coverage.EnumerateArray().Any(t => t.GetProperty("name").GetString() == "Spell" && t.GetProperty("rows").GetInt32() > 200000), "Live CoA catalog did not reach the embedded editor.");
                Console.WriteLine("PASS live CoA catalog through the real embedded bridge: " + coverage.GetRawText());
            }
            await using (var capture = File.Create(Path.Combine(root, "embedded.png")))
                await browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
            rootPanel.Children.Clear();
            view.Dispose();
            var resumedSession = new ClassStudioSession(f => schemas.ResolveColumns(f.LogicalTableName, f.FieldCount),
                _ => Task.FromResult<string?>(root), (_, _) => Task.FromResult(false), workspace, statePath);
            using var resumed = new ClassStudioView(Path.GetFullPath("src/WoWCrucible.Desktop/bin/Debug/net10.0/ClassStudio"), resumedSession, Path.Combine(root, "WebView"));
            rootPanel.Children.Add(resumed);
            Arrange();
            await resumed.Ready.WaitAsync(TimeSpan.FromSeconds(25));
            browser = resumed.Browser!;
            Require((await Evaluate("StudioBridge.ready.then(()=>localStorage.getItem('twc.test.last'))")).GetString() == "456", "Restart did not restore the last edit.");
            Console.WriteLine("PASS real embedded WebView: local assets, existing state, workspace settings, folders, source/row reads, CSV export, validation, retained tabs, immediate shutdown save, blocked external navigation, restart restoration and native capture. No listener or browser launch.");

            void Arrange()
            {
                window.Measure(new Size(1400, 900)); window.Arrange(new Rect(0, 0, 1400, 900));
                rootPanel.Measure(new Size(1400, 900)); rootPanel.Arrange(new Rect(0, 0, 1400, 900));
                rootPanel.UpdateLayout();
            }

            async Task<JsonElement> Evaluate(string expression)
            {
                await browser.ExecuteScriptAsync("window.__probe=null; Promise.resolve().then(()=> (" + expression + ")).then(value=>window.__probe={value},e=>window.__probe={error:e.message});");
                for (var attempt = 0; attempt < 5000; attempt++)
                {
                    var value = await browser.ExecuteScriptAsync("window.__probe");
                    if (value != "null")
                    {
                        using var document = JsonDocument.Parse(value);
                        if (document.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
                        return document.RootElement.GetProperty("value").Clone();
                    }
                    await Task.Delay(20);
                }
                throw new TimeoutException("Native script did not complete: " + expression);
            }
        }
        finally { rootPanel.Children.Clear(); window.Close(); }
    }
}
