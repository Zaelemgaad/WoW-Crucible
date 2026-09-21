using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WoWCrucible.Core;

internal static class DbcBatchExportTestSuite
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Crucible-batch-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var columns = new DbcColumn[]
        {
            new(0, 0, 4, "ID", DbcValueType.Int32, true),
            new(1, 4, 4, "Name", DbcValueType.StringOffset),
            new(2, 8, 4, "Value", DbcValueType.Float32)
        };
        var schema = new DbcSchemaResolution(columns, DbcSchemaMatchKind.NamedMatch, 3, DbcRecordKeyStrategy.Physical(0));
        try
        {
            var paths = Enumerable.Range(0, 125).Select(index => Fixture(Path.Combine(root, $"Folder {index}"), "Example.dbc")).ToArray();
            var before = paths.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            var csv = DbcBatchExportService.Export(paths.Append(paths[0]), DbcRowExportFormat.Csv, _ => schema);
            Require(csv.Exported == 125 && csv.Items.Count == 125 && csv.Failed == 0, "Large selection or duplicate-input handling failed.");
            Require(csv.Items.All(item => item.OutputPath == Path.ChangeExtension(item.SourcePath, ".csv") && item.Rows == 2), "Output names or row counts changed.");
            Require(File.ReadAllText(csv.Items[0].OutputPath).Contains("\"A, \"\"quoted\"\"\r\nname\",1.25"), "CSV quoting, strings, or floating-point values were lost.");
            var json = DbcBatchExportService.Export(paths.Take(2), DbcRowExportFormat.Json, _ => schema);
            using (var parsed = JsonDocument.Parse(File.ReadAllText(json.Items[0].OutputPath)))
            {
                Require(parsed.RootElement[0].GetProperty("Name").GetString() == "A, \"quoted\"\r\nname", "JSON did not decode strings.");
                Require(parsed.RootElement[0].GetProperty("Value").GetDouble() == 1.25, "JSON number typing failed.");
                Require(parsed.RootElement.GetArrayLength() == 2, "JSON lost rows.");
            }
            var oldOutput = File.ReadAllBytes(csv.Items[0].OutputPath);
            var skipped = DbcBatchExportService.Export([paths[0]], DbcRowExportFormat.Csv, _ => throw new Exception("Existing files must not be read."));
            Require(skipped.Skipped == 1 && File.ReadAllBytes(csv.Items[0].OutputPath).SequenceEqual(oldOutput), "Existing output was replaced.");

            var collisionDbc = Fixture(Path.Combine(root, "Collision"), "Example.dbc");
            var collisionDb2 = Fixture(Path.Combine(root, "Collision"), "Example.db2");
            var collision = DbcBatchExportService.Export([collisionDbc, collisionDb2], DbcRowExportFormat.Csv, _ => schema);
            Require(collision.Failed == 2 && !File.Exists(Path.ChangeExtension(collisionDbc, ".csv")), "Same-basename DBC/DB2 collision was not rejected.");

            var broken = Path.Combine(root, "Bad.dbc"); File.WriteAllText(broken, "not a DBC");
            var unknown = Fixture(root, "Unknown.dbc");
            var valid = Fixture(root, "Valid.dbc");
            var mixed = DbcBatchExportService.Export([broken, unknown, valid], DbcRowExportFormat.Json,
                file => file.LogicalTableName == "Unknown" ? DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns("Unknown", 3) : schema);
            Require(mixed.Failed == 2 && mixed.Exported == 1 && !File.Exists(Path.ChangeExtension(unknown, ".json")), "A bad file stopped the batch or a guessed schema was exported.");

            var cancelledSource = Fixture(Path.Combine(root, "Cancel"), "Example.dbc");
            using var cancellation = new CancellationTokenSource();
            var progress = new CancelProgress(cancellation);
            var cancelled = DbcBatchExportService.Export([cancelledSource, valid], DbcRowExportFormat.Csv, _ => schema, progress, cancellation.Token);
            Require(cancelled.Cancelled == 2 && !File.Exists(Path.ChangeExtension(cancelledSource, ".csv")), "Cancellation left a partial export or continued into another file.");
            Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "An export temporary file was leaked.");
            Require(before.All(pair => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "Export modified an input file.");
            var badFormatRejected = false;
            try { DbcBatchExportService.Export([valid], (DbcRowExportFormat)100, _ => schema); }
            catch (ArgumentOutOfRangeException) { badFormatRejected = true; }
            Require(badFormatRejected, "Invalid export format was accepted.");
            Console.WriteLine("PASS Explorer batch exports: 125-file selection, CSV/JSON values, source hashes, collisions, existing outputs, malformed/unknown schemas, and cancellation.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Fixture(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var strings = Encoding.UTF8.GetBytes("\0A, \"quoted\"\r\nname\0");
        writer.Write(Encoding.ASCII.GetBytes("WDBC")); writer.Write(2); writer.Write(3); writer.Write(12); writer.Write(strings.Length);
        writer.Write(17); writer.Write(1); writer.Write(1.25f);
        writer.Write(18); writer.Write(0); writer.Write(-3.5f);
        writer.Write(strings);
        return path;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<DbcBatchExportProgress>
    {
        public void Report(DbcBatchExportProgress progress) { if (progress.CompletedRows > 0) cancellation.Cancel(); }
    }
}
