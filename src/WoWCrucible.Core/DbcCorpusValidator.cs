namespace WoWCrucible.Core;

public sealed record DbcValidationResult(string Path, bool Passed, bool Skipped, bool Warning, string Message, int Rows, int Fields);

public static class DbcCorpusValidator
{
    public static IReadOnlyList<DbcValidationResult> Validate(string schemaPath, string dbcDirectory, bool verifyRoundTrip = true, bool recursive = false)
    {
        dbcDirectory = Path.GetFullPath(dbcDirectory);
        if (!Directory.Exists(dbcDirectory)) throw new DirectoryNotFoundException($"DBC validation directory does not exist: {dbcDirectory}");
        var schema = DbcSchemaCatalog.Load(schemaPath);
        var results = new List<DbcValidationResult>();
        var paths = Directory.EnumerateFiles(dbcDirectory, "*.dbc", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
            throw new InvalidDataException($"No DBC files were found in '{dbcDirectory}' ({(recursive ? "recursive" : "top directory only")}).{(recursive ? string.Empty : " Point to the DBC directory or use --recursive.")}");
        foreach (var path in paths)
        {
            if (new FileInfo(path).Length == 0) { results.Add(new(path, true, true, false, "Empty placeholder", 0, 0)); continue; }
            string? temp = null;
            try
            {
                var dbc = WdbcFile.Load(path);
                var resolution = schema.ResolveColumns(Path.GetFileNameWithoutExtension(path), dbc.FieldCount);
                if (verifyRoundTrip)
                {
                    temp = Path.Combine(Path.GetTempPath(), $"wow-crucible-validate-{Guid.NewGuid():N}.dbc");
                    dbc.Save(temp, false);
                    if (!File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(temp))) throw new InvalidDataException("Unmodified round-trip changed bytes.");
                }
                var message = resolution.MatchKind switch
                {
                    DbcSchemaMatchKind.NamedMatch => "Named schema matched",
                    DbcSchemaMatchKind.MissingTableFallback => "Raw fallback: named table is missing from schema",
                    _ => $"Raw fallback: schema defines {resolution.DefinedFieldCount} fields, file has {dbc.FieldCount}"
                };
                results.Add(new(path, true, false, resolution.UsedFallback, message, dbc.RowCount, dbc.FieldCount));
            }
            catch (Exception ex) { results.Add(new(path, false, false, false, ex.Message, 0, 0)); }
            finally { if (temp is not null) File.Delete(temp); }
        }
        return results;
    }
}
