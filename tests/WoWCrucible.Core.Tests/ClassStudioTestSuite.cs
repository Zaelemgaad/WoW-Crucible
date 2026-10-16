using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WoWCrucible.Core;

internal static class ClassStudioTestSuite
{
    public static async Task RunAsync(string? corpus = null, string? output = null, string? client = null, string? core = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "Crucible-ClassStudio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = DbcSchemaCatalog.CreateBuiltIn12340();
            var service = new ClassStudioDataService(f => catalog.ResolveColumns(f.LogicalTableName, f.FieldCount));
            var path = Path.Combine(root, "Spell.dbc");
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                var strings = Encoding.UTF8.GetBytes("\0Test, \"spell\"\nname\0");
                writer.Write(Encoding.ASCII.GetBytes("WDBC")); writer.Write(1); writer.Write(234); writer.Write(234 * 4); writer.Write(strings.Length);
                for (var i = 0; i < 234; i++) writer.Write(i == 0 ? 900001u : i == 136 ? 1u : 0u);
                writer.Write(strings);
            }
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            var snapshot = await service.LoadAsync(root, root, Path.Combine(root, "cache"));
            Require(snapshot.Tables.Count == 2, "Server/client layers were merged or lost.");
            Require(snapshot.Notices.Any(n => n.StartsWith("SQL is not connected")), "Incomplete learn data was not disclosed.");
            var detail = service.ReadRow("server", "Spell", 900001);
            Require(detail.Count == 236 && (string)detail["Name[enUS]"]! == "Test, \"spell\"\nname", "Full spell row or decoded strings lost.");
            Require(!snapshot.Tables[0].Rows[0].ContainsKey("ManaCost"), "Spell catalog unnecessarily includes every field.");
            var changed = new Dictionary<string, object?>(detail) { ["ManaCost"] = 321u };
            var editFile = Path.Combine(root, "edit.json"); File.WriteAllText(editFile, JsonSerializer.Serialize(new[] { changed }));
            var dbc = WdbcFile.Load(path); var schema = catalog.ResolveColumns("Spell", 234);
            var plan = DbcRowImportService.Preview(dbc, schema, editFile, new(DbcRowImportFormat.Json));
            Require(plan.UpdatedRows == 1 && plan.Changes.Any(c => c.Column == "ManaCost" && c.After == "321"), "Studio row is not compatible with Crucible structured import.");
            Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Reading or preview modified a source.");
            var bad = new ClassStudioDataService(f => catalog.ResolveColumns("Unknown", f.FieldCount));
            try { await bad.LoadAsync(root, null, root); throw new Exception("Guessed schema was accepted."); }
            catch (InvalidDataException) { }
            var packedPath = Path.Combine(root, "CharacterAdvancement.dbc");
            var packed = new byte[20 + 692 + 1];
            "WDBC"u8.CopyTo(packed);
            void Word(int offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(offset, 4), value);
            Word(4, 1); Word(8, 179); Word(12, 692); Word(16, 1); Word(20, 900001); Word(20 + 20, 800157);
            packed[20 + 414] = 1; Word(20 + 415, 900002); packed[20 + 483] = 1;
            File.WriteAllBytes(packedPath, packed);
            var coa = CoAClassStudioSource.ReadTable(packedPath, "server", packedPath);
            Require((uint)coa.Rows[0]["ID"]! == 900001 && (uint)coa.Rows[0]["ConnectedEntry[0]"]! == 900002 && (uint)coa.Rows[0]["Starting"]! == 1,
                "CoA wide IDs, packed byte flags or unaligned fields were truncated.");
            Require(coa.Format == "coa-plan" && coa.Columns.Single(c => c.Name == "Starting").ByteOffset == 414, "Packed plan lost its byte-offset contract.");
            Word(12, 716); File.WriteAllBytes(packedPath, packed);
            try { CoAClassStudioSource.ReadTable(packedPath, "server", packedPath); throw new Exception("Wrong packed CoA layout accepted."); }
            catch (InvalidDataException) { }
            if (corpus is not null)
            {
                var real = await service.LoadAsync(corpus, client, Path.Combine(output ?? root, "client-cache"), corePath: core, clientOrder: core is null ? ClientArchiveOrderPolicy.StandardWrath : ClientArchiveOrderPolicy.CoA);
                if (client is not null) Require(real.Tables.Any(t => t.Layer == "client" && t.Name == "Spell"), "Effective client Spell.dbc was not resolved.");
                Require(real.Tables.Any(t => t.Name == "Spell" && t.Rows.Count > 1), "Live corpus did not load.");
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                    File.WriteAllText(Path.Combine(output, "catalog.json"), JsonSerializer.Serialize(real, json));
                    var spell = real.Tables.Single(t => t.Name == "Spell" && t.Layer == "server").Rows[0];
                    File.WriteAllText(Path.Combine(output, "spell.json"), JsonSerializer.Serialize(service.ReadRow("server", "Spell", Convert.ToUInt32(spell["ID"])), json));
                    var csvPath = Path.Combine(output, "edited-spell.csv");
                    if (File.Exists(csvPath))
                    {
                        var original = WdbcFile.Load(Path.Combine(ClassStudioDataService.FindTableDirectory(corpus)!, "Spell.dbc"));
                        var csvPlan = DbcRowImportService.Preview(original, catalog.ResolveColumns("Spell", original.FieldCount), csvPath, new(DbcRowImportFormat.Csv, AllowAppend: true));
                        Require(csvPlan.InputRows > 0 && csvPlan.Changes.Any(c => c.Column == "ManaCost" && c.After == "321"), "Browser CSV did not import through Crucible's real parser.");
                        Console.WriteLine("PASS browser-produced CSV through native DBC import preview; source unchanged.");
                    }
                }
                Console.WriteLine($"PASS live read: {real.Tables.Count} tables, {real.Tables.Single(t => t.Name == "Spell" && t.Layer == "server").Rows.Count} server spells.");
                foreach (var table in real.Tables.Where(t => t.Format.EndsWith("-plan"))) Console.WriteLine($"  {table.Layer}/{table.Name}: {table.Rows.Count} records");
            }
            Console.WriteLine("PASS Class Studio: explicit layers, decoded full records, structured import, source immutability, missing SQL coverage and schema rejection.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
