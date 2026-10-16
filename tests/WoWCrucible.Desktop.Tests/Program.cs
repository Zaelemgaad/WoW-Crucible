using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WoWCrucible.Core;
using WoWCrucible.Desktop;

var root = Path.Combine(Path.GetTempPath(), "Crucible-Studio-Host-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    File.WriteAllText(Path.Combine(root, "index.html"), "Class Studio test");
    using (var writer = new BinaryWriter(File.Create(Path.Combine(root, "Spell.dbc"))))
    {
        writer.Write(Encoding.ASCII.GetBytes("WDBC")); writer.Write(1); writer.Write(234); writer.Write(936); writer.Write(1);
        for (var i = 0; i < 234; i++) writer.Write(i == 0 ? 900001 : 0);
        writer.Write((byte)0);
    }
    var schemas = DbcSchemaCatalog.CreateBuiltIn12340();
    using var session = new DesktopWorkspaceSession(new DesktopSettings { CoreDbcPath = root });
    var stateFile = Path.Combine(root, "state.json");
    using var host = new ClassStudioHost(root, f => schemas.ResolveColumns(f.LogicalTableName, f.FieldCount), _ => Task.FromResult<string?>(root), session, stateFile);
    using var http = new HttpClient { BaseAddress = new Uri(new Uri(host.Url), "."), Timeout = TimeSpan.FromMinutes(2) };
    Require(await http.GetStringAsync("index.html") == "Class Studio test", "Frontend was not served.");
    var unauthorized = await http.GetAsync(new Uri(new Uri(host.Url), "/api/settings"));
    Require(unauthorized.StatusCode == HttpStatusCode.Forbidden, "Unguessable route token was bypassed.");
    using (var foreign = new HttpRequestMessage(HttpMethod.Post, "api/state"))
    {
        foreign.Headers.Add("Origin", "https://example.org"); foreign.Content = JsonContent.Create(new { });
        Require((await http.SendAsync(foreign)).StatusCode == HttpStatusCode.Forbidden, "Cross-origin mutation accepted.");
    }
    var state = new Dictionary<string, string> { ["twc.workspace"] = "{\"trees\":[]}" };
    Require((await http.PostAsJsonAsync("api/state", state)).IsSuccessStatusCode, "Native state save failed.");
    Require((await http.GetFromJsonAsync<Dictionary<string, string>>("api/state"))!["twc.workspace"] == state["twc.workspace"], "Native state restore failed.");
    Require((await http.PostAsJsonAsync("api/state", new Dictionary<string, string> { ["foreign"] = "x" })).StatusCode == HttpStatusCode.BadRequest, "Unowned state keys accepted.");
    Require((await http.PostAsJsonAsync("api/pick?kind=server", new { })).IsSuccessStatusCode, "Folder callback failed.");
    var load = await http.PostAsJsonAsync("api/load", new { serverPath = root, clientPath = "", includeSql = false, clientOrder = "CoA" });
    Require(load.IsSuccessStatusCode, await load.Content.ReadAsStringAsync());
    var row = await http.GetFromJsonAsync<JsonElement>("api/row?layer=server&table=Spell&id=900001");
    Require(row.GetProperty("ID").GetInt32() == 900001 && row.TryGetProperty("ManaCost", out _), "Full schema row transport failed.");
    Console.WriteLine("PASS native Class Studio host: assets, token/origin checks, native persistence, folder callback, source loading and full spell records.");
}
finally { Directory.Delete(root, true); }
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
