using System.Text.Json;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

// Shared by the editor and headless Explorer exports; neither has a separate schema policy.
internal sealed class DesktopTableSchemaSource(DesktopSettings settings)
{
    private readonly object _gate = new();
    private DbcSchemaCatalog? _catalog;
    private string? _catalogPath;

    public (DbcSchemaResolution Schema, string Source) Resolve(WdbcFile file)
    {
        if (file.ContainerKind is ClientTableContainerKind.Wdb2 or ClientTableContainerKind.Wdc1)
        {
            var definitions = FindDbdDefinitionsPath() ?? throw new DirectoryNotFoundException("Opening WDB2 or WDC1 requires the WoWDBDefs definitions folder. Configure it under DBD schemas & audit.");
            var definition = Path.Combine(definitions, file.LogicalTableName + ".dbd");
            if (!File.Exists(definition)) throw new FileNotFoundException($"No WoWDBDefs definition exists for {file.LogicalTableName}.db2.", definition);
            var build = file.Db2Metadata?.Build ?? 0;
            var resolution = DbdSchemaService.ResolveFile(definition, build, file);
            var identity = file.Wdc1Metadata is { } wdc1 ? $"layout {wdc1.LayoutHash:X8}" : $"build {build}";
            return (resolution, $"{definition} - {identity}");
        }
        lock (_gate)
        {
            var catalog = ResolveCatalog();
            return (catalog.ResolveColumns(file.LogicalTableName, file.FieldCount), _catalogPath ?? "Built-in 12340 definitions");
        }
    }

    public DbcSchemaCatalog ResolveCatalog()
    {
        lock (_gate)
        {
            var path = FindSchemaDefinitionPath();
            if (_catalog is not null && string.Equals(path, _catalogPath, StringComparison.OrdinalIgnoreCase)) return _catalog;
            var catalog = path is null ? DbcSchemaCatalog.CreateBuiltIn12340() : DbcSchemaCatalog.Load(path);
            _catalogPath = path;
            return _catalog = catalog;
        }
    }

    private string? FindDbdDefinitionsPath()
    {
        if (!string.IsNullOrWhiteSpace(settings.DbdDefinitionsPath) && Directory.Exists(settings.DbdDefinitionsPath))
            return Path.GetFullPath(settings.DbdDefinitionsPath);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                foreach (var relative in new[] { Path.Combine("Tools", "WoWDBDefs", "definitions"), Path.Combine("WoWDBDefs", "definitions"), "definitions" })
                {
                    var candidate = Path.Combine(directory.FullName, relative);
                    if (Directory.Exists(candidate)) candidates.Add(candidate);
                }
        var selected = CrucibleWorkspaceLayoutService.SelectBestDbdDefinitionsDirectory(candidates);
        return selected.Length == 0 ? null : selected;
    }

    private string? FindSchemaDefinitionPath()
    {
        if (!string.IsNullOrWhiteSpace(settings.SchemaDefinitionPath) && File.Exists(settings.SchemaDefinitionPath))
            return Path.GetFullPath(settings.SchemaDefinitionPath);
        var settingsPath = CruciblePaths.SettingsFileForRead;
        if (File.Exists(settingsPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("SchemaDefinitionPath", out var configured))
            {
                var path = configured.GetString();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return Path.GetFullPath(path);
            }
        }
        const string fileName = "WotLK 3.3.5 (12340).xml";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            foreach (var relative in new[]
            {
                Path.Combine("Definitions", fileName), Path.Combine("WDBX.Editor", "Definitions", fileName),
                Path.Combine("WDBXEditor", "WDBXEditor", "Definitions", fileName), Path.Combine("WDBX (wow edit)", "Definitions", fileName)
            })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
