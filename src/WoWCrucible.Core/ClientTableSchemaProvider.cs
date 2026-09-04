namespace WoWCrucible.Core;

public sealed record ClientTableSchemaResolution(DbcSchemaResolution Resolution, string SourcePath, string Provider);

/// <summary>
/// Resolves one exact logical schema for WDBC, fixed-layout WDB2, or WDC1.
/// WoWDBDefs is preferred because it carries build ranges, layout hashes, and
/// references; exact WDBX XML remains a fixed-layout fallback.
/// </summary>
public sealed class ClientTableSchemaProvider
{
    private readonly DbcSchemaCatalog? _xml;

    public ClientTableSchemaProvider(int build, string? xmlSchemaPath = null, string? definitionsRoot = null)
    {
        if (build <= 0) throw new ArgumentOutOfRangeException(nameof(build), "A positive client build is required.");
        Build = build;
        XmlSchemaPath = OptionalFile(xmlSchemaPath, "WDBX XML schema");
        DefinitionsRoot = OptionalDirectory(definitionsRoot, "WoWDBDefs definitions root");
        if (XmlSchemaPath is null && DefinitionsRoot is null)
            throw new ArgumentException("Select an exact WDBX XML schema, a WoWDBDefs definitions root, or both.");
        _xml = XmlSchemaPath is null ? null : DbcSchemaCatalog.Load(XmlSchemaPath);
    }

    public int Build { get; }
    public string? XmlSchemaPath { get; }
    public string? DefinitionsRoot { get; }

    public DbcSchemaResolution Resolve(WdbcFile file) => ResolveWithSource(file).Resolution;

    public ClientTableSchemaResolution ResolveWithSource(WdbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var table = file.LogicalTableName;
        if (DefinitionsRoot is not null)
        {
            var definition = Path.Combine(DefinitionsRoot, table + ".dbd");
            if (File.Exists(definition))
            {
                try
                {
                    var resolution = DbdSchemaService.ResolveFile(definition, Build, file);
                    if (resolution.IsExactFor(file))
                        return new(resolution, definition, "WoWDBDefs");
                }
                catch (KeyNotFoundException) when (_xml is not null && file.ContainerKind != ClientTableContainerKind.Wdc1) { }
                catch (InvalidDataException) when (_xml is not null && file.ContainerKind != ClientTableContainerKind.Wdc1) { }
            }
        }

        if (file.ContainerKind == ClientTableContainerKind.Wdc1)
            throw new InvalidDataException($"{table} requires a build/layout-hash-aware WoWDBDefs definition for WDC1.");
        if (_xml is null)
            throw new InvalidDataException($"No exact schema provider resolves {table} for build {Build:N0}.");
        var xmlResolution = _xml.ResolveColumns(table, file.FieldCount);
        if (!xmlResolution.IsExactFor(file))
            throw new InvalidDataException($"The WDBX XML schema does not exactly match {table} ({file.FieldCount:N0} declared fields, {file.RecordSize:N0} record bytes).");
        file.ConfigureWdb2Schema(xmlResolution.Columns);
        return new(xmlResolution, XmlSchemaPath!, "WDBX XML");
    }

    public string RequireDbdPath(string table)
    {
        if (DefinitionsRoot is null) throw new InvalidDataException($"{table} dependency remapping requires WoWDBDefs reference metadata.");
        var path = Path.Combine(DefinitionsRoot, table + ".dbd");
        return File.Exists(path) ? path : throw new FileNotFoundException($"No WoWDBDefs definition exists for {table}.", path);
    }

    private static string? OptionalFile(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "-") return null;
        path = Path.GetFullPath(path);
        return File.Exists(path) ? path : throw new FileNotFoundException($"{label} does not exist.", path);
    }

    private static string? OptionalDirectory(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "-") return null;
        path = Path.GetFullPath(path);
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"{label} does not exist: {path}");
    }
}
