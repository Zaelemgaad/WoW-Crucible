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
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(8));
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
            Require((await Evaluate("StudioSources.ready.then(()=>document.querySelector('#realmSource').value)")).GetString()=="builtin", "Fresh editor forced a realm instead of using the reference workspace.");
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
                workspace.Settings.ServerRootPath = settings.ServerRootPath;
                workspace.Settings.CoreDbcPath = settings.CoreDbcPath;
                workspace.Settings.ClientDataPath = settings.ClientDataPath;
                workspace.Settings.CoreSourcePath = settings.CoreSourcePath;
                var realms = await Evaluate("StudioBridge.api('settings').then(s=>s.realms)");
                Require(realms.GetArrayLength() == 5, "Expected all five installed realms.");
                Require(!realms.GetRawText().Contains("password", StringComparison.OrdinalIgnoreCase), "Realm metadata exposed credentials.");
                Console.WriteLine("PASS installed realm discovery: " + realms.GetRawText());
                var request = JsonSerializer.Serialize(new { serverPath = settings.CoreDbcPath, clientPath = settings.ClientDataPath, corePath = settings.CoreSourcePath, clientOrder = "CoA", realmId = 1, includeSql = true });
                var coverage = await Evaluate("StudioBridge.api('load'," + request + ").then(s=>{window.liveCoASnapshot=s;return {icons:Object.keys(s.classIcons||{}).length,tables:s.tables.map(t=>({layer:t.layer,name:t.name,rows:t.rows.length})),notices:s.notices};})");
                Require(coverage.GetProperty("tables").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "Spell" && t.GetProperty("rows").GetInt32() > 200000), "Live CoA catalog did not reach the embedded editor.");
                Require(coverage.GetProperty("tables").EnumerateArray().Any(t => t.GetProperty("layer").GetString() == "sql"), "Selected realm SQL was not loaded.");
                Require(coverage.GetProperty("icons").GetInt32()==32, "Client class icons were not resolved for all 32 classes: " + coverage.GetRawText());
                var classes = await Evaluate("(() => {const s=window.liveCoASnapshot;if(document.querySelector('#freeModeBtn').classList.contains('on'))document.querySelector('#freeModeBtn').click(); const classes=StudioData.realmClasses(s,'server').map(c=>Object.assign(StudioData.talentClass(s,'server',c.ID),{iconSrc:s.classIcons[c.ID]}));TalentStudio.useSource('live-test',classes); return classes.map(c=>({id:c.classId,name:c.name,boards:c.trees.length,talents:c.trees.reduce((n,t)=>n+t.t.length,0)}));})()");
                Require(classes.GetArrayLength() == 21, "CoA did not expose its 21 custom classes.");
                Require(classes.EnumerateArray().All(c=>c.GetProperty("boards").GetInt32()>0), "A CoA class has no source talent boards.");
                Console.WriteLine("PASS live CoA classes, boards and selected world SQL: " + classes.GetRawText());
                Require((await Evaluate("(async()=>{const icons=[...document.querySelectorAll('#pane0 .class-btn-wrap:not(:has(.class-edit-btn)) img')];await Promise.all(icons.map(i=>i.decode()));return icons.length===21&&icons.every(i=>i.src.startsWith('data:image/png')&&i.naturalWidth>0);})()")).GetBoolean(), "CoA class buttons did not render their client icons.");
                await using(var iconCapture=File.Create(Path.Combine(root,"coa-class-icons.png")))
                    await browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,iconCapture);
                Require((await Evaluate("StudioBridge.api('tooltip',{layer:'server',spellId:1587725,text:StudioData.rows(liveCoASnapshot,'server','Spell').find(s=>s.ID===1587725)['Description[enUS]']}).then(r=>r.notices.length===0 && r.text.includes('148 + 16% of spell power + 12% of attack power') && !r.text.includes('@s:'))")).GetBoolean(), "Actual tooltip renderer did not resolve Poison Rain.");
                Require((await Evaluate("(async()=>{const c=StudioData.talentClass(liveCoASnapshot,'server',12);if(c.trees.map(t=>t.n).join('|')!=='Headhunting|Brutality|Ancestry')return false;const names=[...new Set(c.trees.flatMap(t=>t.t).concat(c.sideboard.t).map(t=>t.iconname))];const icons=await StudioBridge.api('icons',names);return names.every(n=>icons[n]?.startsWith('data:image/png'));})()")).GetBoolean(), "Barbarian spec names or actual talent textures failed.");
                Console.WriteLine("PASS Barbarian source spec names and every talent/ability icon; native Poison Rain rendering.");
                var allIcons=await Evaluate("(async()=>{const s=liveCoASnapshot;const classes=StudioData.realmClasses(s,'server').map(c=>StudioData.talentClass(s,'server',c.ID));const names=[...new Set(classes.flatMap(c=>c.trees.flatMap(t=>t.t).concat(c.sideboard.t)).map(t=>t.iconname))];let missing=[];for(let i=0;i<names.length;i+=512){const batch=names.slice(i,i+512),icons=await StudioBridge.api('icons',batch);missing.push(...batch.filter(n=>!icons[n]));}return {count:names.length,missing};})()");
                Require(allIcons.GetProperty("missing").GetArrayLength()==0,"Missing source talent icons: "+allIcons.GetRawText());
                Console.WriteLine("PASS all CoA talent/ability textures: "+allIcons.GetRawText());
                Require((await Evaluate("(async()=>{const raw=StudioData.rows(liveCoASnapshot,'server','Spell').find(s=>s.ID===1587725)['Description[enUS]'];TalentStudio.importClass({classId:900002,name:'Tooltip probe',trees:[{n:'Probe',t:[{i:900002,n:'Poison Rain',m:1,s:[1587725],d:[raw],sourceLayer:'server',iconname:'spell_nature_corrosivebreath',x:0,y:0}]}]});const cell=document.querySelector('[data-talent-id=\"900002\"]');cell.dispatchEvent(new MouseEvent('mouseenter',{clientX:300,clientY:300}));for(let i=0;i<100;i++){await new Promise(r=>setTimeout(r,20));const text=document.querySelector('#tooltip').textContent;if(text.includes('148 + 16% of spell power'))return !text.includes('@s:')&&!text.includes('${')&&!text.includes('|c');}return false;})()")).GetBoolean(), "Actual hovered Poison Rain tooltip failed.");
                await Evaluate("(() => {window.liveCoASnapshot=null;return true;})()");
                foreach (var realmId in new[] { 2, 3, 4, 5 })
                {
                    var realmRequest = JsonSerializer.Serialize(new { clientPath = settings.ClientDataPath, corePath = settings.CoreSourcePath, clientOrder = "CoA", realmId, includeSql = true });
                    var summary = await Evaluate("StudioBridge.api('load'," + realmRequest + ").then(s=>({name:s.realmName,profile:s.realmProfile,notices:s.notices,sql:s.tables.filter(t=>t.layer==='sql').map(t=>t.source),classes:StudioData.realmClasses(s,'server').map(c=>{const p=StudioData.talentClass(s,'server',c.ID);return {id:p.classId,name:p.name,names:p.trees.map(t=>t.n),boards:p.trees.length,imported:p.trees.flatMap(t=>t.t).filter(t=>t.i>=47000&&t.i<=47999).length};})}))");
                    Require(summary.GetProperty("sql").GetArrayLength()>0, "Realm SQL was not loaded: " + summary.GetRawText());
                    Require(summary.GetProperty("sql").EnumerateArray().All(t=>t.GetString()!.StartsWith("acore_" + (realmId==2?"s9":realmId==3?"s10":realmId==4?"reborn":"remix") + "_world.")), "Wrong realm SQL loaded.");
                    if(realmId==4) Require(summary.GetProperty("classes").EnumerateArray().All(c=>c.GetProperty("imported").GetInt32()==0), "Bronzebeard contains Remix entries.");
                    if(realmId==5) Require(summary.GetProperty("classes").GetArrayLength()==9 && summary.GetProperty("classes").EnumerateArray().Single(c=>c.GetProperty("id").GetInt32()==2).GetProperty("imported").GetInt32()>0, "Remix policy was not applied.");
                    Console.WriteLine("PASS realm source isolation: " + summary.GetProperty("name") + " / " + summary.GetProperty("classes").GetRawText());
                }
            }
            if(coa) {
                await Evaluate("(() => {localStorage.setItem('twc.panes',JSON.stringify({panes:[2,null],splitView:false}));localStorage.setItem('twc.studio.sources',JSON.stringify({realmId:5,selection:'5',includeSql:false}));const settings=JSON.parse(localStorage.getItem('twc.settings'));settings.freeMode=false;localStorage.setItem('twc.settings',JSON.stringify(settings));StudioBridge.changed();return true;})()");
                await view.FlushAsync();
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
            if(coa) {
                var startup=await Evaluate("StudioSources.ready.then(()=>({status:document.querySelector('.studio-source-status').textContent,selected:document.querySelector('#realmSource').value,classes:document.querySelectorAll('#pane0 .class-btn-wrap:not(:has(.class-edit-btn)) .class-btn').length,active:document.querySelector('#pane0 .class-btn.active')?.textContent,boards:document.querySelectorAll('#pane0 .spec-board').length,icons:[...document.querySelectorAll('#pane0 .class-btn-wrap:not(:has(.class-edit-btn)) img')].every(i=>i.src.startsWith('data:image/png')&&i.complete&&i.naturalWidth>0)}))");
                Require(startup.GetProperty("selected").GetString()=="5" && startup.GetProperty("classes").GetInt32()==9 && startup.GetProperty("icons").GetBoolean() && startup.GetProperty("boards").GetInt32()==3 && startup.GetProperty("active").GetString()=="Templar", "Automatic realm load failed: " + startup.GetRawText());
                Require((await Evaluate("(async()=>{await StudioIcons.ready();const icons=[...document.querySelectorAll('#pane0 img[data-client-icon]')];await Promise.all(icons.map(i=>i.decode()));return icons.length>=71&&icons.every(i=>i.dataset.iconStatus==='ready'&&i.src.startsWith('data:image/png')&&i.naturalWidth>0);})()")).GetBoolean(), "Remix talent icons did not finish rendering from the selected client.");
                Console.WriteLine("PASS embedded restart restores last realm (Remix), class selection and real client icons: " + startup.GetRawText());
                await using var sourceCapture=File.Create(Path.Combine(root,"coa-startup.png"));
                await browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,sourceCapture);
            }
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
