namespace WoWCrucible.Core;

public sealed record DbcValidationResult(string Path, bool Passed, bool Skipped, string Message, int Rows, int Fields);

public static class DbcCorpusValidator
{
    public static IReadOnlyList<DbcValidationResult> Validate(string schemaPath, string dbcDirectory, bool verifyRoundTrip = true)
    {
        var schema = DbcSchemaCatalog.Load(schemaPath);
        var results = new List<DbcValidationResult>();
        foreach (var path in Directory.EnumerateFiles(dbcDirectory, "*.dbc").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (new FileInfo(path).Length == 0) { results.Add(new(path, true, true, "Empty placeholder", 0, 0)); continue; }
            string? temp = null;
            try
            {
                var dbc = WdbcFile.Load(path);
                var columns = schema.GetColumns(Path.GetFileNameWithoutExtension(path), dbc.FieldCount);
                if (columns.Count != dbc.FieldCount) throw new InvalidDataException($"Schema has {columns.Count} physical columns; file has {dbc.FieldCount}.");
                if (verifyRoundTrip)
                {
                    temp = Path.Combine(Path.GetTempPath(), $"wow-crucible-validate-{Guid.NewGuid():N}.dbc");
                    dbc.Save(temp, false);
                    if (!File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(temp))) throw new InvalidDataException("Unmodified round-trip changed bytes.");
                }
                results.Add(new(path, true, false, "OK", dbc.RowCount, dbc.FieldCount));
            }
            catch (Exception ex) { results.Add(new(path, false, false, ex.Message, 0, 0)); }
            finally { if (temp is not null) File.Delete(temp); }
        }
        return results;
    }
}
