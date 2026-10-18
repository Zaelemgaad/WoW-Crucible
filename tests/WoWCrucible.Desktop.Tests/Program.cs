using System.Text;
using System.Text.Json;
using WoWCrucible.Core;
using WoWCrucible.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--ui-stress")
        {
            UiStressTestSuite.Run(args.Length > 1 ? int.Parse(args[1]) : 180026, args.Length > 2 ? int.Parse(args[2]) : 2000);
            return;
        }
        if (args.Contains("--workspace-shell")) { WorkspaceShellTestSuite.Run(); return; }
        if (args.Contains("--embedded-studio")) { EmbeddedStudioTestSuite.Run(args.Contains("--coa")); return; }
        TestSessionAsync().GetAwaiter().GetResult();
    }

    private static async Task TestSessionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Crucible-Studio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WriteSpellFixture(root);
            var schemas = DbcSchemaCatalog.CreateBuiltIn12340();
            using var workspace = new DesktopWorkspaceSession(new DesktopSettings { CoreDbcPath = root });
            var stateFile = Path.Combine(root, "state.json");
            string? exported = null;
            using var session = new ClassStudioSession(f => schemas.ResolveColumns(f.LogicalTableName, f.FieldCount),
                _ => Task.FromResult<string?>(root), (name, text) => { exported = name + ":" + text; return Task.FromResult(true); }, workspace, stateFile);
            var state = new Dictionary<string, string> { ["twc.workspace"] = "{\"trees\":[]}" };
            await session.InvokeAsync("state", JsonSerializer.SerializeToElement(state));
            Require(((Dictionary<string, string>)(await session.InvokeAsync("state"))!)["twc.workspace"] == state["twc.workspace"], "Native state restore failed.");
            await Reject("state", new Dictionary<string, string> { ["foreign"] = "x" });
            Require(File.ReadAllText(stateFile).Contains("twc.workspace"), "Rejected state replaced existing work.");
            var pick = JsonSerializer.SerializeToElement(await session.InvokeAsync("pick?kind=server", JsonSerializer.SerializeToElement(new { })));
            Require(pick.GetProperty("path").GetString() == root, "Folder callback failed.");
            await session.InvokeAsync("load", JsonSerializer.SerializeToElement(new { serverPath = root, clientPath = "", includeSql = false, clientOrder = "CoA" }));
            var row = JsonSerializer.SerializeToElement(await session.InvokeAsync("row?layer=server&table=Spell&id=900001"));
            Require(row.GetProperty("ID").GetInt32() == 900001 && row.TryGetProperty("ManaCost", out _), "Full schema row transfer failed.");
            await session.InvokeAsync("export", JsonSerializer.SerializeToElement(new { name = "spells.csv", text = "ID,ManaCost\n900001,321" }));
            Require(exported == "spells.csv:ID,ManaCost\n900001,321", "Native export changed CSV content.");
            await Reject("export", new { name = "../bad.csv", text = "x" });
            await Reject("unknown", new { });
            Console.WriteLine("PASS direct Class Studio session: state round-trip and rejection, folder callback, CoA source loading, full spell rows and native exports.");

            async Task Reject(string route, object body)
            {
                try { await session.InvokeAsync(route, JsonSerializer.SerializeToElement(body)); }
                catch (InvalidDataException) { return; }
                throw new InvalidOperationException("Invalid request was accepted: " + route);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    internal static void WriteSpellFixture(string root)
    {
        using var writer = new BinaryWriter(File.Create(Path.Combine(root, "Spell.dbc")));
        writer.Write(Encoding.ASCII.GetBytes("WDBC")); writer.Write(1); writer.Write(234); writer.Write(936); writer.Write(1);
        for (var i = 0; i < 234; i++) writer.Write(i == 0 ? 900001 : 0);
        writer.Write((byte)0);
    }

    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
